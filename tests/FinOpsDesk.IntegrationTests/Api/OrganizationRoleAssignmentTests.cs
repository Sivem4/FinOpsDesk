using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed partial class OrganizationRoleAssignmentTests : IDisposable
{
    private const string OperatorName = "deploy-operator";

    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public OrganizationRoleAssignmentTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = new FinOpsDeskApiFactory(
            fixture.ConnectionString,
            services => services.AddSingleton<IStartupFilter, VerifiedAccessProbeStartupFilter>(),
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = _stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });
    }

    [Fact]
    public async Task Login_grants_no_role_and_each_role_resolves_its_own_set()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Assert.Equal(0, await CountAssignmentsAsync(user.Id));

        Guid organizationId = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        Assert.Empty(await ListActiveRolesAsync(user.Id, organizationId));

        foreach (OrganizationRole role in new[]
        {
            OrganizationRole.Viewer,
            OrganizationRole.Operator,
            OrganizationRole.ReversalApprover,
            OrganizationRole.OrganizationAdmin,
        })
        {
            Guid separateOrganizationId = await CreateOrganizationAsync();
            Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, separateOrganizationId));
            Guid membershipId = await MembershipIdAsync(user.Id, separateOrganizationId);
            Assert.Equal(
                OrganizationRoleMutationStatus.Granted,
                await StoreGrantAsync(membershipId, role));
            IReadOnlyList<OrganizationRole> active = await ListActiveRolesAsync(user.Id, separateOrganizationId);
            Assert.Equal([role], active);
            Assert.Equal(OrganizationRolePermissions.For(role), OrganizationRolePermissions.Union(active));
        }
    }

    [Fact]
    public async Task One_membership_unions_roles_and_two_organizations_stay_independent()
    {
        ApplicationUser user = await SignedInUserAsync();
        Guid north = await CreateOrganizationAsync();
        Guid south = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, north));
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, south));
        Guid northMembership = await MembershipIdAsync(user.Id, north);
        Guid southMembership = await MembershipIdAsync(user.Id, south);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(northMembership, OrganizationRole.Viewer));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(northMembership, OrganizationRole.Operator));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(southMembership, OrganizationRole.ReversalApprover));

        IReadOnlySet<AccessPermission> northPermissions = OrganizationRolePermissions.Union(
            await ListActiveRolesAsync(user.Id, north));
        Assert.Contains(AccessPermission.ImportsCreate, northPermissions);
        Assert.DoesNotContain(AccessPermission.AllocationsReverse, (IEnumerable<AccessPermission>)northPermissions);
        IReadOnlySet<AccessPermission> southPermissions = OrganizationRolePermissions.Union(
            await ListActiveRolesAsync(user.Id, south));
        Assert.Contains(AccessPermission.AllocationsReverse, southPermissions);
        Assert.DoesNotContain(AccessPermission.ImportsCreate, (IEnumerable<AccessPermission>)southPermissions);
        Assert.Empty(await ListActiveRolesAsync(user.Id, await CreateOrganizationAsync()));
    }

    [Fact]
    public async Task Duplicate_grants_keep_one_active_row_and_regrant_keeps_history()
    {
        ApplicationUser user = await SignedInUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        OrganizationRoleMutationStatus[] results = await Task.WhenAll(
            StoreGrantAsync(membershipId, OrganizationRole.Viewer),
            StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        Assert.Contains(OrganizationRoleMutationStatus.Granted, results);
        Assert.Contains(OrganizationRoleMutationStatus.AlreadyActive, results);
        Assert.Equal(1, await CountActiveAsync(membershipId, OrganizationRole.Viewer));

        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(membershipId, OrganizationRole.Viewer));
        Assert.Equal(OrganizationRoleMutationStatus.NotActive, await StoreRevokeAsync(membershipId, OrganizationRole.Viewer));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<OrganizationRoleAssignment> rows = await context.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.MembershipId == membershipId && assignment.Role == OrganizationRole.Viewer)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(assignment => assignment.RevokedAt is null));
        OrganizationRoleAssignment revoked = rows.Single(assignment => assignment.RevokedAt is not null);
        Assert.True(revoked.RevokedAt > revoked.GrantedAt);
        Assert.Equal(RoleActorKind.DeploymentBootstrap, revoked.GrantActorKind);
        Assert.Null(revoked.GrantActorUserId);
        Assert.Equal(OperatorName, revoked.GrantOperatorName);
    }

    [Fact]
    public async Task Two_administrators_cannot_both_be_revoked()
    {
        ApplicationUser first = await SignedInUserAsync();
        ApplicationUser second = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            "second-role-subject",
            null,
            DateTimeOffset.UtcNow);
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.ApplicationUsers.Add(second);
            await context.SaveChangesAsync();
        }

        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(first, organizationId);
        Assert.Equal(
            DeploymentCommandStatus.MembershipGranted,
            await BootstrapAsync(bootstrap => bootstrap.GrantMembershipAsync(
                second.Issuer,
                second.Subject,
                organizationId,
                CancellationToken.None)));
        Guid firstMembership = await MembershipIdAsync(first.Id, organizationId);
        Guid secondMembership = await MembershipIdAsync(second.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(firstMembership, OrganizationRole.OrganizationAdmin));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(secondMembership, OrganizationRole.OrganizationAdmin));

        OrganizationRoleMutationStatus[] results = await Task.WhenAll(
            StoreRevokeAsync(firstMembership, OrganizationRole.OrganizationAdmin),
            StoreRevokeAsync(secondMembership, OrganizationRole.OrganizationAdmin));
        Assert.Contains(OrganizationRoleMutationStatus.Revoked, results);
        Assert.Contains(OrganizationRoleMutationStatus.LastOrganizationAdmin, results);
        Assert.Equal(1, await CountActiveAdministratorsAsync(organizationId));
        Assert.Equal(
            OrganizationRoleMutationStatus.LastOrganizationAdmin,
            await StoreRevokeAsync(
                (await ListActiveAdministratorMembershipAsync(organizationId)),
                OrganizationRole.OrganizationAdmin));
    }

    [Fact]
    public async Task Next_request_sees_a_grant_and_a_revocation_without_financial_access()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        JsonElement empty = await ReadProbeAsync(client);
        Assert.Equal("Active", empty.GetProperty("status").GetString());
        Assert.Equal("Resolved", empty.GetProperty("permissionStatus").GetString());
        Assert.Equal("resolved", empty.GetProperty("permissionReason").GetString());
        Assert.Empty(empty.GetProperty("permissions").EnumerateArray());

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        JsonElement viewer = await ReadProbeAsync(client);
        Assert.Contains(
            viewer.GetProperty("permissions").EnumerateArray(),
            permission => permission.GetString() == AccessPermission.InvoicesRead.Value);
        Assert.DoesNotContain(
            viewer.GetProperty("permissions").EnumerateArray(),
            permission => permission.GetString() == AccessPermission.RolesManage.Value);

        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(membershipId, OrganizationRole.Viewer));
        JsonElement revoked = await ReadProbeAsync(client);
        Assert.Empty(revoked.GetProperty("permissions").EnumerateArray());

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Operator));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.ReversalApprover));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.OrganizationAdmin));
        using JsonDocument session = await ReadSessionAsync(client);
        Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/invoices")).StatusCode);
        Assert.Null(_factory.Services.GetService<IDeploymentRoleBootstrap>());
    }

    [Fact]
    public async Task Inactive_membership_user_organization_or_session_removes_effective_roles()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        await StoreGrantAsync(membershipId, OrganizationRole.Operator);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
            membership.Revoke(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        Assert.Empty(await ListActiveRolesAsync(user.Id, organizationId));
        JsonElement untrustedMembership = await ReadProbeAsync(client);
        Assert.Equal("Active", untrustedMembership.GetProperty("status").GetString());
        Assert.Equal("Resolved", untrustedMembership.GetProperty("permissionStatus").GetString());
        Assert.Equal("no-active-organization", untrustedMembership.GetProperty("permissionReason").GetString());
        Assert.Empty(untrustedMembership.GetProperty("permissions").EnumerateArray());
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
            membership.Restore();
            await context.SaveChangesAsync();
        }

        Assert.Equal([OrganizationRole.Operator], await ListActiveRolesAsync(user.Id, organizationId));
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            Organization organization = await context.Organizations.SingleAsync(candidate => candidate.Id == organizationId);
            organization.Disable();
            await context.SaveChangesAsync();
        }

        Assert.Empty(await ListActiveRolesAsync(user.Id, organizationId));
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            ApplicationUser tracked = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == user.Id);
            tracked.Disable();
            await context.SaveChangesAsync();
        }

        Assert.Empty(await ListActiveRolesAsync(user.Id, organizationId));
        JsonElement signedOut = await ReadProbeAsync(client);
        Assert.Equal("Rejected", signedOut.GetProperty("status").GetString());
        Assert.Equal("Untrusted", signedOut.GetProperty("permissionStatus").GetString());
        Assert.Empty(signedOut.GetProperty("permissions").EnumerateArray());
    }

    [Fact]
    public async Task Invalid_role_and_sql_outage_do_not_grant_permissions()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        await Assert.ThrowsAsync<SqlException>(() => InsertRoleAsync(membershipId, 9));

        await ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments NOCHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        try
        {
            await InsertRoleAsync(membershipId, 9);
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
            HttpResponseMessage inconsistent = await client.GetAsync(VerifiedAccessProbeMiddleware.Path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, inconsistent.StatusCode);
            Assert.False(ClearsSessionCookie(inconsistent));
            using JsonDocument inconsistentBody = JsonDocument.Parse(await inconsistent.Content.ReadAsStringAsync());
            Assert.Equal("Active", inconsistentBody.RootElement.GetProperty("status").GetString());
            Assert.Equal("Unavailable", inconsistentBody.RootElement.GetProperty("permissionStatus").GetString());
            Assert.Equal("inconsistent-role", inconsistentBody.RootElement.GetProperty("permissionReason").GetString());
            Assert.Empty(inconsistentBody.RootElement.GetProperty("permissions").EnumerateArray());
        }
        finally
        {
            await ExecuteAsync("DELETE FROM access.OrganizationRoleAssignments WHERE Role = 9");
            await ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments WITH CHECK CHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        }

        using JsonDocument restored = await ReadSessionAsync(client);
        Assert.True(restored.RootElement.GetProperty("authenticated").GetBoolean());
        await StoreGrantAsync(membershipId, OrganizationRole.Viewer);
        await RenameAsync("OrganizationRoleAssignments", "OrganizationRoleAssignments_offline");
        try
        {
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
            HttpResponseMessage outage = await client.GetAsync(VerifiedAccessProbeMiddleware.Path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
            Assert.False(ClearsSessionCookie(outage));
            using JsonDocument outageBody = JsonDocument.Parse(await outage.Content.ReadAsStringAsync());
            Assert.Equal("Active", outageBody.RootElement.GetProperty("status").GetString());
            Assert.Equal("Unavailable", outageBody.RootElement.GetProperty("permissionStatus").GetString());
            Assert.Equal("database", outageBody.RootElement.GetProperty("permissionReason").GetString());
            Assert.Empty(outageBody.RootElement.GetProperty("permissions").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/invoices")).StatusCode);
        }
        finally
        {
            await RenameAsync("OrganizationRoleAssignments_offline", "OrganizationRoleAssignments");
        }
    }

    [Fact]
    public async Task Administration_requires_roles_manage_inside_the_same_organization()
    {
        ApplicationUser actor = await SignedInUserAsync();
        ApplicationUser target = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            "target-role-subject",
            null,
            DateTimeOffset.UtcNow);
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.ApplicationUsers.Add(target);
            await context.SaveChangesAsync();
        }

        Guid north = await CreateOrganizationAsync();
        Guid south = await CreateOrganizationAsync();
        await GrantMembershipAsync(actor, north);
        await BootstrapAsync(bootstrap => bootstrap.GrantMembershipAsync(target.Issuer, target.Subject, north, CancellationToken.None));
        await BootstrapAsync(bootstrap => bootstrap.GrantMembershipAsync(target.Issuer, target.Subject, south, CancellationToken.None));
        Guid actorMembership = await MembershipIdAsync(actor.Id, north);
        Guid targetNorth = await MembershipIdAsync(target.Id, north);
        Guid targetSouth = await MembershipIdAsync(target.Id, south);
        await StoreGrantAsync(actorMembership, OrganizationRole.Viewer);
        Assert.Equal(
            OrganizationRoleMutationStatus.Forbidden,
            await AdministerGrantAsync(actor.Id, north, targetNorth, OrganizationRole.Operator));

        await StoreGrantAsync(actorMembership, OrganizationRole.OrganizationAdmin);
        Assert.Equal(
            OrganizationRoleMutationStatus.OutsideOrganization,
            await AdministerGrantAsync(actor.Id, north, targetSouth, OrganizationRole.Viewer));
        Assert.Equal(
            OrganizationRoleMutationStatus.Forbidden,
            await AdministerGrantAsync(actor.Id, south, targetSouth, OrganizationRole.Viewer));
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await AdministerGrantAsync(actor.Id, north, targetNorth, OrganizationRole.Viewer));
        OrganizationRoleAssignment granted = await SingleActiveAsync(targetNorth, OrganizationRole.Viewer);
        Assert.Equal(RoleActorKind.ApplicationUser, granted.GrantActorKind);
        Assert.Equal(actor.Id, granted.GrantActorUserId);
        Assert.Null(granted.GrantOperatorName);
    }

    [Fact]
    public async Task Bootstrap_can_assign_the_first_administrator()
    {
        ApplicationUser user = await SignedInUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.OrganizationAlreadyExists, await CreateOrganizationAgainAsync(organizationId));
        await GrantMembershipAsync(user, organizationId);
        Assert.Equal(
            DeploymentCommandStatus.UserNotFound,
            await BootstrapAsync(bootstrap => bootstrap.GrantRoleAsync(
                _stub.Authority,
                "missing-subject",
                organizationId,
                OrganizationRole.OrganizationAdmin,
                OperatorName,
                CancellationToken.None)));
        Assert.Equal(
            DeploymentCommandStatus.RoleGranted,
            await BootstrapAsync(bootstrap => bootstrap.GrantRoleAsync(
                user.Issuer,
                user.Subject,
                organizationId,
                OrganizationRole.OrganizationAdmin,
                OperatorName,
                CancellationToken.None)));
        Assert.Equal(0, await CountActiveAdministratorsAsync(Guid.NewGuid()));
        Assert.Equal(1, await CountActiveAdministratorsAsync(organizationId));
    }

    [Fact]
    public async Task Role_constraints_do_not_change_reconciliation_history()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE name = N'UX_OrganizationRoleAssignments_MembershipId_Role_Active'
              AND has_filter = 1
            """));
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT delete_referential_action
            FROM sys.foreign_keys
            WHERE name = N'FK_OrganizationRoleAssignments_OrganizationMemberships_MembershipId'
            """));
        Assert.InRange(await ScalarAsync(connection, "SELECT COUNT(*) FROM reconciliation.__EFMigrationsHistory"), 1, int.MaxValue);
        Assert.DoesNotContain(
            typeof(Program).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "FinOpsDesk.AccessBootstrap");
    }

    [Fact]
    public async Task Development_jwt_does_not_read_or_write_role_assignments()
    {
        int before = await CountAllAssignmentsAsync();
        using (FinOpsDeskApiFactory jwt = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt))
        {
            using HttpClient client = jwt.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/invoices")).StatusCode);
        }

        Assert.Equal(before, await CountAllAssignmentsAsync());
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task<ApplicationUser> SignedInUserAsync()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        return await RequireUserAsync();
    }

    private async Task<ApplicationUser> RequireUserAsync()
    {
        byte[] identityKey = ExternalIdentityKey.Compute(_stub.Authority, OidcProtocolStub.Subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser? user = (await context.ApplicationUsers
            .AsNoTracking()
            .Where(candidate => candidate.IdentityKey == identityKey)
            .ToListAsync())
            .SingleOrDefault(candidate => ExternalIdentityKey.Same(
                candidate.Issuer,
                candidate.Subject,
                _stub.Authority,
                OidcProtocolStub.Subject));
        Assert.NotNull(user);
        return user;
    }

    private async Task<Guid> CreateOrganizationAsync()
    {
        Guid organizationId = Guid.NewGuid();
        Assert.Equal(
            DeploymentCommandStatus.OrganizationCreated,
            await BootstrapAsync(bootstrap => bootstrap.CreateOrganizationAsync(
                organizationId,
                "North Ledger",
                CancellationToken.None)));
        return organizationId;
    }

    private Task<DeploymentCommandStatus> CreateOrganizationAgainAsync(Guid organizationId)
    {
        return BootstrapAsync(bootstrap => bootstrap.CreateOrganizationAsync(
            organizationId,
            "North Ledger",
            CancellationToken.None));
    }

    private Task<DeploymentCommandStatus> GrantMembershipAsync(ApplicationUser user, Guid organizationId)
    {
        return BootstrapAsync(bootstrap => bootstrap.GrantMembershipAsync(
            user.Issuer,
            user.Subject,
            organizationId,
            CancellationToken.None));
    }

    private async Task<DeploymentCommandStatus> BootstrapAsync(
        Func<DeploymentRoleBootstrap, Task<DeploymentCommandStatus>> action)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentRoleBootstrap bootstrap = new(context, new OrganizationRoleStore(context), TimeProvider.System);
        return await action(bootstrap);
    }

    private async Task<OrganizationRoleMutationStatus> StoreGrantAsync(
        Guid membershipId,
        OrganizationRole role,
        string operatorName = OperatorName)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).GrantAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow,
            RoleActor.DeploymentBootstrap(operatorName),
            CancellationToken.None);
    }

    private async Task<OrganizationRoleMutationStatus> StoreRevokeAsync(
        Guid membershipId,
        OrganizationRole role,
        string operatorName = OperatorName)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).RevokeAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow.AddMinutes(1),
            RoleActor.DeploymentBootstrap(operatorName),
            CancellationToken.None);
    }

    private async Task<OrganizationRoleMutationStatus> AdministerGrantAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role,
        IOrganizationRoleMutationSync? sync = null)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationRoleStore store = new(context, sync);
        OrganizationRoleAdministration administration = new(store, TimeProvider.System);
        return await administration.GrantAsync(actorUserId, organizationId, membershipId, role, CancellationToken.None);
    }

    private async Task<OrganizationRoleMutationStatus> AdministerRevokeAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] rowVersion)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationRoleStore store = new(context);
        OrganizationRoleAdministration administration = new(store, TimeProvider.System);
        return await administration.RevokeAsync(
            actorUserId,
            organizationId,
            assignmentId,
            rowVersion,
            CancellationToken.None);
    }

    private async Task<(Guid Id, byte[] RowVersion)> ActiveIdentityAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        var row = await context.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.MembershipId == membershipId
                && assignment.Role == role
                && assignment.RevokedAt == null)
            .Select(assignment => new
            {
                assignment.Id,
                Version = EF.Property<byte[]>(assignment, OrganizationRoleAssignmentConfiguration.RowVersionPropertyName),
            })
            .SingleAsync();
        return (row.Id, row.Version.ToArray());
    }

    private async Task<ApplicationUser> CreateUserAsync(string subject)
    {
        ApplicationUser user = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            subject,
            null,
            DateTimeOffset.UtcNow);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        context.ApplicationUsers.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> MembershipIdAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId && membership.OrganizationId == organizationId)
            .Select(membership => membership.Id)
            .SingleAsync();
    }

    private async Task<IReadOnlyList<OrganizationRole>> ListActiveRolesAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).ListActiveAsync(userId, organizationId, CancellationToken.None);
    }

    private async Task<int> CountAssignmentsAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await (
            from assignment in context.OrganizationRoleAssignments
            join membership in context.OrganizationMemberships on assignment.MembershipId equals membership.Id
            where membership.UserId == userId
            select assignment).CountAsync();
    }

    private async Task<int> CountAllAssignmentsAsync()
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationRoleAssignments.CountAsync();
    }

    private async Task<int> CountActiveAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationRoleAssignments.CountAsync(assignment =>
            assignment.MembershipId == membershipId
            && assignment.Role == role
            && assignment.RevokedAt == null);
    }

    private async Task<int> CountActiveAdministratorsAsync(Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await (
            from assignment in context.OrganizationRoleAssignments
            join membership in context.OrganizationMemberships on assignment.MembershipId equals membership.Id
            where membership.OrganizationId == organizationId
                && assignment.Role == OrganizationRole.OrganizationAdmin
                && assignment.RevokedAt == null
            select assignment).CountAsync();
    }

    private async Task<Guid> ListActiveAdministratorMembershipAsync(Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await (
            from assignment in context.OrganizationRoleAssignments
            join membership in context.OrganizationMemberships on assignment.MembershipId equals membership.Id
            where membership.OrganizationId == organizationId
                && assignment.Role == OrganizationRole.OrganizationAdmin
                && assignment.RevokedAt == null
            select assignment.MembershipId).SingleAsync();
    }

    private async Task<OrganizationRoleAssignment> SingleActiveAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationRoleAssignments.AsNoTracking().SingleAsync(assignment =>
            assignment.MembershipId == membershipId
            && assignment.Role == role
            && assignment.RevokedAt == null);
    }

    private async Task InsertRoleAsync(Guid membershipId, byte role)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            """
            INSERT INTO access.OrganizationRoleAssignments
                (Id, MembershipId, Role, GrantedAt, GrantActorKind, GrantActorUserId, GrantOperatorName,
                 RevokedAt, RevokeActorKind, RevokeActorUserId, RevokeOperatorName)
            VALUES
                (@id, @membershipId, @role, SYSDATETIMEOFFSET(), 2, NULL, N'deploy-operator',
                 NULL, NULL, NULL, NULL)
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@membershipId", SqlDbType.UniqueIdentifier) { Value = membershipId });
        command.Parameters.Add(new SqlParameter("@role", SqlDbType.TinyInt) { Value = role });
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RenameAsync(string from, string to)
    {
        if (from is not ("OrganizationRoleAssignments" or "OrganizationRoleAssignments_offline")
            || to is not ("OrganizationRoleAssignments" or "OrganizationRoleAssignments_offline"))
        {
            throw new InvalidOperationException("Unexpected access table rename.");
        }

        await ExecuteAsync("EXEC sp_rename N'access." + from + "', N'" + to + "';");
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = new(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, OidcProtocolStub stub)
    {
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        HttpResponseMessage loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        using HttpClient provider = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage authorize = await provider.GetAsync(loginResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        HttpResponseMessage callback = await client.GetAsync(authorize.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return callback;
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        using JsonDocument document = await ReadSessionAsync(client);
        return document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");
    }

    private static async Task<JsonDocument> ReadSessionAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadProbeAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(VerifiedAccessProbeMiddleware.Path);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SelectAsync(HttpClient client, string csrf, Guid organizationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization")
        {
            Content = new StringContent(
                "{\"organizationId\":\"" + organizationId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static bool ClearsSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return false;
        }

        foreach (string value in values)
        {
            if (value.StartsWith("__Host-finopsdesk-session=", StringComparison.Ordinal)
                && (value.Contains("expires=", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}