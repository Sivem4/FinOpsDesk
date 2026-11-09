using System.Diagnostics;
using System.Text.Json;

using FinOpsDesk.Api.Observability;

using Microsoft.Data.SqlClient;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class RequestLogJsonFormatterTests
{
    private const string Marker = "finopsdesk-secret-marker";

    private static readonly Lock ConsoleGate = new();

    [Fact]
    public void Host_formatter_emits_the_seven_state_fields()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "host-appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "host-appsettings.Development.json"))
            .Build();
        AssertFormatter(configuration, "json");

        string line;
        lock (ConsoleGate)
        {
            TextWriter previous = Console.Out;
            using StringWriter writer = new();
            Console.SetOut(writer);
            try
            {
                using ServiceProvider provider = new ServiceCollection()
                    .AddLogging(logging =>
                    {
                        logging.AddConfiguration(configuration.GetSection("Logging"));
                        logging.AddJsonConsole(options => configuration
                            .GetSection("Logging:Console:FormatterOptions")
                            .Bind(options));
                    })
                    .BuildServiceProvider();
                ILogger logger = provider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(RequestLog.Category);
                using Activity activity = new("formatter-probe");
                activity.SetIdFormat(ActivityIdFormat.W3C);
                activity.AddTag("secret", Marker);
                activity.AddBaggage("secret", Marker);
                activity.TraceStateString = "secret=" + Marker;
                activity.Start();
                using (logger.BeginScope(new Dictionary<string, object> { ["secret"] = Marker }))
                {
                    RequestLog.Write(
                        logger,
                        new RequestCompletedLog(
                            "server-request",
                            null,
                            "GET",
                            "ready",
                            503,
                            4,
                            RequestLog.OutcomeCompleted));
                }

                activity.Stop();
            }
            finally
            {
                Console.SetOut(previous);
            }

            line = writer.ToString().Trim();
        }

        Assert.DoesNotContain(Marker, line, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal(9101, root.GetProperty("EventId").GetInt32());
        Assert.Equal("Information", root.GetProperty("LogLevel").GetString());
        Assert.Equal(RequestLog.Category, root.GetProperty("Category").GetString());
        Assert.Equal(RequestLog.Message, root.GetProperty("Message").GetString());
        Assert.False(root.TryGetProperty("Exception", out _));
        Assert.False(root.TryGetProperty("Scopes", out _));
        Assert.Equal(JsonValueKind.String, root.GetProperty("Timestamp").ValueKind);

        JsonElement state = root.GetProperty("State");
        Assert.Equal(7, state.EnumerateObject().Count());
        Assert.Equal("server-request", state.GetProperty("requestId").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("problemTraceId").ValueKind);
        Assert.Equal("GET", state.GetProperty("httpMethod").GetString());
        Assert.Equal("ready", state.GetProperty("route").GetString());
        Assert.Equal(503, state.GetProperty("statusCode").GetInt32());
        Assert.Equal(4, state.GetProperty("elapsedMilliseconds").GetInt64());
        Assert.Equal(RequestLog.OutcomeCompleted, state.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Probe_connection_string_uses_a_distinct_pool_key()
    {
        const string configured =
            "Server=127.0.0.1;Database=FinOpsDesk;Connect Timeout=2;Connect Retry Count=0;Application Name=FinOpsDesk.Readiness;Trusted_Connection=True";

        string probe = DatabaseReadinessProbe.CreateProbeConnectionString(configured);
        SqlConnectionStringBuilder configuredBuilder = new(configured);
        SqlConnectionStringBuilder probeBuilder = new(probe);

        Assert.NotEqual(configuredBuilder.ConnectionString, probeBuilder.ConnectionString);
        Assert.Equal(DatabaseReadinessProbe.AlternateApplicationName, probeBuilder.ApplicationName);
        Assert.Equal(2, probeBuilder.ConnectTimeout);
        Assert.Equal(0, probeBuilder.ConnectRetryCount);
        Assert.Equal(configuredBuilder.DataSource, probeBuilder.DataSource);
        Assert.Equal(configuredBuilder.InitialCatalog, probeBuilder.InitialCatalog);
    }

    private static void AssertFormatter(IConfiguration configuration, string formatterName)
    {
        Assert.Equal(formatterName, configuration["Logging:Console:FormatterName"]);
        Assert.False(configuration.GetValue<bool>("Logging:Console:FormatterOptions:IncludeScopes"));
        Assert.True(configuration.GetValue<bool>("Logging:Console:FormatterOptions:UseUtcTimestamp"));
        Assert.Equal(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            configuration["Logging:Console:FormatterOptions:TimestampFormat"]);
        Assert.False(configuration.GetValue<bool>("Logging:Console:FormatterOptions:JsonWriterOptions:Indented"));
        Assert.Equal("Information", configuration["Logging:LogLevel:FinOpsDesk.Api.Requests"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft.AspNetCore"]);
    }
}