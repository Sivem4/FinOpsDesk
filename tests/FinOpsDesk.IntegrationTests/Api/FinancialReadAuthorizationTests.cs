using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialReadAuthorizationTests : IDisposable
{
    private const string OperatorName = "deploy-operator";

    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public FinancialReadAuthorizationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = CreateFactory(financialReadAuthorizationEnabled: true);
        using HttpClient warmup = _factory.CreateClient();
    }

    [Fact]
    public async Task Activated_viewer_reads_are_scoped_and_audit_follows_the_role()
    {
        AuthenticationCompositionState state = _factory.Services.GetRequiredService<AuthenticationCompositionState>();
        Assert.True(state.UsesProductionCookie);
        Assert.False(state.ProductionFinancialAccessFailClosed);

        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        FinancialPair north = await CreateOrganizationWithFinancialDataAsync(user, "north");
        FinancialPair south = await CreateOrganizationWithFinancialDataAsync(user, "south");
        Guid northMembership = await MembershipIdAsync(user.Id, north.OrganizationId);
        Guid southMembership = await MembershipIdAsync(user.Id, south.OrganizationId);
        string csrf = await ReadCsrfAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, north.OrganizationId)).StatusCode);
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(northMembership, OrganizationRole.Viewer));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(southMembership, OrganizationRole.Operator));

        await AssertReadsAsync(client, north, south, allowAudit: false);
        using JsonDocument session = await ReadSessionAsync(client);
        Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.False(session.RootElement.TryGetProperty("permissions", out _));
        Assert.Equal(north.OrganizationId, session.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, south.OrganizationId)).StatusCode);
        await AssertReadsAsync(client, south, north, allowAudit: true);
        HttpResponseMessage foreignAudit = await client.GetAsync("/api/allocations/" + north.AllocationId.ToString("D") + "/audit");
        string foreignAuditBody = await foreignAudit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, foreignAudit.StatusCode);
        using (JsonDocument foreignAuditDocument = JsonDocument.Parse(foreignAuditBody))
        {
            Assert.Equal(
                ReconciliationApplicationErrors.AllocationNotFound.Code,
                foreignAuditDocument.RootElement.GetProperty("code").GetString());
        }

        Assert.DoesNotContain(north.InvoiceNumber, foreignAuditBody, StringComparison.Ordinal);

        using HttpRequestMessage spoofed = new(HttpMethod.Get, "/api/invoices");
        spoofed.Headers.TryAddWithoutValidation("X-Organization-Id", north.OrganizationId.ToString("D"));
        HttpResponseMessage spoofedResponse = await client.SendAsync(spoofed);
        string spoofedBody = await spoofedResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, spoofedResponse.StatusCode);
        Assert.Contains(south.InvoiceNumber, spoofedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(north.InvoiceNumber, spoofedBody, StringComparison.Ordinal);

        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(southMembership, OrganizationRole.Operator));
        await AssertStatusAsync(client, "/api/allocations/" + south.AllocationId.ToString("D") + "/audit", HttpStatusCode.Forbidden);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(southMembership, OrganizationRole.ReversalApprover));
        await AssertStatusAsync(client, "/api/allocations/" + south.AllocationId.ToString("D") + "/audit", HttpStatusCode.OK);

        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(southMembership, OrganizationRole.ReversalApprover));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(southMembership, OrganizationRole.OrganizationAdmin));
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task No_role_no_organization_and_revocation_deny_the_next_read()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        string csrf = await ReadCsrfAsync(client);
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);

        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        HttpResponseMessage roleless = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Forbidden, roleless.StatusCode);
        Assert.DoesNotContain("auth.authorization.unavailable", await roleless.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertHeaderContains(roleless, "Cache-Control", "no-store");

        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.OK);
        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(membershipId, OrganizationRole.Viewer));
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.OK);
        await RevokeMembershipAsync(membershipId);
        try
        {
            await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);
        }
        finally
        {
            await RestoreMembershipAsync(membershipId);
        }

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            BrowserSessionStore store = new(context);
            List<BrowserSession> sessions = await context.BrowserSessions
                .Where(session => session.UserId == user.Id && session.RevokedAt == null)
                .ToListAsync();
            foreach (BrowserSession session in sessions)
            {
                await store.RevokeAsync(session.Id, user.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            }
        }

        HttpResponseMessage revoked = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.True(ClearsSessionCookie(revoked));

        await SignInAsync(client);
        csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.OK);
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            ApplicationUser tracked = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == user.Id);
            tracked.Disable();
            await context.SaveChangesAsync();
        }

        try
        {
            HttpResponseMessage disabled = await client.GetAsync("/api/invoices");
            Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
            Assert.True(ClearsSessionCookie(disabled));
        }
        finally
        {
            await using AccessDbContext context = _fixture.CreateAccessContext();
            ApplicationUser tracked = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == user.Id);
            tracked.Restore();
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Anonymous_request_is_401_and_role_storage_failure_is_503_without_sign_out()
    {
        using HttpClient anonymous = _factory.CreateBrowserClient();
        HttpResponseMessage missing = await anonymous.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        AssertHeaderContains(missing, "Cache-Control", "no-store");

        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));

        await ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments NOCHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        try
        {
            await InsertRoleAsync(membershipId, 9);
            HttpResponseMessage inconsistent = await client.GetAsync("/api/invoices");
            await AssertProblemAsync(inconsistent, HttpStatusCode.ServiceUnavailable, "auth.authorization.unavailable");
            Assert.False(ClearsSessionCookie(inconsistent));
            Assert.DoesNotContain("SqlException", await inconsistent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.False(session.RootElement.TryGetProperty("permissions", out _));
        }
        finally
        {
            await ExecuteAsync("DELETE FROM access.OrganizationRoleAssignments WHERE Role = 9");
            await ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments WITH CHECK CHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        }

        await RenameAsync("OrganizationRoleAssignments", "OrganizationRoleAssignments_offline");
        try
        {
            HttpResponseMessage outage = await client.GetAsync("/api/invoices");
            await AssertProblemAsync(outage, HttpStatusCode.ServiceUnavailable, "auth.authorization.unavailable");
            Assert.False(ClearsSessionCookie(outage));
            string outageBody = await outage.Content.ReadAsStringAsync();
            Assert.DoesNotContain("OrganizationRoleAssignments", outageBody, StringComparison.Ordinal);
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
        }
        finally
        {
            await RenameAsync("OrganizationRoleAssignments_offline", "OrganizationRoleAssignments");
        }

        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_financial_post_stays_forbidden_and_does_not_change_sql()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Operator));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.ReversalApprover));
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);

        Dictionary<string, int> before = await CountReconciliationRowsAsync();
        using (HttpRequestMessage malformed = new(HttpMethod.Post, "/api/imports/invoices")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        })
        {
            malformed.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
            malformed.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using HttpResponseMessage unsupported = await client.SendAsync(malformed);
            string unsupportedBody = await unsupported.Content.ReadAsStringAsync();
            Assert.True(
                unsupported.StatusCode == HttpStatusCode.UnsupportedMediaType,
                "/api/imports/invoices json returned " + unsupported.StatusCode + " " + unsupportedBody);
        }

        string allocationId = Guid.NewGuid().ToString("D");
        string reviewCaseId = Guid.NewGuid().ToString("D");
        string[] paths =
        [
            "/api/imports/invoices",
            "/api/imports/bank-transactions",
            "/api/matches/confirm",
            "/api/matches/batch/execute",
            "/api/allocations",
            "/api/allocations/" + allocationId + "/reverse",
            "/api/review-cases",
            "/api/review-cases/" + reviewCaseId + "/resolve",
            "/api/review-cases/" + reviewCaseId + "/dismiss",
        ];

        foreach (string path in paths)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = path.Contains("/imports/", StringComparison.Ordinal)
                    ? CreateImportContent()
                    : new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
            HttpResponseMessage response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                path + " returned " + response.StatusCode + " " + body);
            AssertHeaderContains(response, "Cache-Control", "no-store");
            Assert.DoesNotContain("auth.csrf.invalid", body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await CountReconciliationRowsAsync());
    }

    [Fact]
    public async Task Deployed_configuration_denies_a_viewer_until_the_test_host_enables_reads()
    {
        using FinOpsDeskApiFactory closed = CreateFactory(financialReadAuthorizationEnabled: false);
        using HttpClient warmup = closed.CreateClient();
        AuthenticationCompositionState closedState = closed.Services.GetRequiredService<AuthenticationCompositionState>();
        Assert.True(closedState.UsesProductionCookie);
        Assert.True(closedState.ProductionFinancialAccessFailClosed);

        using HttpClient client = closed.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);
        using HttpRequestMessage post = new(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        post.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        HttpResponseMessage denied = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using JsonDocument session = await ReadSessionAsync(client);
        Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.False(session.RootElement.TryGetProperty("permissions", out _));
    }

    [Fact]
    public async Task Misclassified_financial_endpoints_are_denied_at_runtime()
    {
        using FinOpsDeskApiFactory probed = CreateFactory(
            financialReadAuthorizationEnabled: true,
            financialClassificationProbesEnabled: true);
        using HttpClient client = probed.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await StoreGrantAsync(await MembershipIdAsync(user.Id, organizationId), OrganizationRole.Operator));
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);

        string[] paths =
        [
            "/api/test/financial-unclassified",
            "/api/test/financial-unknown",
            "/api/test/financial-write-permission",
            "/api/test/financial-write-on-get",
            "/api/test/financial-allow-anonymous",
        ];
        foreach (string path in paths)
        {
            HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("ran", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using HttpRequestMessage post = new(HttpMethod.Post, "/api/test/financial-read-on-post");
        post.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        HttpResponseMessage methodMismatch = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Forbidden, methodMismatch.StatusCode);
        Assert.DoesNotContain("ran", await methodMismatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpClient stranger = probed.CreateBrowserClient();
        HttpResponseMessage anonymous = await stranger.GetAsync("/api/test/financial-allow-anonymous");
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        Assert.DoesNotContain("ran", await anonymous.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_contract_separates_legacy_shape_from_enabled_reads()
    {
        using HttpClient anonymous = _factory.CreateBrowserClient();
        using (JsonDocument anonymousContract = await ReadContractSessionAsync(anonymous, "2"))
        {
            Assert.False(anonymousContract.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(anonymousContract.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.Equal(0, anonymousContract.RootElement.GetProperty("permissions").GetArrayLength());
        }

        using (JsonDocument legacy = await ReadContractSessionAsync(anonymous, "future"))
        {
            Assert.False(legacy.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.False(legacy.RootElement.TryGetProperty("permissions", out _));
        }

        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        string csrf = await ReadCsrfAsync(client);

        using (JsonDocument unselected = await ReadContractSessionAsync(client, "2"))
        {
            Assert.True(unselected.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(unselected.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.Equal(0, unselected.RootElement.GetProperty("permissions").GetArrayLength());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        using (JsonDocument roleless = await ReadContractSessionAsync(client, "2"))
        {
            AssertPermissions(roleless.RootElement);
        }

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.OrganizationAdmin));
        using (JsonDocument admin = await ReadContractSessionAsync(client, "2"))
        {
            AssertPermissions(admin.RootElement);
        }

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        using (JsonDocument viewer = await ReadContractSessionAsync(client, "2"))
        {
            AssertPermissions(
                viewer.RootElement,
                "invoices.read",
                "bank-transactions.read",
                "reconciliation.read",
                "allocations.read",
                "review-cases.read");
        }

        using (JsonDocument versionOne = await ReadContractSessionAsync(client, null))
        {
            Assert.False(versionOne.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.False(versionOne.RootElement.TryGetProperty("permissions", out _));
        }

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Operator));
        using (JsonDocument combined = await ReadContractSessionAsync(client, "2"))
        {
            AssertPermissions(
                combined.RootElement,
                "invoices.read",
                "bank-transactions.read",
                "reconciliation.read",
                "allocations.read",
                "audit.read",
                "review-cases.read");
        }

        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(membershipId, OrganizationRole.Viewer));
        Assert.Equal(OrganizationRoleMutationStatus.Revoked, await StoreRevokeAsync(membershipId, OrganizationRole.Operator));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.ReversalApprover));
        using (JsonDocument approver = await ReadContractSessionAsync(client, "2"))
        {
            AssertPermissions(
                approver.RootElement,
                "invoices.read",
                "bank-transactions.read",
                "reconciliation.read",
                "allocations.read",
                "audit.read",
                "review-cases.read");
        }
    }

    [Fact]
    public async Task Session_contract_v2_fails_closed_when_role_storage_fails_and_v1_stays_available()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));

        await RenameAsync("OrganizationRoleAssignments", "OrganizationRoleAssignments_offline");
        try
        {
            using HttpRequestMessage versionTwo = new(HttpMethod.Get, "/api/session");
            versionTwo.Headers.TryAddWithoutValidation(SessionContract.HeaderName, "2");
            HttpResponseMessage unavailable = await client.SendAsync(versionTwo);
            await AssertProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, "auth.authorization.unavailable");
            Assert.False(ClearsSessionCookie(unavailable));
            AssertHeaderContains(unavailable, "Vary", SessionContract.HeaderName);
            Assert.DoesNotContain("OrganizationRoleAssignments", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using JsonDocument versionOne = await ReadContractSessionAsync(client, null);
            Assert.True(versionOne.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(versionOne.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.False(versionOne.RootElement.TryGetProperty("permissions", out _));
        }
        finally
        {
            await RenameAsync("OrganizationRoleAssignments_offline", "OrganizationRoleAssignments");
        }

        using JsonDocument restored = await ReadContractSessionAsync(client, "2");
        AssertPermissions(
            restored.RootElement,
            "invoices.read",
            "bank-transactions.read",
            "reconciliation.read",
            "allocations.read",
            "review-cases.read");
    }

    [Fact]
    public async Task Disabled_financial_reads_keep_contract_v2_empty_without_the_role_table()
    {
        using FinOpsDeskApiFactory closed = CreateFactory(financialReadAuthorizationEnabled: false);
        using HttpClient warmup = closed.CreateClient();
        using HttpClient client = closed.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Viewer));
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);

        await RenameAsync("OrganizationRoleAssignments", "OrganizationRoleAssignments_offline");
        try
        {
            using JsonDocument session = await ReadContractSessionAsync(client, "2");
            AssertPermissions(session.RootElement);
            await AssertStatusAsync(client, "/api/invoices", HttpStatusCode.Forbidden);
        }
        finally
        {
            await RenameAsync("OrganizationRoleAssignments_offline", "OrganizationRoleAssignments");
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static MultipartFormDataContent CreateImportContent()
    {
        MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent("invoiceNumber,amount\nINV,1.00\n"u8.ToArray()), "file", "invoices.csv");
        content.Add(new StringContent("test-source"), "sourceSystem");
        return content;
    }

    private FinOpsDeskApiFactory CreateFactory(
        bool financialReadAuthorizationEnabled,
        Action<IServiceCollection>? configureServices = null,
        bool financialClassificationProbesEnabled = false)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            configureServices,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = _stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            },
            financialReadAuthorizationEnabled: financialReadAuthorizationEnabled,
            financialClassificationProbesEnabled: financialClassificationProbesEnabled);
    }

    private async Task AssertReadsAsync(
        HttpClient client,
        FinancialPair pair,
        FinancialPair hidden,
        bool allowAudit)
    {
        HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
        string invoiceBody = await invoices.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invoices.StatusCode);
        Assert.Contains(pair.InvoiceNumber, invoiceBody, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.InvoiceNumber, invoiceBody, StringComparison.Ordinal);

        HttpResponseMessage banks = await client.GetAsync("/api/bank-transactions");
        string bankBody = await banks.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, banks.StatusCode);
        Assert.Contains(pair.BankReference, bankBody, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.BankReference, bankBody, StringComparison.Ordinal);

        await AssertStatusAsync(client, "/api/matches", HttpStatusCode.OK);
        await AssertStatusAsync(client, "/api/matches/smart", HttpStatusCode.OK);
        await AssertStatusAsync(client, "/api/matches/batch/preview", HttpStatusCode.OK);
        await AssertStatusAsync(client, "/api/allocations", HttpStatusCode.OK);
        await AssertStatusAsync(
            client,
            "/api/allocations/context?invoiceId=" + pair.InvoiceId.ToString("D")
                + "&bankTransactionId=" + pair.BankTransactionId.ToString("D"),
            HttpStatusCode.OK);
        await AssertStatusAsync(
            client,
            "/api/allocations/" + pair.AllocationId.ToString("D") + "/reversal-context",
            HttpStatusCode.OK);

        HttpResponseMessage reviews = await client.GetAsync("/api/review-cases");
        string reviewBody = await reviews.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reviews.StatusCode);
        Assert.Contains(pair.ReviewNote, reviewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.ReviewNote, reviewBody, StringComparison.Ordinal);
        await AssertStatusAsync(client, "/api/review-cases/" + pair.ReviewCaseId.ToString("D"), HttpStatusCode.OK);
        HttpResponseMessage hiddenReview = await client.GetAsync("/api/review-cases/" + hidden.ReviewCaseId.ToString("D"));
        string hiddenReviewBody = await hiddenReview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, hiddenReview.StatusCode);
        Assert.DoesNotContain(hidden.ReviewNote, hiddenReviewBody, StringComparison.Ordinal);
        HttpResponseMessage hiddenContext = await client.GetAsync(
            "/api/allocations/context?invoiceId=" + hidden.InvoiceId.ToString("D")
            + "&bankTransactionId=" + pair.BankTransactionId.ToString("D"));
        Assert.Equal(HttpStatusCode.NotFound, hiddenContext.StatusCode);
        HttpResponseMessage hiddenReversal = await client.GetAsync(
            "/api/allocations/" + hidden.AllocationId.ToString("D") + "/reversal-context");
        Assert.Equal(HttpStatusCode.NotFound, hiddenReversal.StatusCode);
        await AssertStatusAsync(
            client,
            "/api/allocations/" + pair.AllocationId.ToString("D") + "/audit",
            allowAudit ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
    }

    private async Task<FinancialPair> CreateOrganizationWithFinancialDataAsync(ApplicationUser user, string name)
    {
        Guid organizationId = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationId));
        OrganizationId tenant = new(organizationId);
        Invoice invoice = new(InvoiceId.New(), tenant, name + "-invoice", new Money(25m, PersistenceTestData.Pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(25m, PersistenceTestData.Pln),
            new DateOnly(2026, 11, 2),
            name + "-bank");
        Allocation allocation = new(
            AllocationId.New(),
            tenant,
            invoice.Id,
            bank.Id,
            new Money(25m, PersistenceTestData.Pln),
            DateTimeOffset.UtcNow);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            tenant,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            name + "-review",
            DateTimeOffset.UtcNow);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bank);
            context.Allocations.Add(allocation);
            context.ReviewCases.Add(reviewCase);
            await context.SaveChangesAsync();
        }

        return new FinancialPair(
            organizationId,
            invoice.Id.Value,
            invoice.InvoiceNumber,
            bank.Id.Value,
            bank.Reference ?? throw new InvalidOperationException("Bank reference was missing."),
            allocation.Id.Value,
            reviewCase.Id.Value,
            name + "-review");
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
                "Ledger " + organizationId.ToString("N"),
                CancellationToken.None)));
        return organizationId;
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

    private async Task<OrganizationRoleMutationStatus> StoreGrantAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).GrantAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow,
            RoleActor.DeploymentBootstrap(OperatorName),
            CancellationToken.None);
    }

    private async Task<OrganizationRoleMutationStatus> StoreRevokeAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).RevokeAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow.AddMinutes(1),
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

    private async Task RevokeMembershipAsync(Guid membershipId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
        membership.Revoke(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task RestoreMembershipAsync(Guid membershipId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
        membership.Restore();
        await context.SaveChangesAsync();
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

    private async Task<Dictionary<string, int>> CountReconciliationRowsAsync()
    {
        Dictionary<string, int> counts = [];
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        List<string> tables = [];
        await using (SqlCommand list = new(
            """
            SELECT t.name
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
            WHERE s.name = N'reconciliation'
            """,
            connection))
        await using (SqlDataReader reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables)
        {
            if (table.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                throw new InvalidOperationException("Unexpected reconciliation table name.");
            }

            await using SqlCommand count = new(
                "SELECT COUNT(*) FROM [reconciliation].[" + table + "]",
                connection);
            counts[table] = Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private async Task SignInAsync(HttpClient client)
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
        callback.Dispose();
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        using JsonDocument document = await ReadSessionAsync(client);
        return document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");
    }

    private static async Task<JsonDocument> ReadSessionAsync(HttpClient client)
    {
        return await ReadContractSessionAsync(client, null);
    }

    private static async Task<JsonDocument> ReadContractSessionAsync(HttpClient client, string? contract)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/session");
        if (contract is not null)
        {
            request.Headers.TryAddWithoutValidation(SessionContract.HeaderName, contract);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHeaderContains(response, "Cache-Control", "no-store");
        AssertHeaderContains(response, "Vary", SessionContract.HeaderName);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertPermissions(JsonElement session, params string[] expected)
    {
        Assert.Equal(expected.Length > 0, session.GetProperty("financialAccess").GetBoolean());
        JsonElement permissions = session.GetProperty("permissions");
        string[] actual = permissions.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("imports.create", actual);
        Assert.DoesNotContain("reconciliation.confirm", actual);
        Assert.DoesNotContain("allocations.create", actual);
        Assert.DoesNotContain("allocations.reverse", actual);
        Assert.DoesNotContain("review-cases.open", actual);
        Assert.DoesNotContain("review-cases.resolve", actual);
        Assert.DoesNotContain("membership.manage", actual);
        Assert.DoesNotContain("roles.manage", actual);
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

    private static async Task AssertStatusAsync(HttpClient client, string path, HttpStatusCode statusCode)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(statusCode, response.StatusCode);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        Assert.Equal(statusCode, response.StatusCode);
        AssertHeaderContains(response, "Cache-Control", "no-store");
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static void AssertHeaderContains(HttpResponseMessage response, string name, string fragment)
    {
        Assert.True(response.Headers.TryGetValues(name, out IEnumerable<string>? values));
        Assert.Contains(values!, value => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
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

    private sealed record FinancialPair(
        Guid OrganizationId,
        Guid InvoiceId,
        string InvoiceNumber,
        Guid BankTransactionId,
        string BankReference,
        Guid AllocationId,
        Guid ReviewCaseId,
        string ReviewNote);
}