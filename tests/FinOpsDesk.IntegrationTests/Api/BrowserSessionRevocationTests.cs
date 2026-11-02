using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class BrowserSessionRevocationTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public BrowserSessionRevocationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = CreateFactory(_stub, fixture.ConnectionString, configure: null);
    }

    [Fact]
    public async Task Two_logins_create_independent_sessions_for_one_user()
    {
        using HttpClient first = _factory.CreateBrowserClient();
        using HttpClient second = _factory.CreateBrowserClient();
        await SignInAsync(first, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid stamp = user.SessionStamp;
        Assert.Equal(1, await CountSessionsAsync(user.Id));

        await SignInAsync(second, _stub);
        List<BrowserSession> sessions = await ListSessionsAsync(user.Id);
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0].Id, sessions[1].Id);
        Assert.Equal(stamp, (await RequireUserAsync()).SessionStamp);
        Assert.All(sessions, session => Assert.Null(session.RevokedAt));
    }

    [Fact]
    public async Task Invalid_callback_creates_no_session_and_a_failed_insert_issues_no_cookie()
    {
        ApplicationUser user = await RequireUserAsyncOrNull() ?? await CreateUserAsync();
        int before = await CountSessionsAsync(user.Id);
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await client.GetAsync("/signin-oidc?code=not-a-code&state=not-a-state");
        Assert.NotEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Null(SessionCookie(callback));
        Assert.Equal(before, await CountSessionsAsync(user.Id));

        await RenameAsync("BrowserSessions", "BrowserSessions_offline");
        try
        {
            using HttpClient retry = _factory.CreateBrowserClient();
            HttpResponseMessage failed = await CompleteCallbackAsync(retry, _stub);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Null(SessionCookie(failed));
            Assert.Contains("auth.identity.unavailable", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await RenameAsync("BrowserSessions_offline", "BrowserSessions");
        }

        Assert.Equal(before, await CountSessionsAsync(user.Id));
    }

    [Fact]
    public async Task Legacy_and_malformed_session_identifiers_are_rejected()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        BrowserSession session = await SingleSessionAsync(user.Id);

        using HttpClient legacy = ClientWithSession(Protect(Principal(user, session.Id, includeSession: false)));
        HttpResponseMessage legacyResponse = await legacy.GetAsync("/api/session");
        using JsonDocument legacyBody = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync());
        Assert.False(legacyBody.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(ClearsSessionCookie(legacyResponse));

        using HttpClient duplicate = ClientWithSession(Protect(Principal(user, session.Id, duplicateSession: true)));
        using JsonDocument duplicateBody = await ReadSessionAsync(duplicate);
        Assert.False(duplicateBody.RootElement.GetProperty("authenticated").GetBoolean());

        using HttpClient malformed = ClientWithSession(Protect(Principal(user, session.Id, sessionText: "not-a-guid")));
        using JsonDocument malformedBody = await ReadSessionAsync(malformed);
        Assert.False(malformedBody.RootElement.GetProperty("authenticated").GetBoolean());

        using HttpClient unknown = ClientWithSession(Protect(Principal(user, Guid.NewGuid())));
        using JsonDocument unknownBody = await ReadSessionAsync(unknown);
        Assert.False(unknownBody.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Session_for_another_user_revoked_or_expired_cannot_authenticate()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        BrowserSession own = await SingleSessionAsync(user.Id);
        ApplicationUser other = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            "other-session-subject",
            null,
            DateTimeOffset.UtcNow);
        BrowserSession foreign = BrowserSession.Open(
            BrowserSession.CreateId(),
            other.Id,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(7));
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.ApplicationUsers.Add(other);
            context.BrowserSessions.Add(foreign);
            await context.SaveChangesAsync();
        }

        using HttpClient mismatch = ClientWithSession(Protect(Principal(user, foreign.Id)));
        using JsonDocument mismatchBody = await ReadSessionAsync(mismatch);
        Assert.False(mismatchBody.RootElement.GetProperty("authenticated").GetBoolean());

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            await context.BrowserSessions
                .Where(session => session.Id == own.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    session => session.RevokedAt,
                    DateTimeOffset.UtcNow));
        }

        using HttpClient revoked = ClientWithSession(Protect(Principal(user, own.Id)));
        using JsonDocument revokedBody = await ReadSessionAsync(revoked);
        Assert.False(revokedBody.RootElement.GetProperty("authenticated").GetBoolean());

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            await context.BrowserSessions
                .Where(session => session.Id == own.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.RevokedAt, (DateTimeOffset?)null)
                    .SetProperty(session => session.ExpiresAt, own.CreatedAt.AddMilliseconds(1)));
        }

        using HttpClient expired = ClientWithSession(Protect(Principal(user, own.Id)));
        HttpResponseMessage expiredResponse = await expired.GetAsync("/api/session");
        using JsonDocument expiredBody = JsonDocument.Parse(await expiredResponse.Content.ReadAsStringAsync());
        Assert.False(expiredBody.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(ClearsSessionCookie(expiredResponse));
        using HttpRequestMessage probe = new(HttpMethod.Get, "/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.SendAsync(probe)).StatusCode);
    }

    [Fact]
    public async Task Sql_outage_does_not_trust_a_tenant_or_confirm_logout()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await GrantAsync(user.Id);
        try
        {
            string csrf = await ReadCsrfAsync(client);
            HttpResponseMessage selected = await SelectAsync(client, csrf, organizationId);
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
            string ticket = SessionCookie(selected) ?? throw new InvalidOperationException("Session cookie was missing.");
            Assert.Null(SessionCookie(await client.GetAsync("/api/session")));

            await RenameAsync("BrowserSessions", "BrowserSessions_offline");
            try
            {
                HttpResponseMessage unavailable = await client.GetAsync("/api/session");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                Assert.Contains("auth.membership.unavailable", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                Assert.False(ClearsSessionCookie(unavailable));
                HttpResponseMessage logout = await PostAsync(client, "/logout", csrf);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, logout.StatusCode);
                Assert.NotEqual(HttpStatusCode.NoContent, logout.StatusCode);
            }
            finally
            {
                await RenameAsync("BrowserSessions_offline", "BrowserSessions");
            }

            BrowserSession session = await SingleSessionAsync(user.Id);
            Assert.Null(session.RevokedAt);
            using HttpClient restored = ClientWithSession(ticket);
            using JsonDocument sessionBody = await ReadSessionAsync(restored);
            Assert.True(sessionBody.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(sessionBody.RootElement.GetProperty("financialAccess").GetBoolean());
            Assert.Equal(organizationId, sessionBody.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
        }
        finally
        {
            await DeleteOrganizationAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Logout_revokes_only_the_current_session_and_a_late_cookie_cannot_restore_it()
    {
        using HttpClient first = _factory.CreateBrowserClient();
        using HttpClient second = _factory.CreateBrowserClient();
        await SignInAsync(first, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid firstSessionId = (await SingleSessionAsync(user.Id)).Id;
        await SignInAsync(second, _stub);
        Guid stamp = user.SessionStamp;
        Guid organizationId = await GrantAsync(user.Id);
        try
        {
            string firstCsrf = await ReadCsrfAsync(first);
            string secondCsrf = await ReadCsrfAsync(second);
            HttpResponseMessage selected = await SelectAsync(first, firstCsrf, organizationId);
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
            string reissued = SessionCookie(selected)
                ?? throw new InvalidOperationException("Organization selection did not reissue the session cookie.");
            Assert.Equal(2, await CountSessionsAsync(user.Id));

            HttpResponseMessage logout = await PostAsync(first, "/logout", firstCsrf);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            BrowserSession revoked = (await ListSessionsAsync(user.Id)).Single(session => session.Id == firstSessionId);
            Assert.NotNull(revoked.RevokedAt);
            Assert.Equal(stamp, (await RequireUserAsync()).SessionStamp);

            using HttpClient resurrected = ClientWithSession(reissued);
            HttpResponseMessage replay = await resurrected.GetAsync("/api/session");
            using JsonDocument replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.False(replayBody.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, replayBody.RootElement.GetProperty("activeOrganization").ValueKind);
            Assert.True(ClearsSessionCookie(replay));
            Assert.Equal(HttpStatusCode.Unauthorized, (await resurrected.GetAsync("/api/invoices")).StatusCode);

            using JsonDocument other = await ReadSessionAsync(second);
            Assert.True(other.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(second, secondCsrf, organizationId)).StatusCode);
            Assert.Equal(2, await CountSessionsAsync(user.Id));
            BrowserSession surviving = (await ListSessionsAsync(user.Id)).Single(session => session.Id != revoked.Id);
            Assert.Null(surviving.RevokedAt);
            Assert.Equal(stamp, (await RequireUserAsync()).SessionStamp);

            HttpResponseMessage after = await SelectAsync(resurrected, firstCsrf, organizationId);
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.Null(SessionCookie(after));
            Assert.Equal(2, await CountSessionsAsync(user.Id));
        }
        finally
        {
            await DeleteOrganizationAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Organization_switching_preserves_one_session_and_its_deadline()
    {
        ManualTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync();
        using FinOpsDeskApiFactory factory = CreateFactory(stub, _fixture.ConnectionString, services =>
        {
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.TimeProvider = clock);
        });
        using HttpClient client = factory.CreateBrowserClient();
        await SignInAsync(client, stub);
        ApplicationUser user = await RequireUserAsync(stub.Authority);
        Guid first = await GrantAsync(user.Id);
        Guid second = await GrantAsync(user.Id);
        try
        {
            BrowserSession created = await SingleSessionAsync(user.Id);
            DateTimeOffset expires = await ReadExpiryAsync(client);
            Assert.Equal(created.ExpiresAt.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds());
            Assert.Equal(created.CreatedAt.ToUnixTimeSeconds(), expires.AddHours(-8).ToUnixTimeSeconds());
            string csrf = await ReadCsrfAsync(client);
            clock.UtcNow = clock.UtcNow.AddHours(3);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, first)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, second)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, first)).StatusCode);
            Assert.Equal(expires, await ReadExpiryAsync(client));
            BrowserSession after = await SingleSessionAsync(user.Id);
            Assert.Equal(created.Id, after.Id);
            Assert.Equal(created.ExpiresAt, after.ExpiresAt);
            Assert.Equal(1, await CountSessionsAsync(user.Id));
        }
        finally
        {
            await DeleteOrganizationAsync(user.Id, first, second);
        }
    }

    [Fact]
    public async Task Revocation_is_conditional_and_does_not_revive_a_session()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        BrowserSession session = await SingleSessionAsync(user.Id);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IBrowserSessionStore store = scope.ServiceProvider.GetRequiredService<IBrowserSessionStore>();
        DateTimeOffset revokedAt = DateTimeOffset.UtcNow;
        Assert.Equal(
            BrowserSessionRevocation.Revoked,
            await store.RevokeAsync(session.Id, user.Id, revokedAt, CancellationToken.None));
        Assert.Equal(
            BrowserSessionRevocation.AlreadyRevoked,
            await store.RevokeAsync(session.Id, user.Id, revokedAt.AddMinutes(5), CancellationToken.None));
        BrowserSession stored = await SingleSessionAsync(user.Id);
        Assert.Equal(revokedAt.ToUnixTimeMilliseconds(), stored.RevokedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(BrowserSessionRevocation.Missing, await store.RevokeAsync(Guid.NewGuid(), user.Id, revokedAt, CancellationToken.None));
        Assert.Equal(
            BrowserSessionRevocation.UserMismatch,
            await store.RevokeAsync(session.Id, Guid.NewGuid(), revokedAt, CancellationToken.None));
        Assert.NotNull((await SingleSessionAsync(user.Id)).RevokedAt);
    }

    [Fact]
    public async Task Disable_then_restore_still_rejects_the_original_cookie()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string ticket = SessionCookie(callback) ?? throw new InvalidOperationException("Session cookie was missing.");
        ApplicationUser user = await RequireUserAsync();
        Guid stamp = user.SessionStamp;
        await SetStatusAsync(user.Id, ApplicationUserStatus.Disabled);
        await SetStatusAsync(user.Id, ApplicationUserStatus.Active);
        ApplicationUser restored = await RequireUserAsync();
        Assert.NotEqual(stamp, restored.SessionStamp);

        using HttpClient replay = ClientWithSession(ticket);
        using JsonDocument body = await ReadSessionAsync(replay);
        Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Development_jwt_does_not_create_a_browser_session_and_cookie_finance_stays_closed()
    {
        int before = await CountAllSessionsAsync();
        using (FinOpsDeskApiFactory jwt = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt))
        {
            using HttpClient client = jwt.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/invoices")).StatusCode);
        }

        Assert.Equal(before, await CountAllSessionsAsync());

        using HttpClient browser = _factory.CreateBrowserClient();
        await SignInAsync(browser, _stub);
        using JsonDocument session = await ReadSessionAsync(browser);
        Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task Browser_session_constraints_do_not_touch_reconciliation_history()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE name = N'CK_BrowserSessions_ExpiresAt'
            """));
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT delete_referential_action
            FROM sys.foreign_keys
            WHERE name = N'FK_BrowserSessions_ApplicationUsers_UserId'
            """));
        int reconciliationHistory = await ScalarAsync(connection, "SELECT COUNT(*) FROM reconciliation.__EFMigrationsHistory");
        Assert.InRange(reconciliationHistory, 1, int.MaxValue);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private FinOpsDeskApiFactory CreateFactory(
        OidcProtocolStub stub,
        string connectionString,
        Action<IServiceCollection>? configure)
    {
        return new FinOpsDeskApiFactory(
            connectionString,
            configure,
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

    private async Task<ApplicationUser> RequireUserAsync()
    {
        return await RequireUserAsync(_stub.Authority);
    }

    private async Task<ApplicationUser> RequireUserAsync(string issuer)
    {
        ApplicationUser? user = await FindUserAsync(issuer, OidcProtocolStub.Subject);
        Assert.NotNull(user);
        return user;
    }

    private async Task<ApplicationUser?> RequireUserAsyncOrNull()
    {
        return await FindUserAsync(_stub.Authority, OidcProtocolStub.Subject);
    }

    private async Task<ApplicationUser?> FindUserAsync(string issuer, string subject)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<ApplicationUser> rows = await context.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync();
        return rows.SingleOrDefault(candidate =>
            ExternalIdentityKey.Same(candidate.Issuer, candidate.Subject, issuer, subject));
    }

    private async Task<ApplicationUser> CreateUserAsync()
    {
        ApplicationUser user = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            OidcProtocolStub.Subject,
            null,
            DateTimeOffset.UtcNow);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        context.ApplicationUsers.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<List<BrowserSession>> ListSessionsAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.BrowserSessions
            .AsNoTracking()
            .Where(session => session.UserId == userId)
            .OrderBy(session => session.CreatedAt)
            .ToListAsync();
    }

    private async Task<BrowserSession> SingleSessionAsync(Guid userId)
    {
        List<BrowserSession> sessions = await ListSessionsAsync(userId);
        return Assert.Single(sessions);
    }

    private async Task<int> CountSessionsAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.BrowserSessions.CountAsync(session => session.UserId == userId);
    }

    private async Task<int> CountAllSessionsAsync()
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.BrowserSessions.CountAsync();
    }

    private async Task<Guid> GrantAsync(Guid userId)
    {
        Guid organizationId = Guid.NewGuid();
        await using AccessDbContext context = _fixture.CreateAccessContext();
        context.Organizations.Add(Organization.Create(organizationId, "North Ledger", DateTimeOffset.UtcNow));
        context.OrganizationMemberships.Add(
            OrganizationMembership.Grant(Guid.NewGuid(), userId, organizationId, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        return organizationId;
    }

    private async Task DeleteOrganizationAsync(Guid userId, params Guid[] organizationIds)
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

    private async Task SetStatusAsync(Guid userId, ApplicationUserStatus status)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser user = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == userId);
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

    private async Task RenameAsync(string from, string to)
    {
        if (from is not ("BrowserSessions" or "BrowserSessions_offline")
            || to is not ("BrowserSessions" or "BrowserSessions_offline"))
        {
            throw new InvalidOperationException("Unexpected access table rename.");
        }

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new("EXEC sp_rename N'access." + from + "', N'" + to + "';", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = new(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Protect(ClaimsPrincipal principal)
    {
        CookieAuthenticationOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        DateTimeOffset issued = TimeProvider.System.GetUtcNow();
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued.AddHours(8),
            },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    private static ClaimsPrincipal Principal(
        ApplicationUser user,
        Guid sessionId,
        bool includeSession = true,
        bool duplicateSession = false,
        string? sessionText = null)
    {
        var claims = new List<Claim>
        {
            new("iss", user.Issuer, ClaimValueTypes.String, user.Issuer),
            new("sub", user.Subject, ClaimValueTypes.String, user.Issuer),
            new(FinOpsDeskCookieClaims.SessionStamp, user.SessionStamp.ToString("D"), ClaimValueTypes.String, user.Issuer),
        };
        if (includeSession)
        {
            claims.Add(new Claim(
                FinOpsDeskCookieClaims.SessionId,
                sessionText ?? sessionId.ToString("D"),
                ClaimValueTypes.String,
                user.Issuer));
        }

        if (duplicateSession)
        {
            claims.Add(new Claim(
                FinOpsDeskCookieClaims.SessionId,
                Guid.NewGuid().ToString("D"),
                ClaimValueTypes.String,
                user.Issuer));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            "name",
            roleType: null));
    }

    private HttpClient ClientWithSession(string sessionCookie)
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie",
            "__Host-finopsdesk-session=" + sessionCookie);
        return client;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, OidcProtocolStub stub)
    {
        HttpResponseMessage callback = await CompleteCallbackAsync(client, stub);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return callback;
    }

    private static async Task<HttpResponseMessage> CompleteCallbackAsync(HttpClient client, OidcProtocolStub stub)
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
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<DateTimeOffset> ReadExpiryAsync(HttpClient client)
    {
        using JsonDocument document = await ReadSessionAsync(client);
        return document.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
    }

    private static async Task<HttpResponseMessage> SelectAsync(HttpClient client, string csrf, Guid organizationId)
    {
        return await PostAsync(
            client,
            "/api/session/organization",
            csrf,
            new StringContent(
                "{\"organizationId\":\"" + organizationId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string csrf,
        HttpContent? content = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return await client.SendAsync(request);
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
            string cookie = end < 0 ? value[Prefix.Length..] : value[Prefix.Length..end];
            return cookie.Length == 0 ? null : cookie;
        }

        return null;
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

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }
}