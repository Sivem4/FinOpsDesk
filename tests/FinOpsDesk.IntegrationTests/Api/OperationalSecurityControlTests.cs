using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class OperationalSecurityControlTests : IDisposable
{
    private const string OperatorName = "phase-7d5-operator";

    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _cookieFactory;

    public OperationalSecurityControlTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _cookieFactory = new FinOpsDeskApiFactory(
            fixture.ConnectionString,
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
    public async Task Applied_and_repeated_commands_record_distinct_audit_events()
    {
        SteppingTime time = new();
        ApplicationUser user = await ProvisionAsync("user-a");
        ApplicationUser other = await ProvisionAsync("user-b");
        Guid organizationA = await CreateOrganizationAsync("A");
        Guid organizationB = await CreateOrganizationAsync("B");
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationA));
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationB));
        Guid membershipA = await MembershipIdAsync(user.Id, organizationA);
        Guid membershipB = await MembershipIdAsync(user.Id, organizationB);
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await GrantRoleAsync(membershipA, OrganizationRole.Operator));
        Guid stamp = await ReadStampAsync(user.Id);

        Assert.Equal(DeploymentCommandStatus.MembershipRevoked, await RevokeMembershipAsync(user, organizationA, time));
        DateTimeOffset revokedAt = await ReadRevokedAtAsync(membershipA);
        Assert.Equal(DeploymentCommandStatus.MembershipAlreadyRevoked, await RevokeMembershipAsync(user, organizationA, time));
        Assert.Equal(revokedAt, await ReadRevokedAtAsync(membershipA));
        Assert.Equal(OrganizationMembershipStatus.Active, await ReadMembershipStatusAsync(membershipB));
        Assert.Equal(1, await CountActiveRolesAsync(membershipA));

        Assert.Equal(DeploymentCommandStatus.OrganizationDisabled, await DisableOrganizationAsync(organizationA, time));
        Assert.Equal(DeploymentCommandStatus.OrganizationAlreadyDisabled, await DisableOrganizationAsync(organizationA, time));
        Assert.Equal(OrganizationStatus.Active, await ReadOrganizationStatusAsync(organizationB));
        IReadOnlyList<OrganizationChoice> choices = await ListActiveAsync(user);
        Assert.DoesNotContain(choices, choice => choice.OrganizationId == organizationA);
        Assert.Contains(choices, choice => choice.OrganizationId == organizationB);

        Assert.Equal(DeploymentCommandStatus.UserDisabled, await DisableUserAsync(user, time));
        Guid rotated = await ReadStampAsync(user.Id);
        Assert.NotEqual(stamp, rotated);
        Assert.Equal(DeploymentCommandStatus.UserAlreadyDisabled, await DisableUserAsync(user, time));
        Assert.Equal(rotated, await ReadStampAsync(user.Id));
        Assert.Equal(ApplicationUserStatus.Active, await ReadUserStatusAsync(other.Id));

        List<OperationalSecurityEvent> events = await EventsForUserAsync(user.Id);
        Assert.Equal(4, events.Count);
        Assert.Equal(
            [OperationalSecurityOutcome.Applied, OperationalSecurityOutcome.AlreadyApplied],
            events.Where(item => item.Operation == OperationalSecurityOperation.DisableUser).Select(item => item.Outcome).ToArray());
        Assert.All(events.Where(item => item.Operation == OperationalSecurityOperation.DisableUser), item =>
        {
            Assert.Equal(user.Id, item.TargetUserId);
            Assert.Null(item.TargetOrganizationId);
            Assert.Null(item.TargetMembershipId);
            Assert.Equal(OperatorName, item.DeclaredOperator);
            Assert.Equal(TimeSpan.Zero, item.OccurredAt.Offset);
        });
        OperationalSecurityEvent revocation = Assert.Single(
            events,
            item => item.Operation == OperationalSecurityOperation.RevokeMembership && item.Outcome == OperationalSecurityOutcome.Applied);
        Assert.Equal(organizationA, revocation.TargetOrganizationId);
        Assert.Equal(membershipA, revocation.TargetMembershipId);
        Assert.Equal(OperatorName, revocation.DeclaredOperator);
        Assert.Empty(await EventsForUserAsync(other.Id));
        List<OperationalSecurityEvent> organizationEvents = await EventsForOrganizationAsync(organizationA);
        Assert.Contains(organizationEvents, item => item.Operation == OperationalSecurityOperation.DisableOrganization && item.Outcome == OperationalSecurityOutcome.Applied);
        Assert.Contains(organizationEvents, item => item.Operation == OperationalSecurityOperation.DisableOrganization && item.Outcome == OperationalSecurityOutcome.AlreadyApplied);
        Assert.Empty(await EventsForOrganizationAsync(organizationB));
    }

    [Fact]
    public async Task Missing_targets_and_operator_do_not_write_success_events()
    {
        ApplicationUser user = await ProvisionAsync("missing-target");
        Guid organizationId = await CreateOrganizationAsync("present");
        int before = await CountEventsAsync();

        Assert.Equal(
            DeploymentCommandStatus.InvalidOperator,
            await DisableUserAsync(user, new SteppingTime(), " ", NoDeploymentEmergencySync.Instance));
        Assert.Equal(DeploymentCommandStatus.UserNotFound, await DisableUserAsync(user.Issuer, "missing-subject", new SteppingTime()));
        Assert.Equal(DeploymentCommandStatus.UserNotFound, await DisableUserAsync("", "subject", new SteppingTime()));
        Assert.Equal(DeploymentCommandStatus.OrganizationNotFound, await DisableOrganizationAsync(Guid.NewGuid(), new SteppingTime()));
        Assert.Equal(DeploymentCommandStatus.MembershipNotFound, await RevokeMembershipAsync(user, organizationId, new SteppingTime()));
        Assert.Equal(DeploymentCommandStatus.OrganizationNotFound, await RevokeMembershipAsync(user, Guid.NewGuid(), new SteppingTime()));

        Assert.Equal(before, await CountEventsAsync());
        Assert.Equal(ApplicationUserStatus.Active, await ReadUserStatusAsync(user.Id));
        Assert.Equal(OrganizationStatus.Active, await ReadOrganizationStatusAsync(organizationId));
    }

    [Fact]
    public async Task Commit_failure_rolls_back_the_disable_and_writes_no_event()
    {
        ApplicationUser user = await ProvisionAsync("rollback");
        Guid stamp = await ReadStampAsync(user.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DisableUserAsync(user, new SteppingTime(), OperatorName, new ThrowingSync()));

        Assert.Equal(ApplicationUserStatus.Active, await ReadUserStatusAsync(user.Id));
        Assert.Equal(stamp, await ReadStampAsync(user.Id));
        Assert.Empty(await EventsForUserAsync(user.Id));
    }

    [Fact]
    public async Task Lock_timeout_does_not_revoke_or_audit()
    {
        ApplicationUser user = await ProvisionAsync("locked");
        Guid organizationId = await CreateOrganizationAsync("locked-org");
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        await using SqlConnection holder = new(_fixture.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        await new OrganizationFinancialAuthorization().AcquireExclusiveAsync(holder, held, organizationId, CancellationToken.None);

        DeploymentCommandStatus status = await RevokeMembershipAsync(
            user,
            organizationId,
            new SteppingTime(),
            OperatorName,
            null,
            new SqlApplicationLockPolicy { WaitTimeoutMilliseconds = 200 });

        Assert.Equal(DeploymentCommandStatus.LockNotAcquired, status);
        Assert.Equal(OrganizationMembershipStatus.Active, await ReadMembershipStatusAsync(await MembershipIdAsync(user.Id, organizationId)));
        Assert.Empty(await EventsForOrganizationAsync(organizationId));
    }

    [Fact]
    public async Task Existing_role_commands_still_grant_and_revoke()
    {
        ApplicationUser user = await ProvisionAsync("roles");
        Guid organizationId = await CreateOrganizationAsync("roles");
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        Assert.Equal(
            DeploymentCommandStatus.RoleGranted,
            await BootstrapAsync(bootstrap => bootstrap.GrantRoleAsync(
                user.Issuer,
                user.Subject,
                organizationId,
                OrganizationRole.Viewer,
                OperatorName,
                CancellationToken.None)));
        Assert.Equal(
            DeploymentCommandStatus.RoleRevoked,
            await BootstrapAsync(bootstrap => bootstrap.RevokeRoleAsync(
                user.Issuer,
                user.Subject,
                organizationId,
                OrganizationRole.Viewer,
                OperatorName,
                CancellationToken.None)));
    }

    [Fact]
    public async Task Current_grant_does_not_restore_a_revoked_membership()
    {
        ApplicationUser user = await ProvisionAsync("restore");
        Guid organizationId = await CreateOrganizationAsync("restore");
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await GrantRoleAsync(membershipId, OrganizationRole.Operator));
        Assert.Equal(DeploymentCommandStatus.MembershipRevoked, await RevokeMembershipAsync(user, organizationId, new SteppingTime()));

        Assert.Equal(DeploymentCommandStatus.MembershipInactive, await GrantMembershipAsync(user, organizationId));
        Assert.Equal(OrganizationMembershipStatus.Revoked, await ReadMembershipStatusAsync(membershipId));
        Assert.Equal(1, await CountActiveRolesAsync(membershipId));
    }

    [Fact]
    public async Task Revoked_membership_and_disabled_organization_leave_the_other_organization()
    {
        using HttpClient client = _cookieFactory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client);
        string? sessionCookie = SessionCookie(callback);
        Assert.False(string.IsNullOrWhiteSpace(sessionCookie));
        callback.Dispose();
        ApplicationUser user = await RequireSignedInUserAsync();
        Guid organizationA = await CreateOrganizationAsync("cookie-a");
        Guid organizationB = await CreateOrganizationAsync("cookie-b");
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationA));
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationB));
        string csrf = await ReadCsrfAsync(client);

        Assert.Equal(DeploymentCommandStatus.MembershipRevoked, await RevokeMembershipAsync(user, organizationA, new SteppingTime()));
        Assert.Equal(HttpStatusCode.Forbidden, (await SelectAsync(client, csrf, organizationA)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationB)).StatusCode);

        Assert.Equal(DeploymentCommandStatus.OrganizationDisabled, await DisableOrganizationAsync(organizationA, new SteppingTime()));
        using JsonDocument session = await ReadSessionAsync(client);
        JsonElement organizations = session.RootElement.GetProperty("organizations");
        List<Guid> ids = organizations.EnumerateArray().Select(item => item.GetProperty("organizationId").GetGuid()).ToList();
        Assert.DoesNotContain(organizationA, ids);
        Assert.Contains(organizationB, ids);
        Assert.Equal(organizationB, session.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());

        try
        {
            Assert.Equal(DeploymentCommandStatus.UserDisabled, await DisableUserAsync(user, new SteppingTime()));
            using JsonDocument rejected = await ReadSessionAsync(client);
            Assert.False(rejected.RootElement.GetProperty("authenticated").GetBoolean());
            using HttpClient replay = ClientWithSession(sessionCookie!);
            using JsonDocument replayed = await ReadSessionAsync(replay);
            Assert.False(replayed.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await RestoreSharedUserAsync(user.Id);
        }

        using HttpClient afterRestore = ClientWithSession(sessionCookie!);
        using JsonDocument stillRejected = await ReadSessionAsync(afterRestore);
        Assert.False(stillRejected.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Development_jwt_host_does_not_register_emergency_controls_or_enable_writes()
    {
        using FinOpsDeskApiFactory jwtFactory = new(_fixture.ConnectionString);
        AuthenticationCompositionState state = jwtFactory.Services.GetRequiredService<AuthenticationCompositionState>();
        Assert.False(state.UsesProductionCookie);
        Assert.False(state.ProductionFinancialWritesEnabled);
        Assert.Null(jwtFactory.Services.GetService<IDeploymentEmergencyControls>());
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string appsettings = await File.ReadAllTextAsync(Path.Combine(repo, "src", "backend", "FinOpsDesk.Api", "appsettings.json"));
        Assert.DoesNotContain("FinancialWriteAuthorization", appsettings, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialReadAuthorization", appsettings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_disable_user_reports_applied_then_already_applied()
    {
        ApplicationUser user = await ProvisionAsync("cli");
        ProcessResult first = await RunConsoleAsync(
        [
            "disable-user",
            "--issuer",
            user.Issuer,
            "--subject",
            user.Subject,
            "--operator",
            OperatorName,
        ]);
        ProcessResult second = await RunConsoleAsync(
        [
            "disable-user",
            "--issuer",
            user.Issuer,
            "--subject",
            user.Subject,
            "--operator",
            OperatorName,
        ]);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("user-disabled", first.Token);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("user-already-disabled", second.Token);
        Assert.DoesNotContain("Password=", first.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server=", first.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ApplicationUserStatus.Disabled, await ReadUserStatusAsync(user.Id));
    }

    public void Dispose()
    {
        _cookieFactory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task<ApplicationUser> ProvisionAsync(string subject)
    {
        string issuer = "https://issuer.example/" + subject;
        await using AccessDbContext context = _fixture.CreateAccessContext();
        IExternalIdentityProvisioner provisioner = new ExternalIdentityProvisioner(context, TimeProvider.System);
        ExternalIdentityProvision provision = await provisioner.ProvisionAsync(issuer, subject, null, CancellationToken.None);
        return await context.ApplicationUsers.AsNoTracking().SingleAsync(user => user.Id == provision.UserId);
    }

    private async Task<ApplicationUser> RequireSignedInUserAsync()
    {
        byte[] identityKey = ExternalIdentityKey.Compute(_stub.Authority, OidcProtocolStub.Subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser? user = (await context.ApplicationUsers.AsNoTracking().Where(candidate => candidate.IdentityKey == identityKey).ToListAsync())
            .SingleOrDefault(candidate => ExternalIdentityKey.Same(candidate.Issuer, candidate.Subject, _stub.Authority, OidcProtocolStub.Subject));
        Assert.NotNull(user);
        return user;
    }

    private Task<DeploymentCommandStatus> DisableUserAsync(ApplicationUser user, TimeProvider time)
    {
        return DisableUserAsync(user.Issuer, user.Subject, time);
    }

    private Task<DeploymentCommandStatus> DisableUserAsync(string issuer, string subject, TimeProvider time)
    {
        return DisableUserAsync(issuer, subject, time, OperatorName, null, null);
    }

    private async Task<DeploymentCommandStatus> DisableUserAsync(
        ApplicationUser user,
        TimeProvider time,
        string operatorName,
        IDeploymentEmergencySync sync)
    {
        return await DisableUserAsync(user.Issuer, user.Subject, time, operatorName, sync, null);
    }

    private async Task<DeploymentCommandStatus> DisableUserAsync(
        string issuer,
        string subject,
        TimeProvider time,
        string operatorName,
        IDeploymentEmergencySync? sync,
        SqlApplicationLockPolicy? policy)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentEmergencyControls controls = new(
            context,
            new OrganizationFinancialAuthorization(policy),
            sync,
            time);
        return await controls.DisableUserAsync(issuer, subject, operatorName, CancellationToken.None);
    }

    private Task<DeploymentCommandStatus> RevokeMembershipAsync(ApplicationUser user, Guid organizationId, TimeProvider time)
    {
        return RevokeMembershipAsync(user, organizationId, time, OperatorName, null, null);
    }

    private async Task<DeploymentCommandStatus> RevokeMembershipAsync(
        ApplicationUser user,
        Guid organizationId,
        TimeProvider time,
        string operatorName,
        IDeploymentEmergencySync? sync,
        SqlApplicationLockPolicy? policy)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentEmergencyControls controls = new(
            context,
            new OrganizationFinancialAuthorization(policy),
            sync,
            time);
        return await controls.RevokeMembershipAsync(user.Issuer, user.Subject, organizationId, operatorName, CancellationToken.None);
    }

    private async Task<DeploymentCommandStatus> DisableOrganizationAsync(Guid organizationId, TimeProvider time)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentEmergencyControls controls = new(context, new OrganizationFinancialAuthorization(), null, time);
        return await controls.DisableOrganizationAsync(organizationId, OperatorName, CancellationToken.None);
    }

    private async Task<Guid> CreateOrganizationAsync(string name)
    {
        Guid organizationId = Guid.NewGuid();
        Assert.Equal(
            DeploymentCommandStatus.OrganizationCreated,
            await BootstrapAsync(bootstrap => bootstrap.CreateOrganizationAsync(organizationId, "Org " + name + " " + organizationId.ToString("N"), CancellationToken.None)));
        return organizationId;
    }

    private Task<DeploymentCommandStatus> GrantMembershipAsync(ApplicationUser user, Guid organizationId)
    {
        return BootstrapAsync(bootstrap => bootstrap.GrantMembershipAsync(user.Issuer, user.Subject, organizationId, CancellationToken.None));
    }

    private async Task<DeploymentCommandStatus> BootstrapAsync(Func<DeploymentRoleBootstrap, Task<DeploymentCommandStatus>> action)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await action(new DeploymentRoleBootstrap(context, new OrganizationRoleStore(context), TimeProvider.System));
    }

    private async Task<OrganizationRoleMutationStatus> GrantRoleAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).GrantAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow,
            RoleActor.DeploymentBootstrap(OperatorName),
            CancellationToken.None);
    }

    private async Task<Guid> MembershipIdAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId && membership.OrganizationId == organizationId)
            .Select(membership => membership.Id)
            .SingleAsync();
    }

    private async Task RestoreSharedUserAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser user = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == userId);
        user.Restore();
        await context.SaveChangesAsync();
    }

    private async Task<Guid> ReadStampAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.ApplicationUsers.Where(user => user.Id == userId).Select(user => user.SessionStamp).SingleAsync();
    }

    private async Task<ApplicationUserStatus> ReadUserStatusAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.ApplicationUsers.Where(user => user.Id == userId).Select(user => user.Status).SingleAsync();
    }

    private async Task<OrganizationMembershipStatus> ReadMembershipStatusAsync(Guid membershipId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationMemberships.Where(membership => membership.Id == membershipId).Select(membership => membership.Status).SingleAsync();
    }

    private async Task<DateTimeOffset> ReadRevokedAtAsync(Guid membershipId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationMemberships.Where(membership => membership.Id == membershipId).Select(membership => membership.RevokedAt).SingleAsync()
            ?? throw new InvalidOperationException("RevokedAt was missing.");
    }

    private async Task<OrganizationStatus> ReadOrganizationStatusAsync(Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.Organizations.Where(organization => organization.Id == organizationId).Select(organization => organization.Status).SingleAsync();
    }

    private async Task<int> CountActiveRolesAsync(Guid membershipId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationRoleAssignments.CountAsync(assignment => assignment.MembershipId == membershipId && assignment.RevokedAt == null);
    }

    private async Task<int> CountEventsAsync()
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OperationalSecurityEvents.CountAsync();
    }

    private async Task<List<OperationalSecurityEvent>> EventsForUserAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OperationalSecurityEvents.AsNoTracking()
            .Where(securityEvent => securityEvent.TargetUserId == userId)
            .OrderBy(securityEvent => securityEvent.OccurredAt)
            .ToListAsync();
    }

    private async Task<List<OperationalSecurityEvent>> EventsForOrganizationAsync(Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OperationalSecurityEvents.AsNoTracking()
            .Where(securityEvent => securityEvent.TargetOrganizationId == organizationId)
            .OrderBy(securityEvent => securityEvent.OccurredAt)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(ApplicationUser user)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationMembershipReader(context).ListActiveAsync(user.Issuer, user.Subject, CancellationToken.None);
    }

    private async Task<ProcessResult> RunConsoleAsync(string[] args)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        string configuration = directory.Parent?.Name ?? string.Empty;
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string dll = Path.Combine(repo, "src", "backend", "FinOpsDesk.AccessBootstrap", "bin", configuration, "net10.0", "FinOpsDesk.AccessBootstrap.dll");
        Assert.True(File.Exists(dll), "Bootstrap executable was not built.");
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["ConnectionStrings__FinOpsDesk"] = _fixture.ConnectionString;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Bootstrap process did not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string token = output.Trim();
        const string prefix = "result=";
        if (token.StartsWith(prefix, StringComparison.Ordinal))
        {
            token = token[prefix.Length..];
        }

        return new ProcessResult(process.ExitCode, token, error);
    }

    private async Task<HttpResponseMessage> SignInAsync(HttpClient client)
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

    private async Task<HttpResponseMessage> SelectAsync(HttpClient client, string csrf, Guid organizationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization");
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        request.Content = new StringContent(
            "{\"organizationId\":\"" + organizationId.ToString("D") + "\"}",
            Encoding.UTF8,
            "application/json");
        return await client.SendAsync(request);
    }

    private HttpClient ClientWithSession(string sessionCookie)
    {
        HttpClient client = _cookieFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", "__Host-finopsdesk-session=" + sessionCookie);
        return client;
    }

    private static string? SessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        const string Prefix = "__Host-finopsdesk-session=";
        foreach (string value in values)
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int end = value.IndexOf(';');
            return end < 0 ? value[Prefix.Length..] : value[Prefix.Length..end];
        }

        return null;
    }

    private sealed class SteppingTime : TimeProvider
    {
        private DateTimeOffset _current = new(2026, 11, 3, 21, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset value = _current;
            _current = _current.AddMinutes(1);
            return value;
        }
    }

    private sealed class ThrowingSync : IDeploymentEmergencySync
    {
        public Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task BeforeCommitAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("audit persistence failed");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Token, string StandardError);
}