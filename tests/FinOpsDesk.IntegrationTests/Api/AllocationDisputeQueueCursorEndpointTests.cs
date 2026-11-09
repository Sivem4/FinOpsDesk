using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
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

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeQueueCursorEndpointTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public AllocationDisputeQueueCursorEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Cursor_keeps_both_queue_predicates_and_stays_inside_the_caller_organization()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId foreignId = OrganizationId.New();
        Guid undecided = await SeedAsync(organizationId, "undecided", ConfirmedAt, decision: null, reversed: false);
        Guid reversedUndecided = await SeedAsync(organizationId, "reversed-undecided", ConfirmedAt.AddMinutes(1), decision: null, reversed: true);
        Guid upheld = await SeedAsync(organizationId, "upheld-open", ConfirmedAt.AddMinutes(2), AllocationDisputeResolutionOutcome.Upheld, reversed: false);
        await SeedAsync(organizationId, "upheld-reversed", ConfirmedAt.AddMinutes(3), AllocationDisputeResolutionOutcome.Upheld, reversed: true);
        await SeedAsync(organizationId, "not-upheld", ConfirmedAt.AddMinutes(4), AllocationDisputeResolutionOutcome.NotUpheld, reversed: false);
        Guid foreign = await SeedAsync(foreignId, "foreign-row", ConfirmedAt.AddMinutes(5), decision: null, reversed: false);

        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using HttpClient anonymous = factory.CreateApiClient();
        using HttpClient foreignClient = factory.CreateApiClient(TestAccessTokens.Create(foreignId.Value));

        HttpResponseMessage denied = await anonymous.GetAsync("/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using JsonDocument decision = await ReadAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=10");
        Guid[] decisionIds = ReadIds(decision);
        Assert.Equal([undecided, reversedUndecided], decisionIds);
        Assert.Equal(JsonValueKind.Null, decision.RootElement.GetProperty("previousCursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, decision.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.False(decision.RootElement.TryGetProperty("totalCount", out _));
        Assert.False(decision.RootElement.TryGetProperty("page", out _));

        using JsonDocument correction = await ReadAsync(client, "/api/allocation-disputes/queue?queue=needs-correction&pagination=cursor");
        Assert.Equal([upheld], ReadIds(correction));

        string foreignCursor = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            ConfirmedAt.AddMinutes(5),
            foreign);
        using JsonDocument positioned = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + foreignCursor);
        Assert.DoesNotContain(foreign, ReadIds(positioned));
        Assert.Empty(ReadIds(positioned));

        string beforeBoth = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            ConfirmedAt.AddMinutes(-1),
            Guid.Empty);
        using JsonDocument ownRows = await ReadAsync(
            foreignClient,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + beforeBoth);
        Assert.Equal([foreign], ReadIds(ownRows));
    }

    [Fact]
    public async Task Cursor_rejects_invalid_input_before_changing_the_offset_error_mapping()
    {
        OrganizationId organizationId = OrganizationId.New();
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string cursor = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            ConfirmedAt,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=nope", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&page=abc", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pageSize=abc", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&page=1&page=2", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pageSize=0", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pageSize=101", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&page=21474838&pageSize=100", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&page=1", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&cursor=" + cursor, "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=offset", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=abc", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=0", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=1&pageSize=2", "reconciliation.allocation_dispute.invalid_pagination");

        string mismatch = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            ConfirmedAt,
            Guid.Empty);
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-correction&pagination=cursor&cursor=" + mismatch,
            mismatch);
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + new string('A', 65),
            new string('A', 65));
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + EncodeRaw(2, 1, 1, ConfirmedAt, Guid.Empty),
            EncodeRaw(2, 1, 1, ConfirmedAt, Guid.Empty));
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + EncodeRaw(1, 9, 1, ConfirmedAt, Guid.Empty),
            EncodeRaw(1, 9, 1, ConfirmedAt, Guid.Empty));
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + EncodeRaw(1, 1, 1, ConfirmedAt.Ticks, 841, Guid.Empty),
            EncodeRaw(1, 1, 1, ConfirmedAt.Ticks, 841, Guid.Empty));
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + EncodeRaw(1, 1, 1, DateTime.MaxValue.Ticks + 1, 0, Guid.Empty),
            EncodeRaw(1, 1, 1, DateTime.MaxValue.Ticks + 1, 0, Guid.Empty));
        await AssertProblemWithoutCursorAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&cursor=" + EncodeRaw(1, 1, 1, DateTime.MaxValue.Ticks, -840, Guid.Empty),
            EncodeRaw(1, 1, 1, DateTime.MaxValue.Ticks, -840, Guid.Empty));
    }

    [Fact]
    public async Task Cursor_walks_forward_and_back_including_a_sql_tie_and_a_changed_page_size()
    {
        OrganizationId organizationId = OrganizationId.New();
        List<Guid> ids = [];
        for (int index = 0; index < 70; index++)
        {
            ids.Add(await SeedAsync(
                organizationId,
                "row-" + index.ToString(CultureInfo.InvariantCulture),
                ConfirmedAt.AddMinutes(index),
                decision: null,
                reversed: false));
        }

        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using JsonDocument first = await ReadAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=50");
        Assert.Equal(ids.Take(50).ToArray(), ReadIds(first));
        string? next = first.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(next);
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("previousCursor").ValueKind);

        using JsonDocument smaller = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=10&cursor=" + next);
        Assert.Equal(ids.Skip(50).Take(10).ToArray(), ReadIds(smaller));
        Assert.DoesNotContain(ids[49], ReadIds(smaller));
        string? afterSmaller = smaller.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(afterSmaller);
        using JsonDocument rest = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=10&cursor=" + afterSmaller);
        Assert.Equal(ids.Skip(60).ToArray(), ReadIds(rest));

        await WalkTieAsync(client);
        await WalkEqualInstantAsync(client);
    }

    [Fact]
    public async Task Cursor_reports_an_empty_forward_page_and_an_empty_backward_page_after_rows_leave()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid first = await SeedAsync(organizationId, "first", ConfirmedAt, decision: null, reversed: false);
        Guid second = await SeedAsync(organizationId, "second", ConfirmedAt.AddMinutes(1), decision: null, reversed: false);
        Guid third = await SeedAsync(organizationId, "third", ConfirmedAt.AddMinutes(2), decision: null, reversed: false);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        using JsonDocument page = await ReadAsync(client, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2");
        Assert.Equal([first, second], ReadIds(page));
        string? next = page.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(next);
        await ResolveAsync(third, AllocationDisputeResolutionOutcome.NotUpheld);
        using JsonDocument emptyForward = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2&cursor=" + next);
        Assert.Empty(ReadIds(emptyForward));
        Assert.Equal(JsonValueKind.Null, emptyForward.RootElement.GetProperty("nextCursor").ValueKind);
        string? back = emptyForward.RootElement.GetProperty("previousCursor").GetString();
        Assert.NotNull(back);

        await ResolveAsync(first, AllocationDisputeResolutionOutcome.NotUpheld);
        await ResolveAsync(second, AllocationDisputeResolutionOutcome.NotUpheld);
        using JsonDocument emptyBackward = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2&cursor=" + back);
        Assert.Empty(ReadIds(emptyBackward));
        Assert.Equal(JsonValueKind.Null, emptyBackward.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, emptyBackward.RootElement.GetProperty("previousCursor").ValueKind);
    }

    [Fact]
    public async Task Cursor_command_uses_sql_datetimeoffset_and_uniqueidentifier_parameters_without_a_count()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid id = await SeedAsync(organizationId, "sql-row", ConfirmedAt, decision: null, reversed: false);
        CommandCapture capture = new();
        await using ReconciliationDbContext context = CreateContext(capture);
        AllocationDisputeQueries queries = new(context);
        string cursor = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            ConfirmedAt.AddMinutes(-1),
            Guid.Empty);
        Assert.True(AllocationDisputeQueueCursor.TryDecode(
            cursor,
            AllocationDisputeQueue.NeedsDecision,
            out AllocationDisputeQueueCursor? decoded));
        FinOpsDesk.SharedKernel.Results.Result<AllocationDisputeQueueCursorResponse> result =
            await queries.ListQueueCursorAsync(organizationId, AllocationDisputeQueue.NeedsDecision, 10, decoded, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(id, Assert.Single(result.Value.Items).DisputeId);
        CapturedCommand command = Assert.Single(capture.Commands);
        Assert.DoesNotContain("COUNT", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RecordedAt", command.Text, StringComparison.Ordinal);
        Assert.Contains(SqlDbType.DateTimeOffset, command.ParameterTypes);
        Assert.Contains(SqlDbType.UniqueIdentifier, command.ParameterTypes);
    }

    private async Task WalkTieAsync(HttpClient client)
    {
        OrganizationId organizationId = OrganizationId.New();
        DateTimeOffset tied = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);
        Guid[] candidates =
        [
            Guid.Parse("11111111-9b9b-49b9-89b9-000000000000"),
            Guid.Parse("11111111-9b9b-49b9-89b9-000000000001"),
            Guid.Parse("00000000-9b9b-49b9-89b9-0000000000fe"),
            Guid.Parse("00000000-9b9b-49b9-89b9-0000000000ff"),
        ];
        foreach (Guid candidate in candidates)
        {
            await SeedAsync(organizationId, "tie-" + candidate.ToString("N"), tied, decision: null, reversed: false, disputeId: candidate);
        }

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid[] sqlOrder = await context.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId)
            .OrderBy(dispute => dispute.RecordedAt)
            .ThenBy(dispute => dispute.Id)
            .Select(dispute => dispute.Id.Value)
            .ToArrayAsync();
        Guid[] dotNetOrder = candidates.OrderBy(candidate => candidate).ToArray();
        Assert.NotEqual(dotNetOrder, sqlOrder);
        Assert.Equal(4, sqlOrder.Length);

        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient tiedClient = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using JsonDocument first = await ReadAsync(tiedClient, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2");
        Assert.Equal(sqlOrder.Take(2).ToArray(), ReadIds(first));
        Assert.Equal(tied, await RecordedAtAsync(sqlOrder[1]));
        Assert.Equal(tied, await RecordedAtAsync(sqlOrder[2]));
        string? next = first.RootElement.GetProperty("nextCursor").GetString();
        using JsonDocument second = await ReadAsync(
            tiedClient,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2&cursor=" + next);
        Assert.Equal(sqlOrder.Skip(2).ToArray(), ReadIds(second));
        string? previous = second.RootElement.GetProperty("previousCursor").GetString();
        using JsonDocument back = await ReadAsync(
            tiedClient,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2&cursor=" + previous);
        Assert.Equal(sqlOrder.Take(2).ToArray(), ReadIds(back));
        string? forwardAgain = back.RootElement.GetProperty("nextCursor").GetString();
        using JsonDocument secondAgain = await ReadAsync(
            tiedClient,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=2&cursor=" + forwardAgain);
        Assert.Equal(sqlOrder.Skip(2).ToArray(), ReadIds(secondAgain));
        _ = client;
    }

    private async Task WalkEqualInstantAsync(HttpClient client)
    {
        OrganizationId organizationId = OrganizationId.New();
        DateTimeOffset utc = DateTimeOffset.Parse("2026-11-09T10:00:00.1234567+00:00", CultureInfo.InvariantCulture);
        DateTimeOffset plusTwo = DateTimeOffset.Parse("2026-11-09T12:00:00.1234567+02:00", CultureInfo.InvariantCulture);
        Assert.Equal(utc.UtcDateTime, plusTwo.UtcDateTime);
        Assert.NotEqual(utc.Offset, plusTwo.Offset);
        Guid left = Guid.Parse("22222222-9b9b-49b9-89b9-000000000010");
        Guid right = Guid.Parse("22222222-9b9b-49b9-89b9-000000000020");
        await SeedAsync(organizationId, "instant-left", utc, decision: null, reversed: false, disputeId: left);
        await SeedAsync(organizationId, "instant-right", plusTwo, decision: null, reversed: false, disputeId: right);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid[] sqlOrder = await context.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId)
            .OrderBy(dispute => dispute.RecordedAt)
            .ThenBy(dispute => dispute.Id)
            .Select(dispute => dispute.Id.Value)
            .ToArrayAsync();
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient instantClient = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using JsonDocument first = await ReadAsync(instantClient, "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=1");
        Assert.Equal(sqlOrder[0], Assert.Single(ReadIds(first)));
        string next = first.RootElement.GetProperty("nextCursor").GetString() ?? string.Empty;
        Assert.True(AllocationDisputeQueueCursor.TryDecode(next, AllocationDisputeQueue.NeedsDecision, out AllocationDisputeQueueCursor? boundary));
        DateTimeOffset stored = await RecordedAtAsync(sqlOrder[0]);
        Assert.Equal(stored.Ticks, boundary!.RecordedAt.Ticks);
        Assert.Equal(stored.Offset, boundary.RecordedAt.Offset);
        using JsonDocument second = await ReadAsync(
            instantClient,
            "/api/allocation-disputes/queue?queue=needs-decision&pagination=cursor&pageSize=1&cursor=" + next);
        Assert.Equal(sqlOrder[1], Assert.Single(ReadIds(second)));
        _ = client;
    }

    private async Task<DateTimeOffset> RecordedAtAsync(Guid disputeId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.Id == new AllocationDisputeId(disputeId))
            .Select(dispute => dispute.RecordedAt)
            .SingleAsync();
    }

    private async Task ResolveAsync(Guid disputeId, AllocationDisputeResolutionOutcome outcome)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        AllocationDispute dispute = await context.AllocationDisputes.SingleAsync(row => row.Id == new AllocationDisputeId(disputeId));
        context.AllocationDisputeResolutions.Add(AllocationDisputeResolution.Create(
            dispute.OrganizationId,
            dispute.Id,
            outcome,
            "left the queue",
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            dispute.RecordedAt.AddMinutes(5),
            Guid.NewGuid()));
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedAsync(
        OrganizationId organizationId,
        string statement,
        DateTimeOffset recordedAt,
        AllocationDisputeResolutionOutcome? decision,
        bool reversed,
        Guid? disputeId = null)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m, "INV-" + statement);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m, reference: statement);
        await using ReconciliationDbContext context = _fixture.CreateContext();
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
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        if (reversed)
        {
            Assert.True(allocation.Reverse("independent reversal", recordedAt).IsSuccess);
        }

        AllocationDispute dispute = AllocationDispute.Create(
            disputeId is Guid explicitId ? new AllocationDisputeId(explicitId) : AllocationDisputeId.New(),
            organizationId,
            allocation.Id,
            statement,
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            recordedAt,
            Guid.NewGuid());
        context.AddRange(allocation, dispute);
        if (decision is AllocationDisputeResolutionOutcome outcome)
        {
            context.AllocationDisputeResolutions.Add(AllocationDisputeResolution.Create(
                organizationId,
                dispute.Id,
                outcome,
                "recorded reason",
                ReviewCaseActorSource.DevelopmentJwt,
                "operator-a",
                recordedAt.AddMinutes(1),
                Guid.NewGuid()));
        }

        await context.SaveChangesAsync();
        return dispute.Id.Value;
    }

    private ReconciliationDbContext CreateContext(CommandCapture capture)
    {
        DbContextOptions<ReconciliationDbContext> options = new DbContextOptionsBuilder<ReconciliationDbContext>()
            .UseSqlServer(
                _fixture.ConnectionString,
                sqlServer => sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", "reconciliation"))
            .AddInterceptors(capture)
            .Options;
        return new ReconciliationDbContext(options);
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, path + " " + body);
        return JsonDocument.Parse(body);
    }

    private static async Task AssertProblemAsync(HttpClient client, string path, string code)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertProblemWithoutCursorAsync(HttpClient client, string path, string cursor)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(cursor, body, StringComparison.Ordinal);
    }

    private static Guid[] ReadIds(JsonDocument document)
    {
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("disputeId").GetGuid())
            .ToArray();
    }

    private static string EncodeRaw(byte version, byte direction, byte queue, DateTimeOffset recordedAt, Guid disputeId)
    {
        return EncodeRaw(version, direction, queue, recordedAt.Ticks, (short)recordedAt.Offset.TotalMinutes, disputeId);
    }

    private static string EncodeRaw(byte version, byte direction, byte queue, long ticks, short minutes, Guid disputeId)
    {
        byte[] payload = new byte[29];
        payload[0] = version;
        payload[1] = direction;
        payload[2] = queue;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(3), ticks);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(11), minutes);
        disputeId.TryWriteBytes(payload.AsSpan(13));
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(CapturedCommand.From(command));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record CapturedCommand(string Text, IReadOnlyList<SqlDbType> ParameterTypes)
    {
        public static CapturedCommand From(DbCommand command)
        {
            List<SqlDbType> types = [];
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter is SqlParameter sqlParameter)
                {
                    types.Add(sqlParameter.SqlDbType);
                }
            }

            return new CapturedCommand(command.CommandText, types);
        }
    }
}