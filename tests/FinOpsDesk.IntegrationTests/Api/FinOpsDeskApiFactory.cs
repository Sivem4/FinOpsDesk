using System.Net.Http.Headers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FinOpsDesk.IntegrationTests.Api;

internal sealed class FinOpsDeskApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = "ConnectionStrings__FinOpsDesk";

    private readonly string _connectionString;

    public FinOpsDeskApiFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;

        // Program reads the connection string while the host is composed.
        // Environment variables are already loaded then and override appsettings.
        // ConfigureAppConfiguration runs later, in time for JWT options but not
        // for that earlier read.
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
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable(ConnectionStringVariable, null);
        base.Dispose(disposing);
    }
}