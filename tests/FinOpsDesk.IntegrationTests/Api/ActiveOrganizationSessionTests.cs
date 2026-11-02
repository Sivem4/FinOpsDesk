using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ActiveOrganizationSessionTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public ActiveOrganizationSessionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = CreateFactory(_stub, fixture.ConnectionString, services =>
        {
            services.AddSingleton<IStartupFilter, VerifiedAccessProbeStartupFilter>();
        });
    }

    [Fact]
    public async Task Membership_without_selection_has_no_trusted_tenant()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid first = await GrantAsync(user.Id, "North Ledger");
        Guid second = await GrantAsync(user.Id, "South Ledger");
        try
        {
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.Equal(2, session.RootElement.GetProperty("organizations").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("activeOrganization").ValueKind);
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());

            using JsonDocument probe = await ReadProbeAsync(client, Guid.NewGuid());
            Assert.Equal("Active", probe.RootElement.GetProperty("status").GetString());
            Assert.True(probe.RootElement.GetProperty("contextThrew").GetBoolean());
            Assert.Equal(JsonValueKind.Null, probe.RootElement.GetProperty("organizationId").ValueKind);
            Assert.Equal(user.Id.ToString("D"), probe.RootElement.GetProperty("actorId").GetString());
            Assert.NotEqual(OidcProtocolStub.Subject, probe.RootElement.GetProperty("actorId").GetString());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, first, second);
        }
    }

    [Fact]
    public async Task Explicit_selection_sets_only_that_organization()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid allowed = await GrantAsync(user.Id, "North Ledger");
        Guid foreign = Guid.NewGuid();
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.Organizations.Add(Organization.Create(foreign, "Foreign Ledger", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        try
        {
            string csrf = await ReadCsrfAsync(client);
            HttpResponseMessage selected = await SelectAsync(client, csrf, allowed);
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
            Assert.Contains("no-store", selected.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

            using JsonDocument session = await ReadSessionAsync(client);
            JsonElement active = session.RootElement.GetProperty("activeOrganization");
            Assert.Equal(allowed, active.GetProperty("organizationId").GetGuid());
            Assert.Equal("North Ledger", active.GetProperty("displayName").GetString());
            Assert.False(session.RootElement.GetProperty("financialAccess").GetBoolean());

            using JsonDocument probe = await ReadProbeAsync(client, foreign);
            Assert.Equal(allowed, probe.RootElement.GetProperty("organizationId").GetGuid());
            Assert.False(probe.RootElement.GetProperty("contextThrew").GetBoolean());
            Assert.Equal(user.Id.ToString("D"), probe.RootElement.GetProperty("actorId").GetString());

            HttpResponseMessage denied = await SelectAsync(client, csrf, foreign);
            string deniedBody = await denied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains("auth.organization.forbidden", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Foreign Ledger", deniedBody, StringComparison.Ordinal);
            Assert.False(SetsSessionCookie(denied));

            using JsonDocument still = await ReadSessionAsync(client);
            Assert.Equal(allowed, still.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/invoices")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsync(
                    "/api/allocations",
                    new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, allowed, foreign);
        }
    }

    [Theory]
    [InlineData("{\"organizationId\":\"not-a-guid\"}")]
    [InlineData("{\"organizationId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"organizationId\":\"11111111-1111-1111-1111-111111111111\",\"extra\":true}")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Malformed_selection_body_is_rejected(string body)
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization");
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("auth.organization.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(SetsSessionCookie(response));
    }

    [Fact]
    public async Task Organization_switch_body_is_limited_to_4096_bytes()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid allowed = await GrantAsync(user.Id, "North Ledger");
        try
        {
            string csrf = await ReadCsrfAsync(client);
            HttpResponseMessage accepted = await SelectAsync(client, csrf, allowed);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

            string declared = new string('x', 4097);
            using (HttpContent declaredContent = new StringContent(declared, Encoding.UTF8, "application/json"))
            {
                Assert.Equal(4097, declaredContent.Headers.ContentLength);
                await AssertRejectedOrganizationBodyAsync(client, csrf, declaredContent);
            }

            byte[] chunked = new byte[8192];
            chunked.AsSpan().Fill((byte)'{');
            using (HttpContent chunkedContent = UnmeasuredJson(chunked))
            {
                Assert.Null(chunkedContent.Headers.ContentLength);
                await AssertRejectedOrganizationBodyAsync(client, csrf, chunkedContent);
            }

            string multibyte = new string('\u20AC', 1366);
            byte[] multibyteBytes = Encoding.UTF8.GetBytes(multibyte);
            Assert.True(multibyte.Length <= 4096);
            Assert.True(multibyteBytes.Length > 4096);
            using (HttpContent multibyteContent = UnmeasuredJson(multibyteBytes))
            {
                Assert.Null(multibyteContent.Headers.ContentLength);
                await AssertRejectedOrganizationBodyAsync(client, csrf, multibyteContent);
            }

            using JsonDocument session = await ReadSessionAsync(client);
            Assert.Equal(
                allowed,
                session.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, allowed);
        }
    }

    [Fact]
    public async Task Selection_requires_the_authenticated_csrf_token()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string anonymous = await ReadCsrfAsync(client);
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid allowed = await GrantAsync(user.Id, "North Ledger");
        try
        {
            HttpResponseMessage rejected = await SelectAsync(client, anonymous, allowed);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("auth.csrf.invalid", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using JsonDocument session = await ReadSessionAsync(client);
            Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("activeOrganization").ValueKind);

            string csrf = await ReadCsrfAsync(client);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, allowed)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/logout", csrf)).StatusCode);
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, allowed);
        }
    }

    [Fact]
    public async Task Selection_without_a_cookie_is_unauthorized()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);

        HttpResponseMessage response = await SelectAsync(client, csrf, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cookie_cannot_be_restored()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string? ticket = SessionCookie(callback);
        Assert.False(string.IsNullOrEmpty(ticket));
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid stamp = user.SessionStamp;
        await SetStatusAsync(user.Id, ApplicationUserStatus.Disabled);
        try
        {
            HttpResponseMessage session = await client.GetAsync("/api/session");
            using JsonDocument document = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.True(ClearsSessionCookie(session));

            using HttpClient replay = ClientWithSession(ticket!);
            using JsonDocument again = await ReadSessionAsync(replay);
            Assert.False(again.RootElement.GetProperty("authenticated").GetBoolean());

            await SetStatusAsync(user.Id, ApplicationUserStatus.Active);
            using HttpClient restored = ClientWithSession(ticket!);
            using JsonDocument afterRestore = await ReadSessionAsync(restored);
            Assert.False(afterRestore.RootElement.GetProperty("authenticated").GetBoolean());
            ApplicationUser current = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
            Assert.NotEqual(stamp, current.SessionStamp);
        }
        finally
        {
            await SetStatusAsync(user.Id, ApplicationUserStatus.Active);
        }
    }

    [Fact]
    public async Task Disable_then_restore_without_a_request_rejects_the_original_cookie()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string ticket = SessionCookie(callback)
            ?? throw new InvalidOperationException("Session cookie was missing.");
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid originalStamp = user.SessionStamp;
        try
        {
            await SetStatusAsync(user.Id, ApplicationUserStatus.Disabled);
            ApplicationUser disabled = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
            Assert.Equal(ApplicationUserStatus.Disabled, disabled.Status);
            Assert.NotEqual(originalStamp, disabled.SessionStamp);

            await SetStatusAsync(user.Id, ApplicationUserStatus.Active);
            ApplicationUser restored = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
            Assert.Equal(ApplicationUserStatus.Active, restored.Status);
            Assert.Equal(disabled.SessionStamp, restored.SessionStamp);

            using HttpClient replay = ClientWithSession(ticket);
            HttpResponseMessage session = await replay.GetAsync("/api/session");
            using JsonDocument document = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.True(ClearsSessionCookie(session));

            using HttpClient fresh = _factory.CreateBrowserClient();
            HttpResponseMessage secondLogin = await SignInAsync(fresh, _stub);
            Assert.False(string.IsNullOrEmpty(SessionCookie(secondLogin)));
            Assert.NotEqual(ticket, SessionCookie(secondLogin));
            using JsonDocument authenticated = await ReadSessionAsync(fresh);
            Assert.True(authenticated.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await SetStatusAsync(user.Id, ApplicationUserStatus.Active);
        }
    }

    [Fact]
    public async Task Recreated_user_does_not_accept_the_old_ticket()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string ticket = SessionCookie(callback) ?? throw new InvalidOperationException("Session cookie was missing.");
        ApplicationUser original = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        await DeleteUserAsync(original.Id);

        using JsonDocument signedOut = await ReadSessionAsync(client);
        Assert.False(signedOut.RootElement.GetProperty("authenticated").GetBoolean());

        ApplicationUser recreated = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            OidcProtocolStub.Subject,
            null,
            DateTimeOffset.UtcNow);
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.ApplicationUsers.Add(recreated);
            await context.SaveChangesAsync();
        }

        using HttpClient replay = ClientWithSession(ticket);
        using JsonDocument replayed = await ReadSessionAsync(replay);
        Assert.False(replayed.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.NotEqual(original.Id, recreated.Id);
        Assert.NotEqual(original.SessionStamp, recreated.SessionStamp);
    }

    [Fact]
    public async Task Revoked_membership_is_forgotten_until_the_same_membership_returns()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid organizationId = await GrantAsync(user.Id, "North Ledger");
        try
        {
            string csrf = await ReadCsrfAsync(client);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
            DateTimeOffset expires = await ReadExpiryAsync(client);
            await RevokeAsync(user.Id, organizationId);

            HttpResponseMessage session = await client.GetAsync("/api/session");
            using JsonDocument document = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("activeOrganization").ValueKind);
            Assert.Equal(0, document.RootElement.GetProperty("organizations").GetArrayLength());
            Assert.False(SetsSessionCookie(session));
            Assert.Equal(expires, document.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
            using JsonDocument probe = await ReadProbeAsync(client, organizationId);
            Assert.True(probe.RootElement.GetProperty("contextThrew").GetBoolean());
            Assert.Equal(JsonValueKind.Null, probe.RootElement.GetProperty("organizationId").ValueKind);

            await RestoreMembershipAsync(user.Id, organizationId);
            using JsonDocument restored = await ReadSessionAsync(client);
            Assert.Equal(
                organizationId,
                restored.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
            Assert.Equal(expires, restored.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/invoices")).StatusCode);
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Disabled_organization_is_not_a_trusted_tenant()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid organizationId = await GrantAsync(user.Id, "Closed Ledger");
        try
        {
            string csrf = await ReadCsrfAsync(client);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
            await using (AccessDbContext context = _fixture.CreateAccessContext())
            {
                Organization organization = await context.Organizations.SingleAsync(candidate => candidate.Id == organizationId);
                organization.Disable();
                await context.SaveChangesAsync();
            }

            using JsonDocument session = await ReadSessionAsync(client);
            Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("activeOrganization").ValueKind);
            Assert.DoesNotContain("Closed Ledger", session.RootElement.ToString(), StringComparison.Ordinal);
            using JsonDocument probe = await ReadProbeAsync(client, organizationId);
            Assert.True(probe.RootElement.GetProperty("contextThrew").GetBoolean());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Database_outage_keeps_the_cookie_and_grants_nothing()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid organizationId = await GrantAsync(user.Id, "North Ledger");
        try
        {
            string csrf = await ReadCsrfAsync(client);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
            DateTimeOffset expires = await ReadExpiryAsync(client);
            await RenameUsersAsync("ApplicationUsers", "ApplicationUsers_offline");
            try
            {
                HttpResponseMessage session = await client.GetAsync("/api/session");
                string body = await session.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, session.StatusCode);
                Assert.Contains("auth.membership.unavailable", body, StringComparison.Ordinal);
                Assert.DoesNotContain("organizations", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
                Assert.False(SetsSessionCookie(session));

                HttpResponseMessage denied = await SelectAsync(client, csrf, organizationId);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
                Assert.False(SetsSessionCookie(denied));

                using HttpRequestMessage probeRequest = new(HttpMethod.Get, VerifiedAccessProbeMiddleware.Path);
                probeRequest.Headers.TryAddWithoutValidation("X-Organization-Id", Guid.NewGuid().ToString("D"));
                HttpResponseMessage probe = await client.SendAsync(probeRequest);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, probe.StatusCode);
                Assert.Contains("unavailable", await probe.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await RenameUsersAsync("ApplicationUsers_offline", "ApplicationUsers");
            }

            using JsonDocument restored = await ReadSessionAsync(client);
            Assert.True(restored.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.Equal(organizationId, restored.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
            Assert.Equal(expires, restored.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Switching_preserves_the_original_expiry()
    {
        ManualTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync();
        using FinOpsDeskApiFactory factory = CreateFactory(stub, _fixture.ConnectionString, services =>
        {
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.TimeProvider = clock);
            services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.TimeProvider = clock);
        });
        using HttpClient client = factory.CreateBrowserClient();
        await SignInAsync(client, stub);
        ApplicationUser user = await RequireUserAsync(stub.Authority, OidcProtocolStub.Subject);
        Guid first = await GrantAsync(user.Id, "North Ledger");
        Guid second = await GrantAsync(user.Id, "South Ledger");
        try
        {
            DateTimeOffset expires = await ReadExpiryAsync(client);
            Assert.Equal(clock.UtcNow.AddHours(8).ToUnixTimeSeconds(), expires.ToUnixTimeSeconds());
            clock.UtcNow = clock.UtcNow.AddHours(2);
            string csrf = await ReadCsrfAsync(client);
            HttpResponseMessage firstSwitch = await SelectAsync(client, csrf, first);
            Assert.Equal(HttpStatusCode.NoContent, firstSwitch.StatusCode);
            Assert.Equal(expires, await ReadExpiryAsync(client));

            HttpResponseMessage read = await client.GetAsync("/api/session");
            Assert.False(SetsSessionCookie(read));
            Assert.Equal(expires, (await JsonDocument.ParseAsync(await read.Content.ReadAsStreamAsync()))
                .RootElement.GetProperty("expiresAt").GetDateTimeOffset());

            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, second)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, first)).StatusCode);
            Assert.Equal(expires, await ReadExpiryAsync(client));

            await RevokeAsync(user.Id, first);
            Assert.Equal(expires, await ReadExpiryAsync(client));
            clock.UtcNow = expires.AddSeconds(1);
            using JsonDocument expired = await ReadSessionAsync(client);
            Assert.False(expired.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, first, second);
        }
    }

    [Fact]
    public async Task Malformed_selection_claim_does_not_sign_the_user_out()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        string ticket = Protect(CookieSessionPrincipal.Create(
            _stub.Authority,
            OidcProtocolStub.Subject,
            null,
            user.SessionStamp,
            organizationId: null), extraOrganization: "not-a-guid");
        using HttpClient forged = ClientWithSession(ticket);

        using JsonDocument session = await ReadSessionAsync(forged);
        Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("activeOrganization").ValueKind);
    }

    [Fact]
    public async Task Duplicate_identity_claims_are_rejected()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        var claims = new List<Claim>
        {
            new("iss", _stub.Authority, ClaimValueTypes.String, _stub.Authority),
            new("iss", _stub.Authority + "/other", ClaimValueTypes.String, _stub.Authority),
            new("sub", OidcProtocolStub.Subject, ClaimValueTypes.String, _stub.Authority),
            new(FinOpsDeskCookieClaims.SessionStamp, user.SessionStamp.ToString("D"), ClaimValueTypes.String, _stub.Authority),
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            "name",
            roleType: null);
        string ticket = Protect(new ClaimsPrincipal(identity), extraOrganization: null);
        using HttpClient forged = ClientWithSession(ticket);

        using JsonDocument session = await ReadSessionAsync(forged);
        Assert.False(session.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Two_issuers_with_the_same_subject_stay_distinct()
    {
        string keys = Path.Combine(Path.GetTempPath(), "finopsdesk-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keys);
        try
        {
            await using OidcProtocolStub other = await OidcProtocolStub.StartAsync();
            using FinOpsDeskApiFactory firstFactory = CreateFactory(_stub, _fixture.ConnectionString, services =>
            {
                ShareKeys(services, keys);
                services.AddSingleton<IStartupFilter, VerifiedAccessProbeStartupFilter>();
            });
            using FinOpsDeskApiFactory secondFactory = CreateFactory(other, _fixture.ConnectionString, services =>
            {
                ShareKeys(services, keys);
                services.AddSingleton<IStartupFilter, VerifiedAccessProbeStartupFilter>();
            });
            using HttpClient first = firstFactory.CreateBrowserClient();
            using HttpClient second = secondFactory.CreateBrowserClient();
            HttpResponseMessage secondLogin = await SignInAsync(second, other);
            await SignInAsync(first, _stub);
            ApplicationUser firstUser = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
            ApplicationUser secondUser = await RequireUserAsync(other.Authority, OidcProtocolStub.Subject);
            Assert.NotEqual(firstUser.Id, secondUser.Id);

            using JsonDocument firstProbe = await ReadProbeAsync(first, Guid.NewGuid());
            using JsonDocument secondProbe = await ReadProbeAsync(second, Guid.NewGuid());
            Assert.Equal(firstUser.Id.ToString("D"), firstProbe.RootElement.GetProperty("actorId").GetString());
            Assert.Equal(secondUser.Id.ToString("D"), secondProbe.RootElement.GetProperty("actorId").GetString());

            string firstSession = SessionCookie(await SignInAsync(first, _stub))
                ?? throw new InvalidOperationException("Session cookie was missing.");
            using HttpClient firstBare = ClientWithCookies(firstFactory, firstSession, csrfCookie: null);
            (string firstCsrf, string? firstCsrfCookie) = await ReadSessionCredentialsAsync(firstBare);
            string secondSession = SessionCookie(secondLogin)
                ?? throw new InvalidOperationException("Session cookie was missing.");
            Assert.False(string.IsNullOrEmpty(firstCsrfCookie));
            using HttpClient cross = ClientWithCookies(secondFactory, secondSession, firstCsrfCookie);
            HttpResponseMessage rejected = await PostAsync(cross, "/logout", firstCsrf);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using JsonDocument still = await ReadSessionAsync(second);
            Assert.True(still.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            Directory.Delete(keys, recursive: true);
        }
    }

    [Fact]
    public async Task Csrf_tokens_are_isolated_by_subject()
    {
        ApplicationUser first = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            "subject-a",
            null,
            DateTimeOffset.UtcNow);
        ApplicationUser second = ApplicationUser.Create(
            Guid.NewGuid(),
            _stub.Authority,
            "subject-b",
            null,
            DateTimeOffset.UtcNow);
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.ApplicationUsers.AddRange(first, second);
            await context.SaveChangesAsync();
        }

        try
        {
            string sessionB = Protect(
                CookieSessionPrincipal.Create(_stub.Authority, "subject-b", null, second.SessionStamp, null),
                extraOrganization: null);
            using HttpClient clientA = ClientWithSession(Protect(
                CookieSessionPrincipal.Create(_stub.Authority, "subject-a", null, first.SessionStamp, null),
                extraOrganization: null));
            using HttpClient clientB = ClientWithSession(sessionB);
            (string tokenA, string? csrfCookieA) = await ReadSessionCredentialsAsync(clientA);

            using HttpClient cross = ClientWithCookies(_factory, sessionB, csrfCookieA);
            HttpResponseMessage rejected = await PostAsync(cross, "/logout", tokenA);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using JsonDocument still = await ReadSessionAsync(clientB);
            Assert.True(still.RootElement.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await DeleteUserAsync(first.Id);
            await DeleteUserAsync(second.Id);
        }
    }

    [Fact]
    public async Task Concurrent_switches_cannot_select_a_foreign_organization()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string session = SessionCookie(callback) ?? throw new InvalidOperationException("Session cookie was missing.");
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid first = await GrantAsync(user.Id, "North Ledger");
        Guid second = await GrantAsync(user.Id, "South Ledger");
        Guid foreign = Guid.NewGuid();
        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            context.Organizations.Add(Organization.Create(foreign, "Foreign Ledger", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        try
        {
            using HttpClient bare = ClientWithSession(session);
            (string csrf, string? csrfCookie) = await ReadSessionCredentialsAsync(bare);
            Assert.False(string.IsNullOrEmpty(csrfCookie));
            using HttpClient north = ClientWithCookies(_factory, session, csrfCookie);
            using HttpClient south = ClientWithCookies(_factory, session, csrfCookie);
            using HttpClient outsider = ClientWithCookies(_factory, session, csrfCookie);
            Task<HttpResponseMessage> northTask = SelectAsync(north, csrf, first);
            Task<HttpResponseMessage> southTask = SelectAsync(south, csrf, second);
            Task<HttpResponseMessage> foreignTask = SelectAsync(outsider, csrf, foreign);
            HttpResponseMessage[] responses = await Task.WhenAll(northTask, southTask, foreignTask);

            Assert.Equal(HttpStatusCode.NoContent, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, responses[1].StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, responses[2].StatusCode);
            Assert.False(SetsSessionCookie(responses[2]));
            Assert.DoesNotContain(
                "Foreign Ledger",
                await responses[2].Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            string northTicket = SessionCookie(responses[0])
                ?? throw new InvalidOperationException("North selection did not reissue the session cookie.");
            string southTicket = SessionCookie(responses[1])
                ?? throw new InvalidOperationException("South selection did not reissue the session cookie.");
            using HttpClient northReader = ClientWithSession(northTicket);
            using HttpClient southReader = ClientWithSession(southTicket);
            using JsonDocument northSession = await ReadSessionAsync(northReader);
            using JsonDocument southSession = await ReadSessionAsync(southReader);
            Assert.Equal(first, northSession.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
            Assert.Equal(second, southSession.RootElement.GetProperty("activeOrganization").GetProperty("organizationId").GetGuid());
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, first, second, foreign);
        }
    }

    [Fact]
    public async Task Revocation_during_selection_leaves_no_trusted_tenant()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync(_stub.Authority, OidcProtocolStub.Subject);
        Guid organizationId = await GrantAsync(user.Id, "North Ledger");
        try
        {
            string csrf = await ReadCsrfAsync(client);
            Task revoke = RevokeAsync(user.Id, organizationId);
            Task<HttpResponseMessage> select = SelectAsync(client, csrf, organizationId);
            await Task.WhenAll(revoke, select);

            using JsonDocument session = await ReadSessionAsync(client);
            Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("activeOrganization").ValueKind);
            using JsonDocument probe = await ReadProbeAsync(client, organizationId);
            Assert.Equal(JsonValueKind.Null, probe.RootElement.GetProperty("organizationId").ValueKind);
        }
        finally
        {
            await DeleteOrganizationsAsync(user.Id, organizationId);
        }
    }

    [Fact]
    public async Task Development_jwt_does_not_consult_access_membership()
    {
        Guid organizationId = Guid.NewGuid();
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Production_cookie_rejects_bearer_authentication()
    {
        using HttpClient bearer = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestAccessTokens.Create(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/invoices")).StatusCode);
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

    private async Task<Guid> GrantAsync(Guid userId, string name)
    {
        Guid organizationId = Guid.NewGuid();
        await using AccessDbContext context = _fixture.CreateAccessContext();
        context.Organizations.Add(Organization.Create(organizationId, name, DateTimeOffset.UtcNow));
        context.OrganizationMemberships.Add(
            OrganizationMembership.Grant(Guid.NewGuid(), userId, organizationId, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        return organizationId;
    }

    private async Task RevokeAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationMembership membership = await context.OrganizationMemberships
            .SingleAsync(candidate => candidate.UserId == userId && candidate.OrganizationId == organizationId);
        membership.Revoke(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task RestoreMembershipAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        OrganizationMembership membership = await context.OrganizationMemberships
            .SingleAsync(candidate => candidate.UserId == userId && candidate.OrganizationId == organizationId);
        membership.Restore();
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

    private async Task DeleteUserAsync(Guid userId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<OrganizationMembership> memberships = await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId)
            .ToListAsync();
        context.OrganizationMemberships.RemoveRange(memberships);
        ApplicationUser? user = await context.ApplicationUsers.SingleOrDefaultAsync(candidate => candidate.Id == userId);
        if (user is not null)
        {
            context.ApplicationUsers.Remove(user);
        }

        await context.SaveChangesAsync();
    }

    private async Task DeleteOrganizationsAsync(Guid userId, params Guid[] organizationIds)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<OrganizationMembership> memberships = await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId && organizationIds.Contains(membership.OrganizationId))
            .ToListAsync();
        context.OrganizationMemberships.RemoveRange(memberships);
        List<Organization> organizations = await context.Organizations
            .Where(organization => organizationIds.Contains(organization.Id))
            .ToListAsync();
        context.Organizations.RemoveRange(organizations);
        await context.SaveChangesAsync();
    }

    private async Task<ApplicationUser> RequireUserAsync(string issuer, string subject)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<ApplicationUser> rows = await context.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync();
        ApplicationUser? user = rows.SingleOrDefault(candidate =>
            ExternalIdentityKey.Same(candidate.Issuer, candidate.Subject, issuer, subject));
        Assert.NotNull(user);
        return user;
    }

    private async Task RenameUsersAsync(string from, string to)
    {
        if (from is not ("ApplicationUsers" or "ApplicationUsers_offline")
            || to is not ("ApplicationUsers" or "ApplicationUsers_offline"))
        {
            throw new InvalidOperationException("Unexpected access table rename.");
        }

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new("EXEC sp_rename N'access." + from + "', N'" + to + "';", connection);
        await command.ExecuteNonQueryAsync();
    }

    private string Protect(ClaimsPrincipal principal, string? extraOrganization)
    {
        if (extraOrganization is not null)
        {
            ClaimsIdentity identity = (ClaimsIdentity)principal.Identity!;
            identity.AddClaim(new Claim(
                FinOpsDeskCookieClaims.Organization,
                extraOrganization,
                ClaimValueTypes.String,
                identity.FindFirst("iss")?.Value ?? string.Empty));
        }

        CookieAuthenticationOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        DateTimeOffset issued = (options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued.Add(options.ExpireTimeSpan),
            },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    private HttpClient ClientWithSession(string sessionCookie)
    {
        return ClientWithCookies(_factory, sessionCookie, csrfCookie: null);
    }

    private static HttpClient ClientWithCookies(
        FinOpsDeskApiFactory factory,
        string sessionCookie,
        string? csrfCookie)
    {
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        string header = "__Host-finopsdesk-session=" + sessionCookie;
        if (!string.IsNullOrEmpty(csrfCookie))
        {
            header += "; __Host-finopsdesk-csrf=" + csrfCookie;
        }

        client.DefaultRequestHeaders.Add("Cookie", header);
        return client;
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

    private static async Task<HttpResponseMessage> SelectAsync(HttpClient client, string csrf, Guid organizationId)
    {
        return await PostAsync(
            client,
            "/api/session/organization",
            csrf,
            "{\"organizationId\":\"" + organizationId.ToString("D") + "\"}");
    }

    private static async Task AssertRejectedOrganizationBodyAsync(
        HttpClient client,
        string csrf,
        HttpContent content)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization");
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        request.Content = content;
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "auth.organization.invalid",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.False(SetsSessionCookie(response));
        Assert.False(ClearsSessionCookie(response));
    }

    private static HttpContent UnmeasuredJson(byte[] body)
    {
        HttpContent content = new StreamContent(new NonSeekableByteStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = null;
        return content;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string csrf,
        string? json = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        (string token, _) = await ReadSessionCredentialsAsync(client);
        return token;
    }

    private static async Task<(string Token, string? CsrfCookie)> ReadSessionCredentialsAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string token = document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");
        return (token, CsrfCookie(response));
    }

    private static void ShareKeys(IServiceCollection services, string directory)
    {
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .SetApplicationName("FinOpsDesk.ActiveOrganizationTests");
    }

    private static async Task<DateTimeOffset> ReadExpiryAsync(HttpClient client)
    {
        using JsonDocument document = await ReadSessionAsync(client);
        return document.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
    }

    private static async Task<JsonDocument> ReadSessionAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> ReadProbeAsync(HttpClient client, Guid headerOrganization)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, VerifiedAccessProbeMiddleware.Path);
        request.Headers.TryAddWithoutValidation("X-Organization-Id", headerOrganization.ToString("D"));
        HttpResponseMessage response = await client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? CsrfCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        const string Prefix = "__Host-finopsdesk-csrf=";
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

    private static bool SetsSessionCookie(HttpResponseMessage response)
    {
        return SessionCookie(response) is not null;
    }

    private static bool ClearsSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return false;
        }

        foreach (string value in values)
        {
            if (!value.StartsWith("__Host-finopsdesk-session=", StringComparison.Ordinal))
            {
                continue;
            }

            if (value.Contains("expires=", StringComparison.OrdinalIgnoreCase)
                || value.Contains("max-age=0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class NonSeekableByteStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public NonSeekableByteStream(byte[] data)
        {
            _data = data;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            int count = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
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