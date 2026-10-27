using System.Net.Http.Headers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

internal class FinOpsDeskApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = "ConnectionStrings__FinOpsDesk";

    private readonly string _connectionString;
    private readonly string? _previousConnectionString;
    private readonly Action<IServiceCollection>? _configureServices;

    public FinOpsDeskApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _configureServices = configureServices;

        // Program reads the connection string while the host is composed.
        // Environment variables are already loaded then and override appsettings.
        // ConfigureAppConfiguration runs later, in time for JWT options but not
        // for that earlier read. Dispose restores the value captured here so a
        // nested factory does not clear the string an outer factory still needs.
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);
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
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinOpsDesk"] = _connectionString,
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
    }
}