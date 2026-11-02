using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AccessMembershipSessionTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public AccessMembershipSessionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = CreateFactory(_stub, fixture.ConnectionString);
    }

    [Fact]
    public async Task Successful_login_creates_one_user_and_repeat_login_reuses_it()
    {
        using HttpClient client = _factory.CreateBrowserClient();

        await SignInAsync(client, _stub);
        ApplicationUser created = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        using JsonDocument first = await ReadSessionAsync(client);
        Assert.True(first.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(first.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(0, first.RootElement.GetProperty("organizations").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("activeOrganization").ValueKind);

        await SignInAsync(client, _stub);
        ApplicationUser again = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);

        Assert.Equal(created.Id, again.Id);
        Assert.Equal(ApplicationUserStatus.Active, again.Status);
    }

    [Theory]
    [InlineData(OidcTokenFault.WrongKey)]
    [InlineData(OidcTokenFault.WrongIssuer)]
    [InlineData(OidcTokenFault.WrongAudience)]
    [InlineData(OidcTokenFault.WrongNonce)]
    [InlineData(OidcTokenFault.TokenEndpointUnavailable)]
    public async Task Failed_token_validation_does_not_create_a_user(OidcTokenFault fault)
    {
        Guid? before = await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject);
        using HttpClient client = _factory.CreateBrowserClient();
        _stub.ArmNextToken(fault);

        await SignInAsync(client, _stub);

        Assert.Equal(before, await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject));
        using JsonDocument document = await ReadSessionAsync(client);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("organizations").GetArrayLength());
    }

    [Fact]
    public async Task Forged_state_does_not_create_a_user()
    {
        Guid? before = await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject);
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        await client.SendAsync(login);

        HttpResponseMessage callback = await client.GetAsync("/signin-oidc?code=forged&state=forged");

        Assert.Equal(before, await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject));
        Assert.DoesNotContain("__Host-finopsdesk-session", CookieHeader(callback), StringComparison.Ordinal);
        using JsonDocument document = await ReadSessionAsync(client);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Provisioning_failure_does_not_issue_a_cookie()
    {
        Guid? before = await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject);
        await RenameUsersAsync("ApplicationUsers", "ApplicationUsers_offline");
        try
        {
            using HttpClient client = _factory.CreateBrowserClient();
            HttpResponseMessage callback = await SignInAsync(client, _stub);
            string body = await callback.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, callback.StatusCode);
            Assert.Contains("no-store", callback.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("auth.identity.unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplicationUsers", body, StringComparison.Ordinal);
            Assert.DoesNotContain(OidcProtocolStub.ClientSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("__Host-finopsdesk-session", CookieHeader(callback), StringComparison.Ordinal);
            using JsonDocument document = await ReadSessionAsync(client);
            Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await RenameUsersAsync("ApplicationUsers_offline", "ApplicationUsers");
        }

        Assert.Equal(before, await FindUserIdAsync(_stub.Authority, OidcProtocolStub.Subject));
    }

    [Fact]
    public async Task Disabled_user_cannot_establish_a_session()
    {
        await EnsureUserAsync(_stub.Authority, OidcProtocolStub.Subject, ApplicationUserStatus.Disabled);
        try
        {
            using HttpClient client = _factory.CreateBrowserClient();
            HttpResponseMessage callback = await SignInAsync(client, _stub);
            string body = await callback.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
            Assert.Contains("auth.user.disabled", body, StringComparison.Ordinal);
            Assert.DoesNotContain("__Host-finopsdesk-session", CookieHeader(callback), StringComparison.Ordinal);
            using JsonDocument document = await ReadSessionAsync(client);
            Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await EnsureUserAsync(_stub.Authority, OidcProtocolStub.Subject, ApplicationUserStatus.Active);
        }
    }

    [Fact]
    public async Task Session_lists_only_active_memberships_of_active_organizations()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid activeId = Guid.NewGuid();
        Guid revokedId = Guid.NewGuid();
        Guid disabledId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.Organizations.Add(Organization.Create(activeId, "North Ledger", now));
            context.Organizations.Add(Organization.Create(revokedId, "Revoked Ledger", now));
            Organization disabled = Organization.Create(disabledId, "Closed Ledger", now);
            disabled.Disable();
            context.Organizations.Add(disabled);
            context.OrganizationMemberships.Add(OrganizationMembership.Grant(Guid.NewGuid(), user.Id, activeId, now));
            OrganizationMembership revoked = OrganizationMembership.Grant(Guid.NewGuid(), user.Id, revokedId, now);
            revoked.Revoke(now);
            context.OrganizationMemberships.Add(revoked);
            context.OrganizationMemberships.Add(OrganizationMembership.Grant(Guid.NewGuid(), user.Id, disabledId, now));
            await context.SaveChangesAsync();
        }

        try
        {
            using JsonDocument document = await ReadSessionAsync(client);
            Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(document.RootElement.GetProperty("financialAccess").GetBoolean());
            JsonElement organizations = document.RootElement.GetProperty("organizations");
            Assert.Equal(1, organizations.GetArrayLength());
            Assert.Equal(activeId, organizations[0].GetProperty("organizationId").GetGuid());
            Assert.Equal("North Ledger", organizations[0].GetProperty("displayName").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("activeOrganization").ValueKind);
            Assert.DoesNotContain("Revoked Ledger", document.RootElement.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Closed Ledger", document.RootElement.ToString(), StringComparison.Ordinal);

            HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
            Assert.Equal(HttpStatusCode.Forbidden, invoices.StatusCode);
        }
        finally
        {
            await DeleteMembershipsAsync(user.Id, activeId, revokedId, disabledId);
        }
    }

    [Fact]
    public async Task Membership_read_failure_is_not_an_empty_organization_list()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        await RenameUsersAsync("ApplicationUsers", "ApplicationUsers_offline");
        try
        {
            HttpResponseMessage response = await client.GetAsync("/api/session");
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("auth.membership.unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("organizations", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
            Assert.DoesNotContain(OidcProtocolStub.ClientSecret, body, StringComparison.Ordinal);
        }
        finally
        {
            await RenameUsersAsync("ApplicationUsers_offline", "ApplicationUsers");
        }

        using JsonDocument restored = await ReadSessionAsync(client);
        Assert.True(restored.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Array, restored.RootElement.GetProperty("organizations").ValueKind);
    }

    [Fact]
    public async Task Production_cookie_still_rejects_bearer_and_financial_calls()
    {
        using HttpClient bearer = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage invoices = await bearer.GetAsync("/api/invoices");
        HttpResponseMessage allocation = await bearer.PostAsync(
            "/api/allocations",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, invoices.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, allocation.StatusCode);

        using HttpClient browser = _factory.CreateBrowserClient();
        await SignInAsync(browser, _stub);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await browser.PostAsync("/api/allocations", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Development_jwt_still_reads_invoices_without_an_access_organization()
    {
        Guid organizationId = Guid.NewGuid();
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        Assert.False(await context.Organizations.AnyAsync(organization => organization.Id == organizationId));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static FinOpsDeskApiFactory CreateFactory(OidcProtocolStub stub, string connectionString)
    {
        return new FinOpsDeskApiFactory(
            connectionString,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });
    }

    private async Task EnsureUserAsync(string issuer, string subject, ApplicationUserStatus status)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser? user = await context.ApplicationUsers
            .SingleOrDefaultAsync(candidate => candidate.IdentityKey == identityKey);
        if (user is null)
        {
            user = ApplicationUser.Create(Guid.NewGuid(), issuer, subject, null, DateTimeOffset.UtcNow);
            context.ApplicationUsers.Add(user);
        }

        if (status == ApplicationUserStatus.Disabled)
        {
            user.Disable();
        }
        else
        {
            user.Restore();
        }

        await context.SaveChangesAsync();
    }

    private async Task<ApplicationUser> RequireUserAsync(string issuer, string subject)
    {
        ApplicationUser? user = await FindUserAsync(issuer, subject);
        Assert.NotNull(user);
        return user;
    }

    private async Task<Guid?> FindUserIdAsync(string issuer, string subject)
    {
        ApplicationUser? user = await FindUserAsync(issuer, subject);
        return user?.Id;
    }

    private async Task<ApplicationUser?> FindUserAsync(string issuer, string subject)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<ApplicationUser> rows = await context.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync();
        return rows.SingleOrDefault(user => ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject));
    }

    private async Task DeleteMembershipsAsync(Guid userId, params Guid[] organizationIds)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<OrganizationMembership> memberships = await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId && organizationIds.Contains(membership.OrganizationId))
            .ToListAsync();
        List<Guid> membershipIds = memberships.Select(membership => membership.Id).ToList();
        List<OrganizationRoleAssignment> assignments = await context.OrganizationRoleAssignments
            .Where(assignment => membershipIds.Contains(assignment.MembershipId))
            .ToListAsync();
        context.OrganizationRoleAssignments.RemoveRange(assignments);
        context.OrganizationMemberships.RemoveRange(memberships);
        List<Organization> organizations = await context.Organizations
            .Where(organization => organizationIds.Contains(organization.Id))
            .ToListAsync();
        context.Organizations.RemoveRange(organizations);
        await context.SaveChangesAsync();
    }

    private async Task RenameUsersAsync(string from, string to)
    {
        if (from is not ("ApplicationUsers" or "ApplicationUsers_offline")
            || to is not ("ApplicationUsers" or "ApplicationUsers_offline"))
        {
            throw new InvalidOperationException("Unexpected access table rename.");
        }

        string sql = "EXEC sp_rename N'access." + from + "', N'" + to + "';";
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
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

        return await client.GetAsync(authorize.Headers.Location!.PathAndQuery);
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
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string CookieHeader(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? string.Join('\n', values)
            : string.Empty;
    }
}