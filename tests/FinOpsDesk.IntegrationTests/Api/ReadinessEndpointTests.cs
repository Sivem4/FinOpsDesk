using System.Net;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Observability;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReadinessEndpointTests : IDisposable
{
    private const string Marker = "finopsdesk-secret-marker";

    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly RequestLogCollector _collector = new();
    private readonly AccessOperationCounter _access = new();
    private readonly FinOpsDeskApiFactory _factory;

    public ReadinessEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = new FinOpsDeskApiFactory(
            fixture.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(_collector);
                services.AddSingleton(_access);
                services.RemoveAll<IBrowserSessionStore>();
                services.AddScoped<IBrowserSessionStore>(provider => new CountingBrowserSessionStore(
                    ActivatorUtilities.CreateInstance<BrowserSessionStore>(provider),
                    provider.GetRequiredService<AccessOperationCounter>()));
                services.RemoveAll<IOrganizationMembershipReader>();
                services.AddScoped<IOrganizationMembershipReader>(provider => new CountingMembershipReader(
                    ActivatorUtilities.CreateInstance<OrganizationMembershipReader>(provider),
                    provider.GetRequiredService<AccessOperationCounter>()));
            },
            "Production",
            AuthenticationCompositions.ProductionCookie,
            new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = _stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });
    }

    [Fact]
    public async Task Sql_probe_reports_ready_and_health_stays_healthy()
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _collector.Clear();
        HttpResponseMessage ready = await client.GetAsync("/ready");
        string readyPayload = await ready.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using JsonDocument readyBody = JsonDocument.Parse(readyPayload);
        Assert.Equal("ready", readyBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("no-store", ready.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("Server=", readyPayload, StringComparison.OrdinalIgnoreCase);
        RequestLogEntry readyLog = Assert.Single(_collector.Entries);
        Assert.Equal(200, readyLog.State["statusCode"]);
        Assert.Equal(RequestLog.ReadyRoute, readyLog.State["route"]);
        Assert.Equal(RequestLog.OutcomeCompleted, readyLog.State["outcome"]);

        _collector.Clear();
        HttpResponseMessage health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", (await health.Content.ReadAsStringAsync()).Trim());
        Assert.Equal("/health", Assert.Single(_collector.Entries).State["route"]);
    }

    [Fact]
    public async Task Unreachable_sql_is_not_ready_without_exception_text()
    {
        using FinOpsDeskApiFactory factory = new(
            "Server=127.0.0.1,1;Database=FinOpsDesk_IT_unreachable;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False");
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/ready");
        string payload = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(payload);
        Assert.Equal("not-ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("status", Assert.Single(body.RootElement.EnumerateObject()).Name);
        Assert.DoesNotContain("SqlException", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_with_a_valid_cookie_skips_access_and_session_still_validates()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client);
        _access.Reset();
        _collector.Clear();

        HttpResponseMessage ready = await client.GetAsync("/ready?" + Marker);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.False(ready.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, _access.Total);
        RequestLogEntry readyLog = Assert.Single(_collector.Entries);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(readyLog.State), StringComparison.Ordinal);

        _collector.Clear();
        HttpResponseMessage session = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.True(_access.Total > 0);
        Assert.Equal("/api/session", Assert.Single(_collector.Entries).State["route"]);

        _collector.Clear();
        HttpResponseMessage invoices = await _factory.CreateClient().GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, invoices.StatusCode);
        Assert.Equal(401, Assert.Single(_collector.Entries).State["statusCode"]);
    }

    [Fact]
    public async Task Login_redirect_and_oidc_callback_use_safe_route_labels()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        _collector.Clear();
        HttpResponseMessage redirect = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        RequestLogEntry loginLog = Assert.Single(_collector.Entries);
        Assert.Equal("/login", loginLog.State["route"]);
        Assert.Equal(302, loginLog.State["statusCode"]);
        Assert.DoesNotContain(OidcProtocolStub.ClientSecret, JsonSerializer.Serialize(loginLog.State), StringComparison.Ordinal);
        Assert.DoesNotContain("code=", JsonSerializer.Serialize(loginLog.State), StringComparison.Ordinal);

        using HttpRequestMessage callback = new(
            HttpMethod.Get,
            "/signin-oidc?code=" + Marker + "&state=" + Marker);
        _collector.Clear();
        await client.SendAsync(callback);
        RequestLogEntry callbackLog = Assert.Single(_collector.Entries);
        Assert.Equal(RequestLog.OidcCallbackRoute, callbackLog.State["route"]);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(callbackLog.State), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/ready")]
    [InlineData("/Ready")]
    public void Callback_path_collision_stops_startup(string callbackPath)
    {
        FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = "https://oidc.invalid",
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "true",
                ["Authentication:Oidc:CallbackPath"] = callbackPath,
            });
        using (factory)
        {
            Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains(
                "Authentication:Oidc:CallbackPath must not match the readiness path /ready.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task SignInAsync(HttpClient client)
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
        callback.Dispose();
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");
    }
}

internal sealed class AccessOperationCounter
{
    public int Total => MembershipResolves + SessionFinds + SessionCreates + SessionRevokes + MembershipLists + Retires;

    public int MembershipResolves { get; set; }

    public int SessionFinds { get; set; }

    public int SessionCreates { get; set; }

    public int SessionRevokes { get; set; }

    public int MembershipLists { get; set; }

    public int Retires { get; set; }

    public void Reset()
    {
        MembershipResolves = 0;
        SessionFinds = 0;
        SessionCreates = 0;
        SessionRevokes = 0;
        MembershipLists = 0;
        Retires = 0;
    }
}

internal sealed class CountingBrowserSessionStore(
    BrowserSessionStore inner,
    AccessOperationCounter counter) : IBrowserSessionStore
{
    public Task<Guid> CreateAsync(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        counter.SessionCreates++;
        return inner.CreateAsync(userId, createdAt, expiresAt, cancellationToken);
    }

    public Task<BrowserSessionState?> FindAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        counter.SessionFinds++;
        return inner.FindAsync(sessionId, cancellationToken);
    }

    public Task<BrowserSessionRevocation> RevokeAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        counter.SessionRevokes++;
        return inner.RevokeAsync(sessionId, userId, revokedAt, cancellationToken);
    }
}

internal sealed class CountingMembershipReader(
    OrganizationMembershipReader inner,
    AccessOperationCounter counter) : IOrganizationMembershipReader
{
    public Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        counter.MembershipLists++;
        return inner.ListActiveAsync(issuer, subject, cancellationToken);
    }

    public Task<MembershipResolution> ResolveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        counter.MembershipResolves++;
        return inner.ResolveAsync(issuer, subject, cancellationToken);
    }

    public Task RetireDisabledSessionAsync(
        Guid userId,
        Guid observedStamp,
        CancellationToken cancellationToken)
    {
        counter.Retires++;
        return inner.RetireDisabledSessionAsync(userId, observedStamp, cancellationToken);
    }
}