using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class OidcBrowserSessionTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;
    private readonly FinOpsDeskApiFactory _factory;

    public OidcBrowserSessionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
        _factory = CreateFactory(_stub, fixture.ConnectionString);
    }

    [Fact]
    public async Task Anonymous_session_is_not_authenticated_and_issues_a_csrf_token()
    {
        using HttpClient client = _factory.CreateBrowserClient();

        HttpResponseMessage response = await client.GetAsync("/api/session");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(document.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("csrfToken").GetString()));
        Assert.DoesNotContain(OidcProtocolStub.AccessTokenMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_establishes_a_cookie_session_without_financial_access()
    {
        using HttpClient client = _factory.CreateBrowserClient();

        HttpResponseMessage callback = await SignInAsync(client, _stub);
        string sessionCookie = RequireSetCookie(callback, "__Host-finopsdesk-session");
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage session = await client.GetAsync("/api/session");
        string body = await session.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(document.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(_stub.Authority, document.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(OidcProtocolStub.Subject, document.RootElement.GetProperty("subject").GetString());
        Assert.Equal(OidcProtocolStub.DisplayName, document.RootElement.GetProperty("displayName").GetString());
        Assert.True(document.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(7));
        Assert.DoesNotContain(OidcProtocolStub.AccessTokenMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(OidcProtocolStub.RejectedEmail, body, StringComparison.Ordinal);
        Assert.DoesNotContain(OidcProtocolStub.RejectedOrganizationId, body, StringComparison.Ordinal);

        HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Forbidden, invoices.StatusCode);
        Assert.Null(invoices.Headers.Location);
    }

    [Fact]
    public async Task Validated_callback_uses_the_json_web_token_handler_and_denies_invoices()
    {
        string? eventTokenType = null;
        using FinOpsDeskApiFactory factory = CreateFactory(
            _stub,
            _fixture.ConnectionString,
            services => services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    Func<TokenValidatedContext, Task> previous = options.Events.OnTokenValidated;
                    options.Events.OnTokenValidated = context =>
                    {
                        eventTokenType = context.SecurityToken?.GetType().FullName;
                        return previous(context);
                    };
                }));

        OpenIdConnectOptions runtime = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(runtime.UseSecurityTokenValidator);
        Assert.Equal(
            "Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler",
            runtime.TokenHandler.GetType().FullName);
        Assert.False(runtime.MapInboundClaims);
        Assert.True(runtime.TokenValidationParameters.ValidateIssuer);
        Assert.True(runtime.TokenValidationParameters.ValidateAudience);
        Assert.True(runtime.TokenValidationParameters.ValidateLifetime);
        Assert.True(runtime.TokenValidationParameters.RequireSignedTokens);
        Assert.True(runtime.ProtocolValidator.RequireNonce);
        Assert.False(runtime.ProtocolValidator.RequireStateValidation);

        using HttpClient client = factory.CreateBrowserClient();
        HttpResponseMessage callback = await SignInAsync(client, _stub);
        RequireSetCookie(callback, "__Host-finopsdesk-session");

        using JsonDocument document = await ReadSessionAsync(client);
        Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(document.RootElement.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(_stub.Authority, document.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(OidcProtocolStub.Subject, document.RootElement.GetProperty("subject").GetString());
        Assert.DoesNotContain(OidcProtocolStub.RejectedEmail, document.RootElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            OidcProtocolStub.RejectedOrganizationId,
            document.RootElement.ToString(),
            StringComparison.Ordinal);

        HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Forbidden, invoices.StatusCode);

        // OpenIdConnectHandler 10.0.12 validates through TokenHandler, then
        // JwtSecurityTokenConverter copies that JsonWebToken onto the event.
        Assert.Equal(typeof(JwtSecurityToken).FullName, eventTokenType);
    }

    [Fact]
    public async Task Login_redirect_uses_the_stub_and_loose_correlation_cookies()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);

        HttpResponseMessage response = await client.SendAsync(login);
        string correlation = RequireSetCookie(response, ".AspNetCore.Correlation.");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string location = response.Headers.Location?.AbsoluteUri ?? string.Empty;
        Assert.StartsWith(_stub.Authority + "/authorize?", location, StringComparison.Ordinal);
        Assert.Contains("response_type=code", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(OidcProtocolStub.RedirectUri), location, StringComparison.Ordinal);
        Assert.DoesNotContain("response_mode=form_post", location, StringComparison.Ordinal);
        Assert.DoesNotContain("offline_access", location, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=openid%20profile", location, StringComparison.Ordinal);
        Assert.Contains("samesite=none", correlation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", correlation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("samesite=strict", correlation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_without_csrf_does_not_redirect()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await ReadCsrfAsync(client);

        HttpResponseMessage response = await client.PostAsync("/login?returnUrl=/", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(FinOpsDeskCsrf.ProblemCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task External_return_url_is_rejected()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=https://evil.invalid/phish");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);

        HttpResponseMessage response = await client.SendAsync(login);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("auth.return_url.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(OidcTokenFault.WrongKey)]
    [InlineData(OidcTokenFault.WrongIssuer)]
    [InlineData(OidcTokenFault.WrongAudience)]
    [InlineData(OidcTokenFault.WrongNonce)]
    [InlineData(OidcTokenFault.TokenEndpointUnavailable)]
    public async Task Failed_token_redemption_does_not_issue_a_session(OidcTokenFault fault)
    {
        using HttpClient client = _factory.CreateBrowserClient();
        _stub.ArmNextToken(fault);

        await SignInAsync(client, _stub);

        using JsonDocument document = await ReadSessionAsync(client);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Callback_with_a_forged_state_does_not_issue_a_session()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        await client.SendAsync(login);

        HttpResponseMessage callback = await client.GetAsync("/signin-oidc?code=forged&state=forged");
        string body = callback.Headers.Contains("Set-Cookie")
            ? string.Join('\n', callback.Headers.GetValues("Set-Cookie"))
            : string.Empty;

        Assert.DoesNotContain("__Host-finopsdesk-session", body, StringComparison.Ordinal);
        using JsonDocument document = await ReadSessionAsync(client);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_csrf_token_cannot_log_out_an_authenticated_session()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        string anonymousToken = await ReadCsrfAsync(client);
        await SignInAsync(client, _stub);

        HttpResponseMessage rejected = await PostAsync(client, "/logout", anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using JsonDocument stillSignedIn = await ReadSessionAsync(client);
        Assert.True(stillSignedIn.RootElement.GetProperty("authenticated").GetBoolean());

        string sessionToken = await ReadCsrfAsync(client);
        HttpResponseMessage accepted = await PostAsync(client, "/logout", sessionToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using JsonDocument signedOut = await ReadSessionAsync(client);
        Assert.False(signedOut.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Logout_without_csrf_keeps_the_session()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);

        HttpResponseMessage response = await client.PostAsync("/logout", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = await ReadSessionAsync(client);
        Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Financial_routes_stay_forbidden_for_an_oidc_principal()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        string csrf = await ReadCsrfAsync(client);

        HttpResponseMessage missing = await client.PostAsync(
            "/api/allocations",
            JsonBody());
        HttpResponseMessage present = await PostAsync(client, "/api/allocations", csrf, JsonBody());

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, present.StatusCode);
        Assert.DoesNotContain(
            FinOpsDeskCsrf.ProblemCode,
            await missing.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            FinOpsDeskCsrf.ProblemCode,
            await present.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorized_callers_still_need_csrf_before_financial_handlers()
    {
        using FinOpsDeskApiFactory openFactory = CreateFactory(
            _stub,
            _fixture.ConnectionString,
            services => services.AddSingleton<IAuthorizationHandler, SucceedAllRequirementsHandler>());
        using HttpClient client = openFactory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        string csrf = await ReadCsrfAsync(client);

        HttpResponseMessage missing = await client.PostAsync("/api/allocations", JsonBody());
        HttpResponseMessage multipart = await client.PostAsync("/api/imports/invoices", CsvBody());
        HttpResponseMessage review = await client.PostAsync("/api/review-cases", JsonBody());
        HttpResponseMessage batch = await client.PostAsync("/api/matches/batch/execute", JsonBody());
        HttpResponseMessage confirm = await client.PostAsync("/api/matches/confirm", JsonBody());
        HttpResponseMessage allowed = await PostAsync(client, "/api/allocations", csrf, JsonBody());

        Assert.Equal(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(missing));
        Assert.Equal(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(multipart));
        Assert.Equal(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(review));
        Assert.Equal(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(batch));
        Assert.Equal(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(confirm));
        Assert.NotEqual(FinOpsDeskCsrf.ProblemCode, await ProblemCodeAsync(allowed));
    }

    [Fact]
    public async Task Development_jwt_does_not_require_cookie_csrf()
    {
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.PostAsync("/api/allocations", JsonBody());
        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(FinOpsDeskCsrf.ProblemCode, body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/login", new StringContent(string.Empty))).StatusCode);
    }

    [Fact]
    public void Production_cookie_without_a_client_secret_does_not_start()
    {
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:ClientSecret"] = "",
            });

        Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "Authentication:Oidc:ClientSecret is required",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_http_authority_does_not_start()
    {
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = "http://idp.example",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });

        Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "HTTP is accepted only for a loopback host",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_cookie_uses_an_eight_hour_absolute_lifetime()
    {
        CookieAuthenticationOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        Assert.False(options.SlidingExpiration);
    }

    [Fact]
    public async Task Cookie_ticket_expires()
    {
        using FinOpsDeskApiFactory factory = CreateFactory(
            _stub,
            _fixture.ConnectionString,
            services => services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.ExpireTimeSpan = TimeSpan.FromSeconds(1)));
        using HttpClient client = factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        await Task.Delay(TimeSpan.FromSeconds(2));

        using JsonDocument document = await ReadSessionAsync(client);
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static FinOpsDeskApiFactory CreateFactory(
        OidcProtocolStub stub,
        string connectionString,
        Action<IServiceCollection>? configureServices = null)
    {
        return new FinOpsDeskApiFactory(
            connectionString,
            configureServices,
            "Production",
            AuthenticationCompositions.ProductionCookie,
            new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });
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
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string csrf,
        HttpContent? content = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = content ?? new StringContent(string.Empty),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static StringContent JsonBody()
    {
        return new StringContent("{}", Encoding.UTF8, "application/json");
    }

    private static MultipartFormDataContent CsvBody()
    {
        MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent("ExternalId,InvoiceNumber,Amount,Currency\n"u8.ToArray()), "file", "invoices.csv");
        content.Add(new StringContent("PLAY"), "sourceSystem");
        return content;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }

    private static string RequireSetCookie(HttpResponseMessage response, string namePrefix)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values));
        string? match = values.FirstOrDefault(value => value.StartsWith(namePrefix, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(match));
        return match!;
    }

    private sealed class SucceedAllRequirementsHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (IAuthorizationRequirement requirement in context.Requirements)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}