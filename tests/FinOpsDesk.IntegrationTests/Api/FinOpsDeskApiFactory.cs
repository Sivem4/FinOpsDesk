using System.Net.Http.Headers;

using FinOpsDesk.Api.Authentication;

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

    public FinOpsDeskApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        string? environmentName = null,
        string? authenticationComposition = null)
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

        // Program reads the connection string and authentication composition
        // while the host is composed. Environment variables are already loaded
        // then and override appsettings. ConfigureAppConfiguration runs later,
        // in time for JWT options but not for that earlier read. Dispose
        // restores the values captured here so a nested factory does not clear
        // a value an outer factory still needs.
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousComposition = Environment.GetEnvironmentVariable(CompositionVariable);
        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);
        Environment.SetEnvironmentVariable(CompositionVariable, _authenticationComposition);
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environmentName);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinOpsDesk"] = _connectionString,
                [AuthenticationCompositions.ConfigurationKey] = _authenticationComposition,
                ["Authentication:JwtBearer:Authority"] = "",
                ["Authentication:JwtBearer:Issuer"] = TestAccessTokens.Issuer,
                ["Authentication:JwtBearer:Audience"] = TestAccessTokens.Audience,
                ["Authentication:JwtBearer:SigningKey"] = TestAccessTokens.SigningKey,
                ["Authentication:JwtBearer:RequireHttpsMetadata"] = "false",
                ["Authentication:JwtBearer:OrganizationClaimType"] = TestAccessTokens.OrganizationClaimType,
            });
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
    }
}