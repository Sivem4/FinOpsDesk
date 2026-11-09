using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeQueueMeasurementTests
{
    private const string MeasureVariable = "FINOPSDESK_MEASURE_DISPUTE_QUEUE";
    private const string NarrowIndex = "IX_AllocationDisputes_OrganizationId_RecordedAt_Id";
    private const int PageSize = 50;
    private const int Repeats = 5;

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public AllocationDisputeQueueMeasurementTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Measure_keyset_against_offset_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(MeasureVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        string outputDirectory = Path.GetFullPath(@"D:\Projects\FinOpsDesk-review\phase-9b-measurements");
        Directory.CreateDirectory(outputDirectory);

        List<PageMeasurement> pages = [];
        List<CountMeasurement> counts = [];
        string decision = "not-measured";
        try
        {
            await using ReconciliationDbContext context = _fixture.CreateContext();
            context.Database.SetCommandTimeout(180);
            OrganizationId decisionOrganization = await SeedAsync(context, "D", 20_000);
            OrganizationId correctionOrganization = await SeedAsync(context, "C", 8_000);
            AllocationDisputeQueries queries = new(context);
            await AssertDistributionAsync(queries, decisionOrganization, 19_600, 400);
            await AssertDistributionAsync(queries, correctionOrganization, 1_500, 6_000);

            List<PageMeasurement> journeys = [];
            await MeasureScenarioAsync(queries, pages, counts, "D", decisionOrganization, AllocationDisputeQueue.NeedsDecision, [1, 5001], outputDirectory, recordCount: true);
            await MeasureScenarioAsync(queries, pages, counts, "D", decisionOrganization, AllocationDisputeQueue.NeedsCorrection, [1, 301], outputDirectory, recordCount: true);
            await MeasureScenarioAsync(queries, pages, counts, "C", correctionOrganization, AllocationDisputeQueue.NeedsDecision, [1, 301], outputDirectory, recordCount: true);
            await MeasureScenarioAsync(queries, pages, counts, "C", correctionOrganization, AllocationDisputeQueue.NeedsCorrection, [1, 5001], outputDirectory, recordCount: true);
            await MeasureScenarioAsync(queries, journeys, counts, "D", decisionOrganization, AllocationDisputeQueue.NeedsDecision, [51], outputDirectory, recordCount: false);
            await MeasureScenarioAsync(queries, journeys, counts, "D", decisionOrganization, AllocationDisputeQueue.NeedsCorrection, [51], outputDirectory, recordCount: false);
            await MeasureScenarioAsync(queries, journeys, counts, "C", correctionOrganization, AllocationDisputeQueue.NeedsDecision, [51], outputDirectory, recordCount: false);
            await MeasureScenarioAsync(queries, journeys, counts, "C", correctionOrganization, AllocationDisputeQueue.NeedsCorrection, [51], outputDirectory, recordCount: false);

            await CreateIndexAsync(includeAllocationId: false);
            await MeasureShapeAsync(queries, pages, "narrow", outputDirectory);
            long narrowPages = await IndexPagesAsync();
            double narrowInsert = await MedianInsertAsync();
            bool lookupsGrow = LookupsGrow(pages);
            long includePages = 0;
            double includeInsert = 0;
            if (lookupsGrow)
            {
                await DropIndexAsync();
                await CreateIndexAsync(includeAllocationId: true);
                await MeasureShapeAsync(queries, pages, "include", outputDirectory);
                includePages = await IndexPagesAsync();
                includeInsert = await MedianInsertAsync();
            }

            await DropIndexAsync();
            double plainInsert = await MedianInsertAsync();
            decision = Decide(pages, lookupsGrow);
            await WriteReportAsync(
                outputDirectory,
                pages,
                counts,
                lookupsGrow,
                decision,
                narrowPages,
                narrowInsert,
                includePages,
                includeInsert,
                plainInsert,
                journeys);
        }
        finally
        {
            await DropIndexAsync();
        }

        Assert.False(string.IsNullOrWhiteSpace(decision));
    }

    private async Task MeasureScenarioAsync(
        AllocationDisputeQueries queries,
        List<PageMeasurement> pages,
        List<CountMeasurement> counts,
        string scenario,
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int[] positions,
        string outputDirectory,
        bool recordCount)
    {
        CapturedSql? countSql = null;
        foreach (int position in positions)
        {
            int page = ((position - 1) / PageSize) + 1;
            AllocationDisputeQueueCursor? cursor = position == 1
                ? null
                : await BoundaryAsync(queries, organizationId, queue, position - 1);
            CapturedBatch offset = await CaptureAsync(captured => captured.ListQueueAsync(organizationId, queue, page, PageSize, CancellationToken.None));
            CapturedBatch keyset = await CaptureAsync(captured => captured.ListQueueCursorAsync(organizationId, queue, PageSize, cursor, CancellationToken.None));
            CapturedSql offsetPage = SinglePage(offset, count: false);
            CapturedSql keysetPage = SinglePage(keyset, count: false);
            countSql ??= SinglePage(offset, count: true);
            Assert.Contains(offsetPage.ParameterTypes, type => type == SqlDbType.Int);
            if (cursor is not null)
            {
                Assert.Contains(keysetPage.ParameterTypes, type => type == SqlDbType.DateTimeOffset);
                Assert.Contains(keysetPage.ParameterTypes, type => type == SqlDbType.UniqueIdentifier);
                Assert.Contains("RecordedAt", keysetPage.Text, StringComparison.Ordinal);
                Assert.Contains("[Id]", keysetPage.Text, StringComparison.Ordinal);
            }

            Guid[] offsetIds = await ReadOffsetIdsAsync(queries, organizationId, queue, page);
            Guid[] keysetIds = await ReadCursorIdsAsync(queries, organizationId, queue, cursor);
            Assert.Equal(offsetIds, keysetIds);
            Assert.Equal(PageSize, offsetIds.Length);

            PageMeasurement measurement = new(scenario, organizationId, queue, position, page, offsetIds);
            measurement.Shapes["offset"] = await TimeAsync(offsetPage);
            measurement.Shapes["keyset"] = await TimeAsync(keysetPage);
            measurement.Commands["offset"] = offsetPage.Text;
            measurement.Commands["keyset"] = keysetPage.Text;
            await RecordPlanAsync(measurement, "offset", offsetPage, outputDirectory);
            await RecordPlanAsync(measurement, "keyset", keysetPage, outputDirectory);
            pages.Add(measurement);
        }

        if (recordCount)
        {
            counts.Add(new CountMeasurement(scenario, queue, await TimeAsync(countSql!)));
        }
    }

    private async Task MeasureShapeAsync(
        AllocationDisputeQueries queries,
        List<PageMeasurement> pages,
        string shape,
        string outputDirectory)
    {
        foreach (PageMeasurement page in pages)
        {
            AllocationDisputeQueueCursor? cursor = page.Position == 1
                ? null
                : await BoundaryAsync(queries, page.OrganizationId, page.Queue, page.Position - 1);
            CapturedBatch batch = await CaptureAsync(captured => captured.ListQueueCursorAsync(
                page.OrganizationId,
                page.Queue,
                PageSize,
                cursor,
                CancellationToken.None));
            CapturedSql command = SinglePage(batch, count: false);
            Assert.Equal(page.Commands["keyset"], command.Text, StringComparer.Ordinal);
            page.Shapes[shape] = await TimeAsync(command);
            await RecordPlanAsync(page, shape, command, outputDirectory);
        }
    }

    private async Task RecordPlanAsync(
        PageMeasurement page,
        string shape,
        CapturedSql command,
        string outputDirectory)
    {
        string plan = await ReadPlanAsync(command);
        PlanFacts facts = DescribePlan(plan);
        page.Facts[shape] = facts;
        page.Plans[shape] = facts.Label;
        if (facts.Lookups is int lookups)
        {
            page.Lookups[shape] = lookups;
        }

        if (facts.RowsRead is long rowsRead)
        {
            page.RowsRead[shape] = rowsRead;
        }

        page.UsesCandidate[shape] = facts.CandidateOrderedRange;
        string fileName = $"{page.Scenario}-{page.Queue}-{page.Position}-{shape}.xml";
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, fileName), plan);
    }

    private static bool LookupsGrow(IReadOnlyList<PageMeasurement> pages)
    {
        return pages.Any(page =>
            page.Facts.TryGetValue("narrow", out PlanFacts? facts)
            && facts.Lookups is int lookups
            && lookups > PageSize + 1);
    }

    private static string Decide(IReadOnlyList<PageMeasurement> pages, bool lookupsGrow)
    {
        string? selected = null;
        foreach (string shape in lookupsGrow ? new[] { "narrow", "include" } : new[] { "narrow" })
        {
            if (pages.Where(page => page.Position > 1).All(page => page.Facts[shape].CandidateOrderedRange)
                && pages.Where(page => page.Position > 1).All(page => PassesDeep(page, shape))
                && pages.Where(page => page.Position == 1).All(page => PassesFirst(page, shape)))
            {
                selected = shape;
                break;
            }
        }

        return selected ?? "rejected";
    }

    private static bool PassesDeep(PageMeasurement page, string shape)
    {
        TimedRun candidate = page.Shapes[shape];
        TimedRun baseline = BetterBaseline(page);
        return candidate.LogicalReadsMedian * 100 <= baseline.LogicalReadsMedian * 50
            && candidate.ElapsedMedian * 100 <= baseline.ElapsedMedian * 80;
    }

    private static bool PassesFirst(PageMeasurement page, string shape)
    {
        TimedRun candidate = page.Shapes[shape];
        TimedRun keyset = page.Shapes["keyset"];
        return candidate.LogicalReadsMedian * 100 <= keyset.LogicalReadsMedian * 110
            && candidate.ElapsedMedian * 100 <= keyset.ElapsedMedian * 110;
    }

    private static TimedRun BetterBaseline(PageMeasurement page)
    {
        TimedRun offset = page.Shapes["offset"];
        TimedRun keyset = page.Shapes["keyset"];
        if (offset.LogicalReadsMedian < keyset.LogicalReadsMedian)
        {
            return offset;
        }

        if (keyset.LogicalReadsMedian < offset.LogicalReadsMedian)
        {
            return keyset;
        }

        return offset.ElapsedMedian < keyset.ElapsedMedian ? offset : keyset;
    }

    private async Task<OrganizationId> SeedAsync(ReconciliationDbContext context, string scenario, int count)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m, "INV-" + scenario);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m, reference: scenario);
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Assert.True(invoice.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(10m, PersistenceTestData.Pln),
            BaseTime,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Add(allocation);
        await context.SaveChangesAsync();

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = """
            WITH nums AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects AS a
                CROSS JOIN sys.all_objects AS b
            )
            INSERT INTO [reconciliation].[AllocationDisputes]
                ([Id], [OrganizationId], [AllocationId], [Statement], [ActorSource], [ActorId], [RecordedAt], [IdempotencyKey])
            SELECT NEWID(), @organizationId, @allocationId, @scenario, 1, N'operator-a',
                DATEADD(millisecond, CAST(nums.n AS int), @base), NEWID()
            FROM nums;

            INSERT INTO [reconciliation].[AllocationDisputeResolutions]
                ([OrganizationId], [AllocationDisputeId], [Outcome], [Reason], [ActorSource], [ActorId], [DecidedAt], [IdempotencyKey])
            SELECT dispute.[OrganizationId], dispute.[Id],
                CASE WHEN @scenario = N'C' AND DATEDIFF(millisecond, @base, dispute.[RecordedAt]) > 7500 THEN 2 ELSE 1 END,
                N'upheld', 1, N'operator-a', dispute.[RecordedAt], NEWID()
            FROM [reconciliation].[AllocationDisputes] AS dispute
            WHERE dispute.[OrganizationId] = @organizationId
                AND (
                    (@scenario = N'D' AND DATEDIFF(millisecond, @base, dispute.[RecordedAt]) % 50 = 0)
                    OR (@scenario = N'C' AND (
                        DATEDIFF(millisecond, @base, dispute.[RecordedAt]) <= 6000
                        OR DATEDIFF(millisecond, @base, dispute.[RecordedAt]) > 7500)));
            """;
        command.Parameters.Add(new SqlParameter("@count", count));
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId.Value));
        command.Parameters.Add(new SqlParameter("@allocationId", allocation.Id.Value));
        command.Parameters.Add(new SqlParameter("@scenario", scenario));
        command.Parameters.Add(new SqlParameter("@base", BaseTime));
        await command.ExecuteNonQueryAsync();
        return organizationId;
    }

    private static async Task AssertDistributionAsync(
        AllocationDisputeQueries queries,
        OrganizationId organizationId,
        long needsDecision,
        long needsCorrection)
    {
        AllocationDisputeWorkSummaryResponse summary = (await queries.SummarizeWorkAsync(organizationId, CancellationToken.None)).Value!;
        Assert.Equal(needsDecision, summary.NeedsDecision);
        Assert.Equal(needsCorrection, summary.NeedsCorrection);
    }

    private async Task<AllocationDisputeQueueCursor> BoundaryAsync(
        AllocationDisputeQueries queries,
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int qualifyingPosition)
    {
        int page = ((qualifyingPosition - 1) / PageSize) + 1;
        int index = (qualifyingPosition - 1) % PageSize;
        AllocationDisputeListResponse response = (await queries.ListQueueAsync(
            organizationId,
            queue,
            page,
            PageSize,
            CancellationToken.None)).Value!;
        AllocationDisputeResponse row = response.Items[index];
        return new AllocationDisputeQueueCursor(
            AllocationDisputeQueueCursorDirection.Forward,
            queue,
            row.RecordedAt,
            row.DisputeId);
    }

    private static async Task<Guid[]> ReadOffsetIdsAsync(
        AllocationDisputeQueries queries,
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int page)
    {
        AllocationDisputeListResponse response = (await queries.ListQueueAsync(
            organizationId,
            queue,
            page,
            PageSize,
            CancellationToken.None)).Value!;
        return response.Items.Select(item => item.DisputeId).ToArray();
    }

    private static async Task<Guid[]> ReadCursorIdsAsync(
        AllocationDisputeQueries queries,
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        AllocationDisputeQueueCursor? cursor)
    {
        AllocationDisputeQueueCursorResponse response = (await queries.ListQueueCursorAsync(
            organizationId,
            queue,
            PageSize,
            cursor,
            CancellationToken.None)).Value!;
        return response.Items.Select(item => item.DisputeId).ToArray();
    }

    private async Task<CapturedBatch> CaptureAsync(Func<AllocationDisputeQueries, Task> execute)
    {
        CommandCapture capture = new();
        DbContextOptions<ReconciliationDbContext> options = new DbContextOptionsBuilder<ReconciliationDbContext>()
            .UseSqlServer(
                _fixture.ConnectionString,
                sqlServer => sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", "reconciliation"))
            .AddInterceptors(capture)
            .Options;
        await using ReconciliationDbContext context = new(options);
        context.Database.SetCommandTimeout(180);
        await execute(new AllocationDisputeQueries(context));
        return new CapturedBatch(capture.Commands);
    }

    private static CapturedSql SinglePage(CapturedBatch batch, bool count)
    {
        CapturedSql[] matches = batch.Commands
            .Where(command => command.Text.Contains("COUNT", StringComparison.OrdinalIgnoreCase) == count)
            .ToArray();
        return Assert.Single(matches);
    }

    private async Task<TimedRun> TimeAsync(CapturedSql command)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        List<string> messages = [];
        connection.InfoMessage += (_, args) => messages.Add(args.Message);
        await using (SqlCommand settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS IO ON; SET STATISTICS TIME ON;";
            await settings.ExecuteNonQueryAsync();
        }

        List<long> reads = [];
        List<long> elapsed = [];
        for (int repeat = 0; repeat < Repeats + 1; repeat++)
        {
            messages.Clear();
            await ExecuteAsync(connection, command);
            if (repeat == 0)
            {
                continue;
            }

            reads.Add(Sum(messages, LogicalReadsPattern));
            elapsed.Add(Sum(messages, ElapsedPattern));
        }

        await using (SqlCommand settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;";
            await settings.ExecuteNonQueryAsync();
        }

        return new TimedRun(Median(reads), Median(elapsed), reads, elapsed);
    }

    private async Task<string> ReadPlanAsync(CapturedSql command)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (SqlCommand settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS XML ON;";
            await settings.ExecuteNonQueryAsync();
        }

        string plan = string.Empty;
        await using (SqlCommand sql = command.ToCommand(connection))
        {
            await using SqlDataReader reader = await sql.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    string text = reader.GetValue(0)?.ToString() ?? string.Empty;
                    if (text.Contains("ShowPlanXML", StringComparison.Ordinal))
                    {
                        plan = text;
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await using (SqlCommand settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS XML OFF;";
            await settings.ExecuteNonQueryAsync();
        }

        Assert.Contains("ShowPlanXML", plan, StringComparison.Ordinal);
        return plan;
    }

    private static async Task ExecuteAsync(SqlConnection connection, CapturedSql command)
    {
        await using SqlCommand sql = command.ToCommand(connection);
        await using SqlDataReader reader = await sql.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
        }

        while (await reader.NextResultAsync())
        {
            while (await reader.ReadAsync())
            {
            }
        }
    }

    private async Task<long> IndexPagesAsync()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(partition_stats.used_page_count)
            FROM sys.dm_db_partition_stats AS partition_stats
            INNER JOIN sys.indexes AS indexes
                ON indexes.object_id = partition_stats.object_id
                AND indexes.index_id = partition_stats.index_id
            WHERE indexes.name = @name;
            """;
        command.Parameters.Add(new SqlParameter("@name", NarrowIndex));
        object? value = await command.ExecuteScalarAsync();
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<double> MedianInsertAsync()
    {
        List<double> elapsed = [];
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            long started = Stopwatch.GetTimestamp();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO [reconciliation].[AllocationDisputes]
                    ([Id], [OrganizationId], [AllocationId], [Statement], [ActorSource], [ActorId], [RecordedAt], [IdempotencyKey])
                SELECT TOP (1) NEWID(), allocation.[OrganizationId], allocation.[Id], N'measurement', 1, N'operator-a', @recordedAt, NEWID()
                FROM [reconciliation].[Allocations] AS allocation;
                """;
            command.Parameters.Add(new SqlParameter("@recordedAt", BaseTime.AddDays(repeat + 1)));
            await command.ExecuteNonQueryAsync();
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        double[] ordered = elapsed.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private async Task CreateIndexAsync(bool includeAllocationId)
    {
        string include = includeAllocationId ? " INCLUDE ([AllocationId])" : string.Empty;
        string sql = $"""
            CREATE UNIQUE NONCLUSTERED INDEX [{NarrowIndex}]
            ON [reconciliation].[AllocationDisputes] ([OrganizationId], [RecordedAt], [Id]){include};
            """;
        await ExecuteSqlAsync(sql);
    }

    private async Task DropIndexAsync()
    {
        await ExecuteSqlAsync($"""
            IF EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [name] = N'{NarrowIndex}'
                    AND [object_id] = OBJECT_ID(N'[reconciliation].[AllocationDisputes]'))
            DROP INDEX [{NarrowIndex}] ON [reconciliation].[AllocationDisputes];
            """);
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static PlanFacts DescribePlan(string plan)
    {
        XDocument document = XDocument.Parse(plan);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        List<OperatorFacts> disputeOperators = [];
        int lookupTotal = 0;
        bool sawLookup = false;
        bool lookupExecutionsAbsent = false;
        foreach (XElement relop in document.Descendants(ns + "RelOp"))
        {
            XElement? scan = relop.Element(ns + "IndexScan");
            XElement? target = scan?.Element(ns + "Object");
            string table = (string?)target?.Attribute("Table") ?? string.Empty;
            bool lookup = string.Equals((string?)scan?.Attribute("Lookup"), "1", StringComparison.Ordinal);
            XElement? runtime = relop.Element(ns + "RunTimeInformation")?.Element(ns + "RunTimeCountersPerThread");
            if (lookup)
            {
                sawLookup = true;
                if (runtime?.Attribute("ActualExecutions") is null)
                {
                    lookupExecutionsAbsent = true;
                }
                else
                {
                    lookupTotal += (int?)runtime.Attribute("ActualExecutions") ?? 0;
                }
            }

            if (!lookup && table.Contains("AllocationDisputes", StringComparison.Ordinal))
            {
                disputeOperators.Add(ReadOperator(relop, scan, target, runtime, ns));
            }
        }

        OperatorFacts? chosen = disputeOperators
            .OrderByDescending(item => item.CandidateIndex)
            .ThenByDescending(item => item.Ordered)
            .ThenByDescending(item => item.RowsRead ?? -1)
            .FirstOrDefault();
        if (chosen is null)
        {
            return new PlanFacts("absent", false, false, false, false, lookupExecutionsAbsent ? null : lookupTotal, null, false);
        }

        bool boundaryRange = chosen.BoundaryRange;
        return new PlanFacts(
            chosen.Label,
            chosen.OrganizationEqualitySeek,
            boundaryRange,
            chosen.ResidualBoundary,
            chosen.Ordered,
            sawLookup && lookupExecutionsAbsent ? null : lookupTotal,
            chosen.RowsRead,
            chosen.CandidateIndex && chosen.Ordered && boundaryRange);
    }

    private static OperatorFacts ReadOperator(
        XElement relop,
        XElement? scan,
        XElement? target,
        XElement? runtime,
        XNamespace ns)
    {
        string physical = (string?)relop.Attribute("PhysicalOp") ?? "absent";
        string index = (string?)target?.Attribute("Index") ?? string.Empty;
        bool ordered = string.Equals((string?)scan?.Attribute("Ordered"), "1", StringComparison.Ordinal);
        bool candidate = index.Contains(NarrowIndex, StringComparison.Ordinal);
        bool organizationSeek = false;
        bool boundaryRange = false;
        foreach (XElement keys in scan?.Descendants(ns + "SeekKeys") ?? [])
        {
            foreach (XElement prefix in keys.Elements(ns + "Prefix"))
            {
                if (string.Equals((string?)prefix.Attribute("ScanType"), "EQ", StringComparison.Ordinal)
                    && HasColumn(prefix, "OrganizationId"))
                {
                    organizationSeek = physical.Contains("Seek", StringComparison.Ordinal);
                }
            }

            foreach (string rangeName in new[] { "StartRange", "EndRange" })
            {
                foreach (XElement range in keys.Elements(ns + rangeName))
                {
                    if (HasColumn(range, "RecordedAt") || HasColumn(range, "Id"))
                    {
                        boundaryRange = true;
                    }
                }
            }
        }

        bool residual = scan?.Element(ns + "Predicate") is XElement predicate
            && (HasColumn(predicate, "RecordedAt") || HasColumn(predicate, "Id"));
        long? rowsRead = runtime?.Attribute("ActualRowsRead") is null
            ? null
            : (long?)runtime.Attribute("ActualRowsRead");
        string label = candidate && boundaryRange
            ? "boundary-range"
            : organizationSeek
                ? "organization-seek"
                : residual
                    ? "residual"
                    : physical;
        return new OperatorFacts(label, organizationSeek, boundaryRange, residual, ordered, candidate, rowsRead);
    }

    private static bool HasColumn(XElement element, string column)
    {
        return element.Descendants().Any(item =>
            string.Equals(item.Name.LocalName, "ColumnReference", StringComparison.Ordinal)
            && string.Equals((string?)item.Attribute("Column"), column, StringComparison.Ordinal));
    }

    private static async Task WriteReportAsync(
        string outputDirectory,
        IReadOnlyList<PageMeasurement> pages,
        IReadOnlyList<CountMeasurement> counts,
        bool lookupsGrow,
        string decision,
        long narrowPages,
        double narrowInsert,
        long includePages,
        double includeInsert,
        double plainInsert,
        IReadOnlyList<PageMeasurement> journeys)
    {
        List<string> lines =
        [
            "# Allocation dispute queue measurements",
            string.Empty,
            "Database pattern: FinOpsDesk_IT_<guid>. Buffers were left in place.",
            "One warmup and five timed executions. Median is the middle value.",
            "Baseline rule: lower median logical reads; if tied, lower median elapsed; if still tied, keyset without the candidate.",
            "An ordered range requires seek keys on the cursor boundary. An Index Seek name or Ordered=\"1\" alone does not pass.",
            "Organization equality, a residual predicate, and a missing metric are reported separately. absent means the plan did not contain that metric.",
            $"Lookups grew with rejected rows: {lookupsGrow}",
            $"Index decision: {decision}",
            $"Narrow index pages: {narrowPages}. Median insert with narrow index: {narrowInsert.ToString("0.000", CultureInfo.InvariantCulture)} ms. Without index: {plainInsert.ToString("0.000", CultureInfo.InvariantCulture)} ms. Include pages: {includePages}. Include insert: {includeInsert.ToString("0.000", CultureInfo.InvariantCulture)} ms.",
            string.Empty,
            "| Scenario | Queue | Position | Shape | Logical reads | Elapsed ms | Lookups | Rows read | Access | Org seek | Boundary range | Residual | Ordered | Ordered range |",
            "|---|---|---:|---|---:|---:|---:|---:|---|---|---|---|---|---|",
        ];
        foreach (PageMeasurement page in pages.Concat(journeys))
        {
            foreach ((string shape, TimedRun timing) in page.Shapes)
            {
                PlanFacts facts = page.Facts[shape];
                lines.Add("| " + string.Join(
                    " | ",
                    [
                        page.Scenario,
                        page.Queue.ToString(),
                        page.Position.ToString(CultureInfo.InvariantCulture),
                        shape,
                        timing.LogicalReadsMedian.ToString(CultureInfo.InvariantCulture),
                        timing.ElapsedMedian.ToString(CultureInfo.InvariantCulture),
                        FormatMetric(facts.Lookups),
                        FormatMetric(facts.RowsRead),
                        facts.Label,
                        facts.OrganizationEqualitySeek ? "yes" : "no",
                        facts.BoundaryRangeSeek ? "yes" : "no",
                        facts.ResidualBoundary ? "yes" : "no",
                        facts.Ordered ? "yes" : "no",
                        facts.CandidateOrderedRange ? "yes" : "no",
                    ]));
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Page journeys");
        lines.Add("Position 51 is the second page of 50 rows. Offset and keyset returned the same ids.");
        lines.Add("Fresh keyset is page 1 plus page 2 and no summary. Summary keyset adds both scenario counts once. Offset adds that queue count on each page.");
        foreach (string scenario in new[] { "D", "C" })
        {
            long summaryReads = counts
                .Where(count => count.Scenario == scenario)
                .Sum(count => count.Timing.LogicalReadsMedian);
            foreach (AllocationDisputeQueue queue in new[] { AllocationDisputeQueue.NeedsDecision, AllocationDisputeQueue.NeedsCorrection })
            {
                PageMeasurement first = pages.Single(page => page.Scenario == scenario && page.Queue == queue && page.Position == 1);
                PageMeasurement second = journeys.Single(page => page.Scenario == scenario && page.Queue == queue && page.Position == 51);
                long queueCount = counts.Single(count => count.Scenario == scenario && count.Queue == queue).Timing.LogicalReadsMedian;
                long fresh = first.Shapes["keyset"].LogicalReadsMedian + second.Shapes["keyset"].LogicalReadsMedian;
                long withSummary = fresh + summaryReads;
                long offset = first.Shapes["offset"].LogicalReadsMedian + queueCount
                    + second.Shapes["offset"].LogicalReadsMedian + queueCount;
                lines.Add(
                    $"{scenario} {queue}: page1 keyset {first.Shapes["keyset"].LogicalReadsMedian}, page2 keyset {second.Shapes["keyset"].LogicalReadsMedian}, "
                    + $"page1 offset {first.Shapes["offset"].LogicalReadsMedian}, page2 offset {second.Shapes["offset"].LogicalReadsMedian}, "
                    + $"fresh keyset {fresh}, summary keyset {withSummary}, offset with counts {offset}.");
            }
        }

        lines.Add(string.Empty);
        lines.Add("## COUNT_BIG");
        foreach (CountMeasurement count in counts)
        {
            lines.Add($"{count.Scenario} {count.Queue}: logical reads {count.Timing.LogicalReadsMedian}, elapsed {count.Timing.ElapsedMedian} ms");
        }

        await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "results.md"), lines);
    }

    private static long Median(IReadOnlyList<long> values)
    {
        long[] ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private static long Sum(IEnumerable<string> messages, Regex pattern)
    {
        long total = 0;
        foreach (string message in messages)
        {
            foreach (Match match in pattern.Matches(message))
            {
                total += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return total;
    }

    private static readonly Regex LogicalReadsPattern = new("(?<!lob )logical reads (\\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex ElapsedPattern = new("elapsed time = (\\d+) ms", RegexOptions.CultureInvariant);

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<CapturedSql> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command is SqlCommand sqlCommand)
            {
                Commands.Add(CapturedSql.From(sqlCommand));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record CapturedBatch(IReadOnlyList<CapturedSql> Commands);

    private sealed record CapturedSql(string Text, IReadOnlyList<SqlParameter> Parameters, IReadOnlyList<SqlDbType> ParameterTypes)
    {
        public static CapturedSql From(SqlCommand command)
        {
            List<SqlParameter> parameters = [];
            List<SqlDbType> types = [];
            foreach (SqlParameter parameter in command.Parameters)
            {
                SqlParameter copy = new(parameter.ParameterName, parameter.SqlDbType)
                {
                    Value = parameter.Value ?? DBNull.Value,
                    Size = parameter.Size,
                    Precision = parameter.Precision,
                    Scale = parameter.Scale,
                    IsNullable = parameter.IsNullable,
                };
                parameters.Add(copy);
                types.Add(parameter.SqlDbType);
            }

            return new CapturedSql(command.CommandText, parameters, types);
        }

        public SqlCommand ToCommand(SqlConnection connection)
        {
            SqlCommand command = connection.CreateCommand();
            command.CommandText = Text;
            command.CommandTimeout = 180;
            foreach (SqlParameter parameter in Parameters)
            {
                command.Parameters.Add(new SqlParameter(parameter.ParameterName, parameter.SqlDbType)
                {
                    Value = parameter.Value ?? DBNull.Value,
                    Size = parameter.Size,
                    Precision = parameter.Precision,
                    Scale = parameter.Scale,
                    IsNullable = parameter.IsNullable,
                });
            }

            return command;
        }
    }

    private sealed record TimedRun(long LogicalReadsMedian, long ElapsedMedian, IReadOnlyList<long> Reads, IReadOnlyList<long> Elapsed);

    private sealed class PageMeasurement
    {
        public PageMeasurement(
            string scenario,
            OrganizationId organizationId,
            AllocationDisputeQueue queue,
            int position,
            int page,
            IReadOnlyList<Guid> ids)
        {
            Scenario = scenario;
            OrganizationId = organizationId;
            Queue = queue;
            Position = position;
            Page = page;
            Ids = ids;
        }

        public string Scenario { get; }

        public OrganizationId OrganizationId { get; }

        public AllocationDisputeQueue Queue { get; }

        public int Position { get; }

        public int Page { get; }

        public IReadOnlyList<Guid> Ids { get; }

        public Dictionary<string, TimedRun> Shapes { get; } = [];

        public Dictionary<string, PlanFacts> Facts { get; } = [];

        public Dictionary<string, string> Commands { get; } = [];

        public Dictionary<string, string> Plans { get; } = [];

        public Dictionary<string, int> Lookups { get; } = [];

        public Dictionary<string, long> RowsRead { get; } = [];

        public Dictionary<string, bool> UsesCandidate { get; } = [];
    }

    private sealed record CountMeasurement(string Scenario, AllocationDisputeQueue Queue, TimedRun Timing);

    private sealed record PlanFacts(
        string Label,
        bool OrganizationEqualitySeek,
        bool BoundaryRangeSeek,
        bool ResidualBoundary,
        bool Ordered,
        int? Lookups,
        long? RowsRead,
        bool CandidateOrderedRange);

    private sealed record OperatorFacts(
        string Label,
        bool OrganizationEqualitySeek,
        bool BoundaryRange,
        bool ResidualBoundary,
        bool Ordered,
        bool CandidateIndex,
        long? RowsRead);

    private static string FormatMetric(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "absent";
    }

    private static string FormatMetric(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "absent";
    }
}