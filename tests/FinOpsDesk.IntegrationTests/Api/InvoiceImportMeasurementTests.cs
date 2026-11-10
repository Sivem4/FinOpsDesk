using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Observability;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceImportMeasurementTests : IDisposable
{
    private const string MeasureVariable = "FINOPSDESK_MEASURE_IMPORT";

    private const int DecisionRows = 2000;

    private const int DecisionSamples = 5;

    private const long DecisionThresholdMilliseconds = 5000;

    private const long CommitHintMilliseconds = 2000;

    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(15);

    private readonly SqlServerFixture _fixture;

    private readonly ITestOutputHelper _output;

    private FinOpsDeskApiFactory? _factory;

    public InvoiceImportMeasurementTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Measure_invoice_import_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(MeasureVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        string outputDirectory = Path.GetFullPath(@"D:\Projects\FinOpsDesk-review\phase-10b-measurements");
        Directory.CreateDirectory(outputDirectory);
        string resultPath = Path.Combine(outputDirectory, "invoice-import-samples.txt");
        ImportCommitTimingLog timing = new();
        RequestLogCollector collector = new();
        List<MeasuredImport> measured = [];
        string? failure = null;

        try
        {
            _factory = new FinOpsDeskApiFactory(
                _fixture.ConnectionString,
                services =>
                {
                    services.AddSingleton<ILoggerProvider>(collector);
                    services.AddSingleton(timing);
                    services.AddSingleton<ImportSqlTimingInterceptor>();
                    services.AddDbContext<ReconciliationDbContext>((provider, options) =>
                        options.AddInterceptors(provider.GetRequiredService<ImportSqlTimingInterceptor>()));
                    WrapImportStore(services, timing);
                });
            using HttpClient client = _factory.CreateApiClient();
            MeasuredImport warmup = await MeasureAsync(client, collector, timing, "warmup", DecisionRows);
            measured.Add(warmup);
            for (int index = 1; index <= DecisionSamples; index++)
            {
                measured.Add(await MeasureAsync(client, collector, timing, "sample-" + index, DecisionRows));
            }

            measured.Add(await MeasureAsync(client, collector, timing, "one-row", 1));
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name;
            _output.WriteLine(failure);
        }
        finally
        {
            File.WriteAllText(resultPath, FormatReport(measured, failure, _fixture.ConnectionString), Encoding.UTF8);
        }

        Assert.Null(IncompleteReason(measured, failure));
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }

    private async Task<MeasuredImport> MeasureAsync(
        HttpClient client,
        RequestLogCollector collector,
        ImportCommitTimingLog timing,
        string label,
        int rows)
    {
        Guid organizationId = Guid.NewGuid();
        byte[] csv = BuildCsv(label, organizationId, rows);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(csv);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "invoices.csv");
        content.Add(new StringContent("MEASURE"), "sourceSystem");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports/invoices")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestAccessTokens.Create(organizationId));

        long clientStarted = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        string body;
        try
        {
            response = await client.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception)
        {
            return MeasuredImport.Incomplete(
                label,
                rows,
                organizationId,
                Stopwatch.GetElapsedTime(clientStarted).TotalMilliseconds,
                "request failed: " + exception.GetType().Name);
        }

        double clientMilliseconds = Stopwatch.GetElapsedTime(clientStarted).TotalMilliseconds;
        if (!response.Headers.TryGetValues(RequestLog.HeaderName, out IEnumerable<string>? requestIds))
        {
            return MeasuredImport.Incomplete(label, rows, organizationId, clientMilliseconds, "X-Request-Id was absent");
        }

        string[] ids = requestIds.ToArray();
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0]))
        {
            return MeasuredImport.Incomplete(label, rows, organizationId, clientMilliseconds, "X-Request-Id was not a single value");
        }

        string requestId = ids[0];
        (RequestLogEntry? entry, string? eventProblem) = await WaitForEventAsync(collector, requestId);
        if (entry is null)
        {
            return MeasuredImport.Incomplete(
                label,
                rows,
                organizationId,
                clientMilliseconds,
                eventProblem ?? "9101 event was absent before the wait expired",
                requestId);
        }

        int statusCode = (int)response.StatusCode;
        bool replayed = false;
        int importedRows = -1;
        string? bodyProblem = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            replayed = root.GetProperty("replayed").GetBoolean();
            importedRows = root.GetProperty("importedRows").GetInt32();
        }
        catch (Exception exception)
        {
            bodyProblem = exception.GetType().Name;
        }

        int storedInvoices = -1;
        string? countProblem = null;
        try
        {
            await using ReconciliationDbContext context = _fixture.CreateContext();
            storedInvoices = await context.Invoices.CountAsync(invoice =>
                invoice.OrganizationId == new OrganizationId(organizationId));
        }
        catch (Exception exception)
        {
            countProblem = exception.GetType().Name;
        }

        timing.TryGet(requestId, out int commitAttempts, out double commitMilliseconds, out int sqlCommands, out double sqlMilliseconds);
        long? elapsed = ReadElapsed(entry);
        string outcome = entry.State.TryGetValue("outcome", out object? outcomeValue)
            ? outcomeValue as string ?? string.Empty
            : string.Empty;
        int loggedStatus = entry.State.TryGetValue("statusCode", out object? statusValue) && statusValue is int status
            ? status
            : -1;

        string? reason = null;
        if (bodyProblem is not null)
        {
            reason = "response body could not be read: " + bodyProblem;
        }
        else if (countProblem is not null)
        {
            reason = "invoice count failed: " + countProblem;
        }
        else if (statusCode != StatusCodes.Status201Created || loggedStatus != StatusCodes.Status201Created)
        {
            reason = "status was " + statusCode + " and 9101 status was " + loggedStatus;
        }
        else if (!string.Equals(outcome, RequestLog.OutcomeCompleted, StringComparison.Ordinal))
        {
            reason = "9101 outcome was " + outcome;
        }
        else if (replayed)
        {
            reason = "response was a replay";
        }
        else if (importedRows != rows || storedInvoices != rows)
        {
            reason = "imported " + importedRows + " and stored " + storedInvoices;
        }
        else if (elapsed is null)
        {
            reason = "9101 elapsedMilliseconds was absent";
        }
        else if (commitAttempts < 1)
        {
            reason = "CommitAsync was not observed";
        }

        return new MeasuredImport(
            label,
            rows,
            organizationId,
            requestId,
            reason is null,
            reason,
            elapsed,
            clientMilliseconds,
            commitAttempts,
            commitMilliseconds,
            sqlCommands,
            sqlMilliseconds,
            statusCode,
            importedRows,
            storedInvoices);
    }

    private static async Task<(RequestLogEntry? Entry, string? Problem)> WaitForEventAsync(
        RequestLogCollector collector,
        string requestId)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            RequestLogEntry[] matches = collector.Entries
                .Where(entry => string.Equals(ReadRequestId(entry), requestId, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1)
            {
                return (matches[0], null);
            }

            if (matches.Length > 1)
            {
                return (null, "more than one 9101 event matched X-Request-Id");
            }

            if (Stopwatch.GetElapsedTime(started) >= EventWait)
            {
                return (null, "9101 event was absent before the wait expired");
            }

            await Task.Delay(50);
        }
    }

    private static void WrapImportStore(IServiceCollection services, ImportCommitTimingLog timing)
    {
        ServiceDescriptor existing = services.Single(descriptor => descriptor.ServiceType == typeof(ICsvImportStore));
        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(ICsvImportStore),
            provider =>
            {
                ICsvImportStore inner = existing.ImplementationFactory is not null
                    ? (ICsvImportStore)existing.ImplementationFactory(provider)
                    : (ICsvImportStore)ActivatorUtilities.CreateInstance(provider, existing.ImplementationType!);
                return new MeasuringCsvImportStore(
                    inner,
                    provider.GetRequiredService<IHttpContextAccessor>(),
                    timing);
            },
            existing.Lifetime));
    }

    private static byte[] BuildCsv(string label, Guid organizationId, int rows)
    {
        var builder = new StringBuilder();
        builder.Append("ExternalId,InvoiceNumber,Amount,Currency\n");
        string prefix = organizationId.ToString("N");
        for (int row = 1; row <= rows; row++)
        {
            builder.Append(prefix);
            builder.Append('-');
            builder.Append(label);
            builder.Append('-');
            builder.Append(row.ToString("D4", CultureInfo.InvariantCulture));
            builder.Append(",INV-");
            builder.Append(row.ToString("D4", CultureInfo.InvariantCulture));
            builder.Append(",10.00,PLN\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string? ReadRequestId(RequestLogEntry entry)
    {
        return entry.State.TryGetValue("requestId", out object? value) ? value as string : null;
    }

    private static long? ReadElapsed(RequestLogEntry entry)
    {
        if (!entry.State.TryGetValue("elapsedMilliseconds", out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long elapsed => elapsed,
            int elapsed => elapsed,
            _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : null,
        };
    }

    internal static string FormatReport(
        IReadOnlyList<MeasuredImport> measured,
        string? failure,
        string connectionString)
    {
        var builder = new StringBuilder();
        SqlConnectionStringBuilder database = new(connectionString);
        builder.Append("catalog=").Append(database.InitialCatalog).Append('\n');
        builder.Append("dataSource=").Append(database.DataSource).Append('\n');
        builder.Append("transport=WebApplicationFactory in-process https://localhost").Append('\n');
        builder.Append("failure=").Append(failure ?? "none").Append('\n');
        foreach (MeasuredImport sample in measured)
        {
            builder.Append(sample.Label)
                .Append(" complete=").Append(sample.Complete)
                .Append(" reason=").Append(sample.Reason ?? "none")
                .Append(" requestId=").Append(sample.RequestId ?? "none")
                .Append(" status=").Append(sample.StatusCode)
                .Append(" imported=").Append(sample.ImportedRows)
                .Append(" stored=").Append(sample.StoredInvoices)
                .Append(" apiMs=").Append(sample.ApiMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "none")
                .Append(" clientMs=").Append(sample.ClientMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" commitAttempts=").Append(sample.CommitAttempts)
                .Append(" commitMs=").Append(sample.CommitMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" sqlCommands=").Append(sample.SqlCommands)
                .Append(" sqlMs=").Append(sample.SqlMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        string? incomplete = IncompleteReason(measured, failure);
        if (incomplete is not null)
        {
            builder.Append("decision=INCOMPLETE reason=").Append(incomplete).Append('\n');
        }
        else
        {
            MeasuredImport[] decision = measured
                .Where(sample => sample.Label.StartsWith("sample-", StringComparison.Ordinal))
                .ToArray();
            long[] values = decision.Select(sample => sample.ApiMilliseconds!.Value).OrderBy(value => value).ToArray();
            long median = values[DecisionSamples / 2];
            builder.Append("raw9101=");
            builder.Append(string.Join(
                ',',
                values.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            builder.Append(" median=").Append(median.ToString(CultureInfo.InvariantCulture));
            builder.Append(" min=").Append(values[0].ToString(CultureInfo.InvariantCulture));
            builder.Append(" max=").Append(values[^1].ToString(CultureInfo.InvariantCulture));
            builder.Append(" decision=").Append(median <= DecisionThresholdMilliseconds ? "NO-GO" : "ABOVE-THRESHOLD");
            builder.Append('\n');
        }

        builder.Append("commitHintMs=").Append(CommitHintMilliseconds).Append('\n');
        return builder.ToString();
    }

    internal static string? IncompleteReason(IReadOnlyList<MeasuredImport> measured, string? failure)
    {
        if (failure is not null)
        {
            return "run failure: " + failure;
        }

        string? warmup = RequiredSampleReason(measured, "warmup");
        if (warmup is not null)
        {
            return warmup;
        }

        MeasuredImport[] decision = measured
            .Where(sample => sample.Label.StartsWith("sample-", StringComparison.Ordinal))
            .ToArray();
        if (decision.Length != DecisionSamples)
        {
            return "decision samples were " + decision.Length.ToString(CultureInfo.InvariantCulture);
        }

        foreach (MeasuredImport sample in decision)
        {
            string? reason = SampleReason(sample);
            if (reason is not null)
            {
                return sample.Label + ": " + reason;
            }
        }

        string? oneRow = RequiredSampleReason(measured, "one-row");
        if (oneRow is not null)
        {
            return oneRow;
        }

        if (measured.Count != DecisionSamples + 2)
        {
            return "samples were " + measured.Count.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    internal static MeasuredImport CompletedSample(string label, long apiMilliseconds)
    {
        int rows = string.Equals(label, "one-row", StringComparison.Ordinal) ? 1 : DecisionRows;
        return new MeasuredImport(
            label,
            rows,
            Guid.Empty,
            "request",
            true,
            null,
            apiMilliseconds,
            apiMilliseconds,
            1,
            1,
            1,
            1,
            StatusCodes.Status201Created,
            rows,
            rows);
    }

    internal static MeasuredImport FailedSample(string label, string reason)
    {
        return MeasuredImport.Incomplete(label, DecisionRows, Guid.Empty, 1, reason, "request");
    }

    private static string? RequiredSampleReason(IReadOnlyList<MeasuredImport> measured, string label)
    {
        MeasuredImport[] matches = measured
            .Where(sample => string.Equals(sample.Label, label, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return label + " count was " + matches.Length.ToString(CultureInfo.InvariantCulture);
        }

        string? reason = SampleReason(matches[0]);
        return reason is null ? null : label + ": " + reason;
    }

    private static string? SampleReason(MeasuredImport sample)
    {
        if (!sample.Complete)
        {
            return sample.Reason ?? "incomplete";
        }

        if (sample.ApiMilliseconds is null)
        {
            return "9101 elapsedMilliseconds was absent";
        }

        return null;
    }

    internal sealed record MeasuredImport(
        string Label,
        int Rows,
        Guid OrganizationId,
        string? RequestId,
        bool Complete,
        string? Reason,
        long? ApiMilliseconds,
        double ClientMilliseconds,
        int CommitAttempts,
        double CommitMilliseconds,
        int SqlCommands,
        double SqlMilliseconds,
        int StatusCode,
        int ImportedRows,
        int StoredInvoices)
    {
        public static MeasuredImport Incomplete(
            string label,
            int rows,
            Guid organizationId,
            double clientMilliseconds,
            string reason,
            string? requestId = null)
        {
            return new MeasuredImport(
                label,
                rows,
                organizationId,
                requestId,
                false,
                reason,
                null,
                clientMilliseconds,
                0,
                0,
                0,
                0,
                0,
                -1,
                -1);
        }
    }
}

public sealed class InvoiceImportMeasurementDecisionTests
{
    private const string ConnectionString =
        "Server=localhost;Database=preserved;Trusted_Connection=True";

    [Fact]
    public void Incomplete_warmup_with_five_valid_decision_samples_is_incomplete()
    {
        string report = Report(
            InvoiceImportMeasurementTests.FailedSample("warmup", "request failed: TimeoutException"),
            InvoiceImportMeasurementTests.CompletedSample("one-row", 52),
            null);

        AssertIncomplete(report, "warmup: request failed: TimeoutException");
    }

    [Fact]
    public void Incomplete_one_row_with_five_valid_decision_samples_is_incomplete()
    {
        string report = Report(
            InvoiceImportMeasurementTests.CompletedSample("warmup", 1822),
            InvoiceImportMeasurementTests.FailedSample("one-row", "status was 500 and 9101 status was 500"),
            null);

        AssertIncomplete(report, "one-row: status was 500 and 9101 status was 500");
    }

    [Fact]
    public void Run_failure_with_every_sample_complete_is_incomplete()
    {
        string report = Report(
            InvoiceImportMeasurementTests.CompletedSample("warmup", 1822),
            InvoiceImportMeasurementTests.CompletedSample("one-row", 52),
            "TimeoutException");

        AssertIncomplete(report, "run failure: TimeoutException");
    }

    [Fact]
    public void Complete_saved_run_remains_no_go()
    {
        string report = Report(
            InvoiceImportMeasurementTests.CompletedSample("warmup", 1822),
            InvoiceImportMeasurementTests.CompletedSample("one-row", 52),
            null,
            536,
            512,
            504,
            607,
            375);

        Assert.Contains(
            "raw9101=375,504,512,536,607 median=512 min=375 max=607 decision=NO-GO",
            report,
            StringComparison.Ordinal);
        Assert.DoesNotContain("decision=INCOMPLETE", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_run_above_five_seconds_is_above_threshold()
    {
        string report = Report(
            InvoiceImportMeasurementTests.CompletedSample("warmup", 1822),
            InvoiceImportMeasurementTests.CompletedSample("one-row", 52),
            null,
            5001,
            5002,
            5003,
            5004,
            5005);

        Assert.Contains("median=5003", report, StringComparison.Ordinal);
        Assert.Contains("decision=ABOVE-THRESHOLD", report, StringComparison.Ordinal);
        Assert.DoesNotContain("decision=INCOMPLETE", report, StringComparison.Ordinal);
        Assert.DoesNotContain("decision=NO-GO", report, StringComparison.Ordinal);
    }

    private static string Report(
        InvoiceImportMeasurementTests.MeasuredImport warmup,
        InvoiceImportMeasurementTests.MeasuredImport oneRow,
        string? failure,
        params long[] decisionMilliseconds)
    {
        long[] values = decisionMilliseconds.Length == 0
            ? [536, 512, 504, 607, 375]
            : decisionMilliseconds;
        List<InvoiceImportMeasurementTests.MeasuredImport> measured = [warmup];
        for (int index = 0; index < values.Length; index++)
        {
            measured.Add(InvoiceImportMeasurementTests.CompletedSample("sample-" + (index + 1), values[index]));
        }

        measured.Add(oneRow);
        return InvoiceImportMeasurementTests.FormatReport(measured, failure, ConnectionString);
    }

    private static void AssertIncomplete(string report, string reason)
    {
        Assert.Contains("decision=INCOMPLETE reason=" + reason, report, StringComparison.Ordinal);
        Assert.DoesNotContain("decision=NO-GO", report, StringComparison.Ordinal);
        Assert.DoesNotContain("decision=ABOVE-THRESHOLD", report, StringComparison.Ordinal);
        Assert.DoesNotContain("raw9101=", report, StringComparison.Ordinal);
    }
}

internal sealed class ImportCommitTimingLog
{
    private readonly object _gate = new();

    private readonly Dictionary<string, CommitObservation> _observations = new(StringComparer.Ordinal);

    public void AddCommit(string traceIdentifier, TimeSpan elapsed)
    {
        lock (_gate)
        {
            CommitObservation observation = GetOrAdd(traceIdentifier);
            observation.CommitAttempts++;
            observation.CommitMilliseconds += elapsed.TotalMilliseconds;
        }
    }

    public void AddSql(string traceIdentifier, TimeSpan elapsed)
    {
        lock (_gate)
        {
            CommitObservation observation = GetOrAdd(traceIdentifier);
            observation.SqlCommands++;
            observation.SqlMilliseconds += elapsed.TotalMilliseconds;
        }
    }

    public bool TryGet(
        string traceIdentifier,
        out int commitAttempts,
        out double commitMilliseconds,
        out int sqlCommands,
        out double sqlMilliseconds)
    {
        lock (_gate)
        {
            if (!_observations.TryGetValue(traceIdentifier, out CommitObservation? observation))
            {
                commitAttempts = 0;
                commitMilliseconds = 0;
                sqlCommands = 0;
                sqlMilliseconds = 0;
                return false;
            }

            commitAttempts = observation.CommitAttempts;
            commitMilliseconds = observation.CommitMilliseconds;
            sqlCommands = observation.SqlCommands;
            sqlMilliseconds = observation.SqlMilliseconds;
            return true;
        }
    }

    private CommitObservation GetOrAdd(string traceIdentifier)
    {
        if (!_observations.TryGetValue(traceIdentifier, out CommitObservation? observation))
        {
            observation = new CommitObservation();
            _observations.Add(traceIdentifier, observation);
        }

        return observation;
    }

    private sealed class CommitObservation
    {
        public int CommitAttempts { get; set; }

        public double CommitMilliseconds { get; set; }

        public int SqlCommands { get; set; }

        public double SqlMilliseconds { get; set; }
    }
}

internal static class ImportCommitScope
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? TraceIdentifier => Current.Value;

    public static IDisposable Begin(string traceIdentifier)
    {
        string? previous = Current.Value;
        Current.Value = traceIdentifier;
        return new Pop(previous);
    }

    private sealed class Pop(string? previous) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
        }
    }
}

internal sealed class MeasuringCsvImportStore : ICsvImportStore
{
    private readonly ICsvImportStore _inner;

    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly ImportCommitTimingLog _timing;

    public MeasuringCsvImportStore(
        ICsvImportStore inner,
        IHttpContextAccessor httpContextAccessor,
        ImportCommitTimingLog timing)
    {
        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
        _timing = timing;
    }

    public Task<ImportBatch?> FindBatchByFileIdentityAsync(
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        CancellationToken cancellationToken)
    {
        return _inner.FindBatchByFileIdentityAsync(
            organizationId,
            importType,
            sourceSystem,
            fileHash,
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, InvoiceImportFacts>> FindInvoicesByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        return _inner.FindInvoicesByExternalIdsAsync(
            organizationId,
            sourceSystem,
            externalIds,
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, BankTransactionImportFacts>> FindBankTransactionsByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        return _inner.FindBankTransactionsByExternalIdsAsync(
            organizationId,
            sourceSystem,
            externalIds,
            cancellationToken);
    }

    public async Task<ImportCommitResult> CommitAsync(ImportCommitWork work, CancellationToken cancellationToken)
    {
        string? traceIdentifier = _httpContextAccessor.HttpContext?.TraceIdentifier;
        long started = Stopwatch.GetTimestamp();
        using IDisposable? scope = traceIdentifier is null ? null : ImportCommitScope.Begin(traceIdentifier);
        try
        {
            return await _inner.CommitAsync(work, cancellationToken);
        }
        finally
        {
            if (traceIdentifier is not null)
            {
                _timing.AddCommit(traceIdentifier, Stopwatch.GetElapsedTime(started));
            }
        }
    }
}

internal sealed class ImportSqlTimingInterceptor : DbCommandInterceptor
{
    private readonly ImportCommitTimingLog _timing;

    public ImportSqlTimingInterceptor(ImportCommitTimingLog timing)
    {
        _timing = timing;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        return result;
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        Record(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return new ValueTask<int>(result);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        Record(eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return new ValueTask<DbDataReader>(result);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        Record(eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return new ValueTask<object?>(result);
    }

    private void Record(CommandExecutedEventData eventData)
    {
        string? traceIdentifier = ImportCommitScope.TraceIdentifier;
        if (traceIdentifier is not null)
        {
            _timing.AddSql(traceIdentifier, eventData.Duration);
        }
    }
}