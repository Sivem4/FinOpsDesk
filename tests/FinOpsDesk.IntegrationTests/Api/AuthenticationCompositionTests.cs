using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AuthenticationCompositionTests
{
    private readonly SqlServerFixture _fixture;

    public AuthenticationCompositionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task E2E_environment_keeps_jwt_bearer_and_rejects_a_missing_token()
    {
        using FinOpsDeskApiFactory factory = CreateFactory(
            AuthenticationCompositions.E2EEnvironmentName,
            AuthenticationCompositions.DevelopmentJwt);
        using HttpClient anonymous = factory.CreateApiClient();
        using HttpClient member = factory.CreateApiClient(
            TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage missing = await anonymous.GetAsync("/api/invoices");
        HttpResponseMessage accepted = await member.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await AssertSchemesAsync(factory, bearer: true, cookie: false);
    }

    [Fact]
    public async Task Production_cookie_composition_ignores_a_valid_bearer_token()
    {
        using FinOpsDeskApiFactory factory = CreateFactory(
            "Production",
            AuthenticationCompositions.ProductionCookie);
        using HttpClient client = factory.CreateApiClient(
            TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
        HttpResponseMessage allocation = await client.PostAsync(
            "/api/allocations",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        using HttpClient health = factory.CreateApiClient();
        HttpResponseMessage healthResponse = await health.GetAsync("/health");

        Assert.Equal(HttpStatusCode.Unauthorized, invoices.StatusCode);
        Assert.Null(invoices.Headers.Location);
        Assert.False(invoices.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, allocation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        await AssertSchemesAsync(factory, bearer: false, cookie: true);
    }

    [Fact]
    public async Task Production_cookie_composition_rejects_an_authenticated_organization_claim()
    {
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, AlwaysAuthenticatedHandler>(
                        AlwaysAuthenticatedHandler.SchemeName,
                        _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = AlwaysAuthenticatedHandler.SchemeName;
                    options.DefaultChallengeScheme = AlwaysAuthenticatedHandler.SchemeName;
                });
            },
            "Production",
            AuthenticationCompositions.ProductionCookie);
        using HttpClient client = factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Development_can_select_the_production_cookie_composition()
    {
        using FinOpsDeskApiFactory factory = CreateFactory(
            "Development",
            AuthenticationCompositions.ProductionCookie);
        using HttpClient client = factory.CreateApiClient(
            TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertSchemesAsync(factory, bearer: false, cookie: true);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Development_jwt_is_rejected_outside_development_and_e2e(string environmentName)
    {
        using FinOpsDeskApiFactory factory = CreateFactory(
            environmentName,
            AuthenticationCompositions.DevelopmentJwt);

        Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "DevelopmentJwt is only allowed in the Development and E2E environments.",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_composition_is_rejected()
    {
        using FinOpsDeskApiFactory factory = CreateFactory("Development", "Both");

        Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "Authentication:Composition must be DevelopmentJwt or ProductionCookie.",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private FinOpsDeskApiFactory CreateFactory(string environmentName, string composition)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            environmentName: environmentName,
            authenticationComposition: composition);
    }

    private static async Task AssertSchemesAsync(
        FinOpsDeskApiFactory factory,
        bool bearer,
        bool cookie)
    {
        IAuthenticationSchemeProvider schemes = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Equal(bearer, await schemes.GetSchemeAsync("Bearer") is not null);
        Assert.Equal(cookie, await schemes.GetSchemeAsync("Cookies") is not null);
    }

    private sealed class AlwaysAuthenticatedHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestIntruder";

        public AlwaysAuthenticatedHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new("org_id", "11111111-1111-1111-1111-111111111111"),
                new("sub", "phase-7a-intruder"),
            ];
            ClaimsIdentity identity = new(claims, Scheme.Name);
            AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}