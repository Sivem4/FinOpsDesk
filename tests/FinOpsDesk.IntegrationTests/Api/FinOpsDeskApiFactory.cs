using System.Net.Http.Headers;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

internal class FinOpsDeskApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = "ConnectionStrings__FinOpsDesk";
    private const string CompositionVariable = "Authentication__Composition";

    private readonly string _connectionString;
    private readonly string? _previousConnectionString;
    private readonly string? _previousComposition;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _environmentName;
    private readonly string _authenticationComposition;
    private readonly IReadOnlyDictionary<string, string?>? _configurationOverrides;
    private readonly bool _financialReadAuthorizationEnabled;
    private readonly bool _financialWriteAuthorizationEnabled;
    private readonly bool _financialClassificationProbesEnabled;
    private readonly string? _previousFinancialReadAuthorization;
    private readonly string? _previousFinancialWriteAuthorization;
    private readonly string? _previousFinancialClassificationProbes;

    public FinOpsDeskApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        string? environmentName = null,
        string? authenticationComposition = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null,
        bool financialReadAuthorizationEnabled = false,
        bool financialWriteAuthorizationEnabled = false,
        bool financialClassificationProbesEnabled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _configureServices = configureServices;
        _environmentName = string.IsNullOrWhiteSpace(environmentName)
            ? "Development"
            : environmentName;
        _authenticationComposition = string.IsNullOrWhiteSpace(authenticationComposition)
            ? AuthenticationCompositions.DevelopmentJwt
            : authenticationComposition;
        _configurationOverrides = configurationOverrides;
        _financialReadAuthorizationEnabled = financialReadAuthorizationEnabled;
        _financialWriteAuthorizationEnabled = financialWriteAuthorizationEnabled;
        _financialClassificationProbesEnabled = financialClassificationProbesEnabled;

        // Program reads the connection string and authentication composition
        // while the host is composed. Environment variables are already loaded
        // then and override appsettings. ConfigureAppConfiguration runs later,
        // in time for JWT options but not for that earlier read. Dispose
        // restores the values captured here so a nested factory does not clear
        // a value an outer factory still needs.
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousComposition = Environment.GetEnvironmentVariable(CompositionVariable);
        _previousFinancialReadAuthorization = Environment.GetEnvironmentVariable(
            "Authentication__FinancialReadAuthorization");
        _previousFinancialWriteAuthorization = Environment.GetEnvironmentVariable(
            "Authentication__FinancialWriteAuthorization");
        _previousFinancialClassificationProbes = Environment.GetEnvironmentVariable(
            "Authentication__FinancialClassificationProbes");
        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);
        Environment.SetEnvironmentVariable(CompositionVariable, _authenticationComposition);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialReadAuthorization",
            _financialReadAuthorizationEnabled ? FinancialReadAuthorization.Enabled : string.Empty);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialWriteAuthorization",
            _financialWriteAuthorizationEnabled ? FinancialWriteAuthorization.Enabled : string.Empty);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialClassificationProbes",
            _financialClassificationProbesEnabled
                ? FinancialClassificationProbeEndpoints.Enabled
                : string.Empty);
    }

    public HttpClient CreateApiClient(string? accessToken = null)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public HttpClient CreateBrowserClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environmentName);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Dictionary<string, string?> settings = new()
            {
                ["ConnectionStrings:FinOpsDesk"] = _connectionString,
                [AuthenticationCompositions.ConfigurationKey] = _authenticationComposition,
                ["Authentication:JwtBearer:Authority"] = "",
                ["Authentication:JwtBearer:Issuer"] = TestAccessTokens.Issuer,
                ["Authentication:JwtBearer:Audience"] = TestAccessTokens.Audience,
                ["Authentication:JwtBearer:SigningKey"] = TestAccessTokens.SigningKey,
                ["Authentication:JwtBearer:RequireHttpsMetadata"] = "false",
                ["Authentication:JwtBearer:OrganizationClaimType"] = TestAccessTokens.OrganizationClaimType,
            };

            if (string.Equals(
                _authenticationComposition,
                AuthenticationCompositions.ProductionCookie,
                StringComparison.Ordinal))
            {
                settings["Authentication:Oidc:Authority"] = "https://oidc.invalid";
                settings["Authentication:Oidc:ClientId"] = "finopsdesk-test-client";
                settings["Authentication:Oidc:ClientSecret"] = "finopsdesk-test-client-secret-not-production";
                settings["Authentication:Oidc:BrowserOrigin"] = "https://localhost";
                settings["Authentication:Oidc:RequireHttpsMetadata"] = "true";
            }

            settings[FinancialReadAuthorization.ConfigurationKey] = _financialReadAuthorizationEnabled
                ? FinancialReadAuthorization.Enabled
                : string.Empty;
            settings[FinancialWriteAuthorization.ConfigurationKey] = _financialWriteAuthorizationEnabled
                ? FinancialWriteAuthorization.Enabled
                : string.Empty;
            settings[FinancialClassificationProbeEndpoints.ConfigurationKey] = _financialClassificationProbesEnabled
                ? FinancialClassificationProbeEndpoints.Enabled
                : string.Empty;

            if (_configurationOverrides is not null)
            {
                foreach ((string key, string? value) in _configurationOverrides)
                {
                    settings[key] = value;
                }
            }

            configuration.AddInMemoryCollection(settings);
        });

        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable(ConnectionStringVariable, _previousConnectionString);
        Environment.SetEnvironmentVariable(CompositionVariable, _previousComposition);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialReadAuthorization",
            _previousFinancialReadAuthorization);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialWriteAuthorization",
            _previousFinancialWriteAuthorization);
        Environment.SetEnvironmentVariable(
            "Authentication__FinancialClassificationProbes",
            _previousFinancialClassificationProbes);
    }
}