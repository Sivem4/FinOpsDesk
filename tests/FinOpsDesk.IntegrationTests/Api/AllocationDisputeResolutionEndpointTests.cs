using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit.Abstractions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeResolutionEndpointTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private static readonly DateTimeOffset ConfirmedAt = DateTimeOffset.Parse("2026-11-07T09:00:00Z");

    private readonly SqlServerFixture _fixture;

    private readonly ITestOutputHelper _output;

    public AllocationDisputeResolutionEndpointTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task New_decision_replay_and_second_key_keep_one_terminal_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();

        HttpResponseMessage created = await client.SendAsync(Resolve(dispute.Id.Value, "upheld", " The challenge stands. ", key));
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False(created.Headers.Contains("Location"));
        using JsonDocument createdDocument = JsonDocument.Parse(createdBody);
        Assert.Equal("upheld", createdDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("The challenge stands.", createdDocument.RootElement.GetProperty("reason").GetString());
        Assert.Equal("operator-a", createdDocument.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("developmentJwt", createdDocument.RootElement.GetProperty("actorSource").GetString());
        Assert.False(createdDocument.RootElement.TryGetProperty("organizationId", out _));
        Assert.False(createdDocument.RootElement.TryGetProperty("idempotencyKey", out _));
        string decidedAt = createdDocument.RootElement.GetProperty("decidedAt").GetString() ?? string.Empty;

        using HttpClient other = Client(factory, organizationId, "operator-b");
        HttpResponseMessage replay = await other.SendAsync(Resolve(dispute.Id.Value, "upheld", "The challenge stands.", key));
        string replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument replayDocument = JsonDocument.Parse(replayBody);
        Assert.Equal("operator-a", replayDocument.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(decidedAt, replayDocument.RootElement.GetProperty("decidedAt").GetString());

        HttpResponseMessage secondKey = await client.SendAsync(
            Resolve(dispute.Id.Value, "not_upheld", "The challenge stands.", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, secondKey.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.resolution.already_resolved",
            await ReadCodeAsync(secondKey));
        Assert.Equal(1, await CountResolutionsAsync(organizationId));

        HttpResponseMessage read = await client.GetAsync(ResolutionPath(dispute.Id.Value));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using JsonDocument readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("upheld", readDocument.RootElement.GetProperty("decision").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Unresolved_dispute_returns_null_and_unknown_or_foreign_disputes_are_not_found()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganization = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        AllocationDispute foreign = await SeedDisputeAsync(otherOrganization);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, null);

        HttpResponseMessage open = await client.GetAsync(ResolutionPath(dispute.Id.Value));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await open.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("decision").ValueKind);

        HttpResponseMessage missing = await client.GetAsync(ResolutionPath(Guid.NewGuid()));
        HttpResponseMessage foreignRead = await client.GetAsync(ResolutionPath(foreign.Id.Value));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.not_found", await ReadCodeAsync(missing));
        Assert.Equal("reconciliation.allocation_dispute.not_found", await ReadCodeAsync(foreignRead));

        using HttpClient writer = Client(factory, organizationId, "operator-a");
        HttpResponseMessage foreignWrite = await writer.SendAsync(
            Resolve(foreign.Id.Value, "upheld", "hidden", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);
        Assert.Equal(0, await CountResolutionsAsync(otherOrganization));
    }

    [Fact]
    public async Task Actor_is_required_before_a_key_read_including_replay()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        ResolutionObservation observation = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(pause: null, save: null, observation, forceConflict: false);
        using HttpClient missing = Client(factory, organizationId, null);
        HttpResponseMessage denied = await missing.SendAsync(Resolve(dispute.Id.Value, "upheld", "reason", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("reconciliation.actor.required", await ReadCodeAsync(denied));
        Assert.Equal(0, observation.KeyReads);

        using FinOpsDeskApiFactory writer = new(_fixture.ConnectionString);
        using HttpClient client = Client(writer, organizationId, "operator-a");
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Resolve(dispute.Id.Value, "upheld", "reason", key))).StatusCode);
        HttpResponseMessage replay = await missing.SendAsync(Resolve(dispute.Id.Value, "upheld", "reason", key));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal("reconciliation.actor.required", await ReadCodeAsync(replay));
        Assert.Equal(0, observation.KeyReads);
    }

    [Fact]
    public async Task Validation_rejects_the_body_key_and_query_without_writing()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();

        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.outcome_required");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\"}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.reason_required");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\",\"reason\":\"   \"}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.reason_required");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\",\"reason\":\"" + new string('a', 501) + "\"}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.reason_too_long");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"open\",\"reason\":\"because\"}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.invalid_request");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\",\"reason\":\"because\",\"extra\":1}", key.ToString("D"))), "reconciliation.allocation_dispute.resolution.invalid_request");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\",\"reason\":\"because\"}", null)), "reconciliation.allocation_dispute.resolution.idempotency_key_required");
        await AssertProblemAsync(await client.SendAsync(ResolveRaw(dispute.Id.Value, "{\"outcome\":\"upheld\",\"reason\":\"because\"}", "  " + key.ToString("D"))), "reconciliation.allocation_dispute.resolution.idempotency_key_invalid");
        HttpResponseMessage query = await client.GetAsync(ResolutionPath(dispute.Id.Value) + "?organizationId=" + organizationId.Value.ToString("D"));
        await AssertProblemAsync(query, "reconciliation.allocation_dispute.resolution.invalid_request");
        Assert.Equal(0, await CountResolutionsAsync(organizationId));

        HttpResponseMessage kept = await client.SendAsync(Resolve(dispute.Id.Value, "not_upheld", "\uFEFFedge\uFEFF", key));
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await kept.Content.ReadAsStringAsync());
        Assert.Equal("\uFEFFedge\uFEFF", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Existing_key_wins_over_an_unknown_dispute()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Resolve(dispute.Id.Value, "upheld", "stored", key))).StatusCode);

        HttpResponseMessage conflict = await client.SendAsync(Resolve(Guid.NewGuid(), "not_upheld", "stored", key));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
            await ReadCodeAsync(conflict));
    }

    [Fact]
    public async Task Decision_does_not_change_registration_allocation_balances_or_financial_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Confirmed seeded = await SeedConfirmedAsync(organizationId, 18m);
        AllocationDispute dispute = await AddDisputeAsync(organizationId, seeded.AllocationId, "registration stays");
        FinancialState before = await ReadStateAsync(seeded.AllocationId);
        byte[] disputeRow = await ReadDisputeBytesAsync(dispute.Id);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.SendAsync(Resolve(dispute.Id.Value, "upheld", "judgment only", Guid.NewGuid()))).StatusCode);

        FinancialState after = await ReadStateAsync(seeded.AllocationId);
        Assert.Equal(before, after);
        Assert.Equal(disputeRow, await ReadDisputeBytesAsync(dispute.Id));
        HttpResponseMessage registration = await client.GetAsync("/api/allocation-disputes/" + dispute.Id.Value.ToString("D"));
        using JsonDocument document = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        Assert.Equal(
            ["actorId", "actorSource", "allocationId", "disputeId", "recordedAt", "statement"],
            document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Decision_is_allowed_after_reversal_and_replay_keeps_it()
    {
        OrganizationId organizationId = OrganizationId.New();
        Confirmed seeded = await SeedConfirmedAsync(organizationId, 21m);
        AllocationDispute dispute = await AddDisputeAsync(organizationId, seeded.AllocationId, "survives reversal");
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Reverse(seeded.AllocationId.Value, Guid.NewGuid()))).StatusCode);

        Guid key = Guid.NewGuid();
        HttpResponseMessage created = await client.SendAsync(Resolve(dispute.Id.Value, "not_upheld", "after reversal", key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        HttpResponseMessage replay = await client.SendAsync(Resolve(dispute.Id.Value, "not_upheld", "after reversal", key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument first = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using JsonDocument second = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.RootElement.GetProperty("decidedAt").GetString(), second.RootElement.GetProperty("decidedAt").GetString());
        Assert.Equal(AllocationStatus.Reversed, (await ReadStateAsync(seeded.AllocationId)).Status);
    }

    [Fact]
    public async Task Reversal_completes_while_a_decision_transaction_is_open()
    {
        // The gate below is a test transaction. It pauses after the decision INSERT and
        // before that test transaction commits. It does not prove the production unit of
        // work rollback exercised by Production_unit_of_work_recovers_a_sql_unique_collision.
        OrganizationId organizationId = OrganizationId.New();
        Confirmed seeded = await SeedConfirmedAsync(organizationId, 33m);
        AllocationDispute dispute = await AddDisputeAsync(organizationId, seeded.AllocationId, "no allocation lock");
        FinancialState before = await ReadStateAsync(seeded.AllocationId);
        ResolutionSavePause save = new(ResolutionSaveMode.HoldUncommittedInsert)
        {
            HoldAfterInsert = true,
        };
        ResolutionObservation observation = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(pause: null, save, observation, forceConflict: false);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid reversalKey = Guid.NewGuid();
        Task<HttpResponseMessage>? decision = null;
        try
        {
            decision = client.SendAsync(Resolve(dispute.Id.Value, "upheld", "held", Guid.NewGuid()));
            int holder = await save.PausedSession.Task.WaitAsync(WaitLimit);
            save.Resume.TrySetResult();
            await save.Inserted.Task.WaitAsync(WaitLimit);
            int openTransactions = await OpenTransactionCountAsync(holder);
            bool insertLock = await GrantedResolutionLockAsync(holder);
            _output.WriteLine(
                "decision-session=" + holder.ToString(CultureInfo.InvariantCulture)
                + " open_transaction_count=" + openTransactions.ToString(CultureInfo.InvariantCulture)
                + " resolution_lock=" + insertLock.ToString(CultureInfo.InvariantCulture));
            Assert.True(openTransactions > 0);
            Assert.True(insertLock);

            HttpResponseMessage reversed = await client.SendAsync(Reverse(seeded.AllocationId.Value, reversalKey)).WaitAsync(WaitLimit);
            Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
            Assert.False(await AllocationWaitBlockedByAsync(holder));
            Assert.True(await OpenTransactionCountAsync(holder) > 0);
        }
        finally
        {
            save.Resume.TrySetResult();
            save.Release.TrySetResult();
        }

        Assert.NotNull(decision);
        Assert.Equal(HttpStatusCode.Created, (await decision.WaitAsync(WaitLimit)).StatusCode);
        FinancialState after = await ReadStateAsync(seeded.AllocationId);
        Assert.Equal(AllocationStatus.Reversed, after.Status);
        Assert.Equal(before.InvoiceAllocated - 33m, after.InvoiceAllocated);
        Assert.Equal(before.InvoiceOutstanding + 33m, after.InvoiceOutstanding);
        Assert.Equal(before.BankAllocated - 33m, after.BankAllocated);
        Assert.Equal(before.BankUnapplied + 33m, after.BankUnapplied);
        Assert.Equal(before.AuditCount + 1, after.AuditCount);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(entry =>
            entry.AllocationId == seeded.AllocationId && entry.Action == FinancialAuditAction.Reversal);
        Assert.Equal("operator-a", audit.ActorId);
        Assert.Equal(reversalKey, audit.IdempotencyKey);
        Assert.Equal(33m, audit.Amount.Amount);
        Assert.Equal(1, await CountResolutionsAsync(organizationId));
    }

    [Fact]
    public async Task Production_unit_of_work_recovers_a_sql_unique_collision()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute left = await SeedDisputeAsync(organizationId, "left");
        AllocationDispute right = await SeedDisputeAsync(organizationId, "right");
        Guid key = Guid.NewGuid();
        ResolutionObservation observation = new();
        ProductionInsertBarrier barrier = new(_fixture.ConnectionString, observation);
        await using FinOpsDeskApiFactory factory = CreateProductionCollisionFactory(observation, barrier);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Task<HttpResponseMessage>? first = null;
        Task<HttpResponseMessage>? second = null;
        try
        {
            first = client.SendAsync(Resolve(left.Id.Value, "upheld", "left reason", key));
            second = client.SendAsync(Resolve(right.Id.Value, "upheld", "right reason", key));
            await barrier.BothEntered.WaitAsync(WaitLimit);
            Assert.Equal(2, observation.WritesEntered);

            HttpResponseMessage created = await first.WaitAsync(WaitLimit);
            HttpResponseMessage other = await second.WaitAsync(WaitLimit);
            HttpResponseMessage winner = created.StatusCode == HttpStatusCode.Created ? created : other;
            HttpResponseMessage loser = ReferenceEquals(winner, created) ? other : created;
            Assert.Equal(HttpStatusCode.Created, winner.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal(
                "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
                await ReadCodeAsync(loser));
        }
        finally
        {
            barrier.Release();
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        _output.WriteLine(
            "constraint=" + observation.ConstraintName
            + " sql=" + observation.SqlNumber.ToString(CultureInfo.InvariantCulture)
            + " had_transaction=" + observation.FailedCommandHadTransaction.ToString(CultureInfo.InvariantCulture)
            + " failed_session=" + observation.FailedSession.ToString(CultureInfo.InvariantCulture)
            + " recovery_session=" + observation.RecoverySession.ToString(CultureInfo.InvariantCulture)
            + " recovery_open_transactions=" + observation.RecoveryTransactionCount.ToString(CultureInfo.InvariantCulture)
            + " fresh_context=" + observation.RecoveryContextIsFresh.ToString(CultureInfo.InvariantCulture)
            + " failed_context=" + observation.FailedContextId
            + " recovery_context=" + observation.RecoveryContextId);
        Assert.Equal(2, observation.WritesEntered);
        Assert.Equal(typeof(UniqueConstraintConflictException), observation.HandlerExceptionType);
        Assert.Equal(1, observation.RecognizedCollisions);
        Assert.True(observation.SqlNumber is 2601 or 2627);
        Assert.Equal(
            AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName,
            observation.ConstraintName);
        Assert.DoesNotContain(
            AllocationDisputeResolutionConfiguration.PrimaryKeyName,
            observation.ConstraintMessage,
            StringComparison.Ordinal);
        Assert.True(observation.RecoveryReadObserved);
        Assert.True(observation.RecoverySessionFound);
        Assert.Equal(0, observation.RecoveryTransactionCount);
        Assert.True(observation.RecoveryContextIsFresh);
        Assert.NotEqual(0, observation.FailedSession);
        Assert.False(string.IsNullOrWhiteSpace(observation.FailedContextId));
        Assert.NotEqual(observation.FailedContextId, observation.RecoveryContextId);
        Assert.Equal(1, await CountResolutionsAsync(organizationId));
    }

    [Fact]
    public async Task Other_key_on_the_dispute_rechecks_the_requested_key_before_already_resolved()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute disputeX = await SeedDisputeAsync(organizationId, "dispute-x");
        AllocationDispute disputeY = await SeedDisputeAsync(organizationId, "dispute-y");
        await StoreResolutionAsync(organizationId, disputeX, Guid.NewGuid(), "original", "operator-x");
        ResolutionLookupPause pause = new() { PauseFirstMiss = true };
        ResolutionObservation observation = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(pause, save: null, observation, forceConflict: false);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();
        Task<HttpResponseMessage> pending = client.SendAsync(Resolve(disputeX.Id.Value, "upheld", "late", key));
        await pause.Entered.Task.WaitAsync(WaitLimit);

        using FinOpsDeskApiFactory otherFactory = new(_fixture.ConnectionString);
        using HttpClient other = Client(otherFactory, organizationId, "operator-b");
        Assert.Equal(
            HttpStatusCode.Created,
            (await other.SendAsync(Resolve(disputeY.Id.Value, "not_upheld", "other dispute", key))).StatusCode);
        pause.Resume.TrySetResult();
        HttpResponseMessage continued = await pending.WaitAsync(WaitLimit);

        Assert.Equal(HttpStatusCode.Conflict, continued.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
            await ReadCodeAsync(continued));
        Assert.Equal(0, observation.WritesEntered);
        Assert.Equal(2, await CountResolutionsAsync(organizationId));
    }

    [Fact]
    public async Task Recovery_rechecks_the_requested_key_after_the_losing_transaction_ends()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute disputeX = await SeedDisputeAsync(organizationId, "recover-x");
        AllocationDispute disputeY = await SeedDisputeAsync(organizationId, "recover-y");
        ResolutionLookupPause pause = new() { PauseAfterLoserRollback = true };
        ResolutionSavePause save = new(ResolutionSaveMode.RollbackThenThrow);
        ResolutionObservation observation = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(pause, save, observation, forceConflict: true);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();
        Task<HttpResponseMessage> pending = client.SendAsync(Resolve(disputeX.Id.Value, "upheld", "from a", key));
        await pause.Entered.Task.WaitAsync(WaitLimit);
        Assert.True(observation.LoserRolledBack);
        Assert.Equal(0, await OpenTransactionCountAsync(observation.LoserSession));

        await StoreResolutionAsync(organizationId, disputeX, Guid.NewGuid(), "already there", "operator-x");
        await StoreResolutionAsync(organizationId, disputeY, key, "other dispute", "operator-y");
        pause.Resume.TrySetResult();
        HttpResponseMessage continued = await pending.WaitAsync(WaitLimit);

        Assert.Equal(HttpStatusCode.Conflict, continued.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
            await ReadCodeAsync(continued));
        Assert.Equal(2, await CountResolutionsAsync(organizationId));
    }

    // These collisions classify a paused test transaction. ResolutionSaveGate replaces
    // IUnitOfWork, so they do not prove production rollback or exception translation.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_key_and_same_dispute_collision_uses_the_shared_recovery(bool payloadsMatch)
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        Guid key = Guid.NewGuid();
        await AssertCollisionAsync(
            organizationId,
            dispute.Id.Value,
            dispute.Id.Value,
            key,
            key,
            payloadsMatch ? "same" : "left",
            payloadsMatch ? "same" : "right",
            payloadsMatch ? HttpStatusCode.OK : HttpStatusCode.Conflict,
            payloadsMatch ? null : "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
            AllocationDisputeResolutionConfiguration.PrimaryKeyName,
            AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName);
    }

    [Fact]
    public async Task Two_keys_for_one_dispute_collide_on_the_primary_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        await AssertCollisionAsync(
            organizationId,
            dispute.Id.Value,
            dispute.Id.Value,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "first",
            "second",
            HttpStatusCode.Conflict,
            "reconciliation.allocation_dispute.resolution.already_resolved",
            AllocationDisputeResolutionConfiguration.PrimaryKeyName);
    }

    [Fact]
    public async Task Same_key_for_two_disputes_collides_on_the_idempotency_index()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute left = await SeedDisputeAsync(organizationId, "left");
        AllocationDispute right = await SeedDisputeAsync(organizationId, "right");
        Guid key = Guid.NewGuid();
        await AssertCollisionAsync(
            organizationId,
            left.Id.Value,
            right.Id.Value,
            key,
            key,
            "left reason",
            "right reason",
            HttpStatusCode.Conflict,
            "reconciliation.allocation_dispute.resolution.idempotency_payload_conflict",
            AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName);
    }

    [Fact]
    public async Task Sql_rejects_update_and_delete_and_recognizes_only_the_named_foreign_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        AllocationDispute second = await SeedDisputeAsync(organizationId, "second");
        AllocationDispute uncheckedDispute = await SeedDisputeAsync(organizationId, "unchecked");
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Resolve(dispute.Id.Value, "upheld", "kept", key))).StatusCode);

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (SqlCommand trigger = new(
            """
            SELECT COUNT(*)
            FROM sys.triggers
            WHERE name = N'TR_AllocationDisputeResolutions_RejectMutation'
              AND is_disabled = 0
            """,
            connection))
        {
            Assert.Equal(1, (int)(await trigger.ExecuteScalarAsync())!);
        }

        SqlException updated = await ExecuteAsync(
            connection,
            "UPDATE [reconciliation].[AllocationDisputeResolutions] SET [Reason] = N'changed' WHERE [AllocationDisputeId] = @id",
            ("@id", dispute.Id.Value));
        SqlException deleted = await ExecuteAsync(
            connection,
            "DELETE FROM [reconciliation].[AllocationDisputeResolutions] WHERE [AllocationDisputeId] = @id",
            ("@id", dispute.Id.Value));
        Assert.Equal(50004, updated.Number);
        Assert.Equal(50004, deleted.Number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        AllocationDisputeResolution stored = await context.AllocationDisputeResolutions.SingleAsync(
            resolution => resolution.AllocationDisputeId == dispute.Id);
        Assert.Equal("kept", stored.Reason);

        HttpResponseMessage replay = await client.SendAsync(Resolve(dispute.Id.Value, "upheld", "kept", key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.SendAsync(Resolve(second.Id.Value, "not_upheld", "another", Guid.NewGuid()))).StatusCode);

        SqlException foreignKey = await ExecuteAsync(
            connection,
            """
            INSERT INTO [reconciliation].[AllocationDisputeResolutions]
                ([OrganizationId], [AllocationDisputeId], [Outcome], [Reason], [ActorSource], [ActorId], [DecidedAt], [IdempotencyKey])
            VALUES
                (@organizationId, @disputeId, 1, N'reason', 1, N'actor', SYSUTCDATETIME(), @key)
            """,
            ("@organizationId", organizationId.Value),
            ("@disputeId", Guid.NewGuid()),
            ("@key", Guid.NewGuid()));
        Assert.Equal(547, foreignKey.Number);
        Assert.Contains(
            AllocationDisputeResolutionConfiguration.ForeignKeyName,
            foreignKey.Message,
            StringComparison.Ordinal);
        Assert.True(new AllocationDisputeResolutionConflict().IsDisputeForeignKeyConflict(foreignKey));

        SqlException check = await ExecuteAsync(
            connection,
            """
            INSERT INTO [reconciliation].[AllocationDisputeResolutions]
                ([OrganizationId], [AllocationDisputeId], [Outcome], [Reason], [ActorSource], [ActorId], [DecidedAt], [IdempotencyKey])
            VALUES
                (@organizationId, @disputeId, 9, N'reason', 1, N'actor', SYSUTCDATETIME(), @key)
            """,
            ("@organizationId", organizationId.Value),
            ("@disputeId", uncheckedDispute.Id.Value),
            ("@key", Guid.NewGuid()));
        Assert.Equal(547, check.Number);
        Assert.False(new AllocationDisputeResolutionConflict().IsDisputeForeignKeyConflict(check));
    }

    [Fact]
    public async Task Existing_dispute_has_no_backfilled_resolution()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeResolutions.CountAsync(
            resolution => resolution.AllocationDisputeId == dispute.Id));
        string migration = await File.ReadAllTextAsync(FindMigration());
        Assert.DoesNotContain("INSERT INTO", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP TRIGGER [reconciliation].[TR_AllocationDisputeResolutions_RejectMutation]", migration, StringComparison.Ordinal);
    }

    private async Task AssertCollisionAsync(
        OrganizationId organizationId,
        Guid leftDispute,
        Guid rightDispute,
        Guid leftKey,
        Guid rightKey,
        string leftReason,
        string rightReason,
        HttpStatusCode secondStatus,
        string? secondCode,
        params string[] allowedConstraintNames)
    {
        ResolutionSavePause save = new(ResolutionSaveMode.HoldUncommittedInsert)
        {
            HoldAfterInsert = true,
        };
        ResolutionObservation observation = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(pause: null, save, observation, forceConflict: false);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Task<HttpResponseMessage> first = client.SendAsync(Resolve(leftDispute, "upheld", leftReason, leftKey));
        HttpResponseMessage created;
        HttpResponseMessage other;
        try
        {
            int holder = await save.PausedSession.Task.WaitAsync(WaitLimit);
            Task<HttpResponseMessage> second = client.SendAsync(Resolve(rightDispute, "upheld", rightReason, rightKey));
            await WaitForCountAsync(() => observation.WritesEntered, 2);
            save.Resume.TrySetResult();
            await save.Inserted.Task.WaitAsync(WaitLimit);
            int waiter = await WaitForBlockedSessionAsync(holder);
            observation.LoserSession = waiter;
            string waiting = await DescribeResolutionWaitAsync(waiter);
            _output.WriteLine(waiting);
            Assert.Contains("AllocationDisputeResolutions", waiting, StringComparison.Ordinal);
            save.Release.TrySetResult();
            created = await first.WaitAsync(WaitLimit);
            other = await second.WaitAsync(WaitLimit);
        }
        finally
        {
            save.Resume.TrySetResult();
            save.Release.TrySetResult();
        }

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(secondStatus, other.StatusCode);
        if (secondCode is not null)
        {
            Assert.Equal(secondCode, await ReadCodeAsync(other));
        }

        Assert.Equal(1, observation.RecognizedCollisions);
        Assert.Contains(observation.ConstraintName, allowedConstraintNames);
        Assert.True(observation.SqlNumber is 2601 or 2627);
        Assert.Equal(0, observation.RecoveryTransactionCount);
        Assert.True(observation.KeyMisses >= 2);
        Assert.Equal(1, await CountResolutionsAsync(organizationId));
        if (allowedConstraintNames.Length == 1
            && allowedConstraintNames[0] == AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName)
        {
            Assert.DoesNotContain(
                AllocationDisputeResolutionConfiguration.PrimaryKeyName,
                observation.ConstraintMessage,
                StringComparison.Ordinal);
        }

        if (allowedConstraintNames.Length == 1
            && allowedConstraintNames[0] == AllocationDisputeResolutionConfiguration.PrimaryKeyName)
        {
            Assert.DoesNotContain(
                AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName,
                observation.ConstraintMessage,
                StringComparison.Ordinal);
        }
    }

    private FinOpsDeskApiFactory CreateFactory(
        ResolutionLookupPause? pause,
        ResolutionSavePause? save,
        ResolutionObservation? observation,
        bool forceConflict)
    {
        return new FinOpsDeskApiFactory(_fixture.ConnectionString, services =>
        {
            if (observation is not null)
            {
                services.AddSingleton(observation);
            }

            if (pause is not null || observation is not null)
            {
                if (pause is not null)
                {
                    services.AddSingleton(pause);
                }

                services.RemoveAll<IAllocationDisputeResolutionLookup>();
                services.AddScoped<IAllocationDisputeResolutionLookup>(provider => new PausingResolutionLookup(
                    provider.GetRequiredService<AllocationDisputeResolutionStore>(),
                    provider.GetService<ResolutionLookupPause>(),
                    provider.GetService<ResolutionObservation>()));
            }

            if (save is not null)
            {
                services.AddSingleton(save);
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new ResolutionSaveGate(
                    provider.GetRequiredService<ReconciliationDbContext>(),
                    provider.GetRequiredService<ResolutionSavePause>(),
                    provider.GetService<ResolutionObservation>()));
            }

            if (forceConflict)
            {
                services.RemoveAll<IAllocationDisputeResolutionConflict>();
                services.AddSingleton<IAllocationDisputeResolutionConflict>(new ForcedResolutionConflict());
            }
            else if (observation is not null)
            {
                services.RemoveAll<IAllocationDisputeResolutionConflict>();
                services.AddSingleton<IAllocationDisputeResolutionConflict>(new ObservingResolutionConflict(
                    new AllocationDisputeResolutionConflict(),
                    observation));
            }
        });
    }

    private FinOpsDeskApiFactory CreateProductionCollisionFactory(
        ResolutionObservation observation,
        ProductionInsertBarrier barrier)
    {
        return new FinOpsDeskApiFactory(_fixture.ConnectionString, services =>
        {
            services.AddSingleton(observation);
            services.AddSingleton(barrier);
            services.ConfigureDbContext<ReconciliationDbContext>(
                options => options.AddInterceptors(barrier),
                ServiceLifetime.Scoped);
            services.RemoveAll<IAllocationDisputeResolutionConflict>();
            services.AddSingleton<IAllocationDisputeResolutionConflict>(new ObservingResolutionConflict(
                new AllocationDisputeResolutionConflict(),
                observation));
        });
    }

    private async Task<AllocationDispute> SeedDisputeAsync(OrganizationId organizationId, string statement = "challenge")
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 10m, reference: number);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(10m, PersistenceTestData.Pln),
            ConfirmedAt);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank, allocation);
        await context.SaveChangesAsync();
        return await AddDisputeAsync(organizationId, allocation.Id, statement);
    }

    private async Task<AllocationDispute> AddDisputeAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        string statement)
    {
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            organizationId,
            allocationId,
            statement,
            ReviewCaseActorSource.DevelopmentJwt,
            "registrar",
            ConfirmedAt,
            Guid.NewGuid());
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AllocationDisputes.Add(dispute);
        await context.SaveChangesAsync();
        return dispute;
    }

    private async Task StoreResolutionAsync(
        OrganizationId organizationId,
        AllocationDispute dispute,
        Guid key,
        string reason,
        string actorId)
    {
        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            organizationId,
            dispute.Id,
            AllocationDisputeResolutionOutcome.Upheld,
            reason,
            ReviewCaseActorSource.DevelopmentJwt,
            actorId,
            DateTimeOffset.Parse("2026-11-07T11:00:00Z"),
            key);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AllocationDisputeResolutions.Add(resolution);
        await context.SaveChangesAsync();
    }

    private async Task<Confirmed> SeedConfirmedAsync(OrganizationId organizationId, decimal amount)
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, amount, reference: number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bank.Id);
        Assert.True(AllocationConfirmation.Apply(
            storedInvoice,
            storedBank,
            new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            new Money(amount, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return new Confirmed(allocation.Id);
    }

    private async Task<FinancialState> ReadStateAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == allocation.BankTransactionId);
        int audits = await context.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId);
        byte[] rowVersion = context.Entry(allocation)
            .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
            .CurrentValue!
            .ToArray();
        return new FinancialState(
            allocation.Status,
            invoice.AllocatedAmount.Amount,
            invoice.OutstandingAmount.Amount,
            bank.AllocatedAmount.Amount,
            bank.UnappliedAmount.Amount,
            audits,
            Convert.ToHexString(rowVersion));
    }

    private async Task<byte[]> ReadDisputeBytesAsync(AllocationDisputeId disputeId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        AllocationDispute dispute = await context.AllocationDisputes.SingleAsync(candidate => candidate.Id == disputeId);
        return Encoding.UTF8.GetBytes(string.Join(
            '|',
            dispute.Statement,
            dispute.ActorId,
            dispute.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
            dispute.IdempotencyKey.ToString("D")));
    }

    private async Task<int> CountResolutionsAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.AllocationDisputeResolutions.CountAsync(
            resolution => resolution.OrganizationId == organizationId);
    }

    private async Task<int> WaitForBlockedSessionAsync(int blockingSessionId)
    {
        using CancellationTokenSource timeout = new(WaitLimit);
        while (!timeout.IsCancellationRequested)
        {
            await using SqlConnection connection = new(_fixture.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT session_id
                FROM sys.dm_exec_requests
                WHERE blocking_session_id = @blockingSessionId
                """;
            command.Parameters.Add(new SqlParameter("@blockingSessionId", blockingSessionId));
            object? value = await command.ExecuteScalarAsync(timeout.Token);
            if (value is not null and not DBNull)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            await Task.Yield();
        }

        throw new TimeoutException("No request was blocked by session " + blockingSessionId.ToString(CultureInfo.InvariantCulture));
    }

    private async Task WaitForCountAsync(Func<int> read, int expected)
    {
        using CancellationTokenSource timeout = new(WaitLimit);
        while (!timeout.IsCancellationRequested)
        {
            if (read() >= expected)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("Count stayed at " + read().ToString(CultureInfo.InvariantCulture));
    }

    private async Task<string> DescribeResolutionWaitAsync(int sessionId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.resource_type, l.request_mode, l.request_status,
                   COALESCE(OBJECT_NAME(p.object_id), OBJECT_NAME(l.resource_associated_entity_id))
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
                ON l.resource_associated_entity_id = p.hobt_id
                OR l.resource_associated_entity_id = p.partition_id
            WHERE l.resource_database_id = DB_ID()
              AND l.request_session_id = @sessionId
              AND l.request_status = N'WAIT'
            """;
        command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder waiting = new();
        while (await reader.ReadAsync())
        {
            waiting.Append(reader.GetString(0)).Append(' ')
                .Append(reader.GetString(1)).Append(' ')
                .Append(reader.GetString(2)).Append(' ')
                .Append(reader.IsDBNull(3) ? "?" : reader.GetString(3))
                .Append("; ");
        }

        return waiting.ToString();
    }

    private async Task<bool> AllocationWaitBlockedByAsync(int blockingSessionId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
                ON l.resource_associated_entity_id = p.hobt_id
                OR l.resource_associated_entity_id = p.partition_id
            WHERE l.resource_database_id = DB_ID()
              AND l.request_status = N'WAIT'
              AND COALESCE(p.object_id, l.resource_associated_entity_id) = OBJECT_ID(N'reconciliation.Allocations')
              AND l.request_session_id IN (
                    SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id = @blockingSessionId)
            """;
        command.Parameters.Add(new SqlParameter("@blockingSessionId", blockingSessionId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    private async Task<bool> GrantedResolutionLockAsync(int sessionId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
                ON l.resource_associated_entity_id = p.hobt_id
                OR l.resource_associated_entity_id = p.partition_id
            WHERE l.resource_database_id = DB_ID()
              AND l.request_session_id = @sessionId
              AND l.request_status = N'GRANT'
              AND COALESCE(OBJECT_NAME(p.object_id), OBJECT_NAME(l.resource_associated_entity_id)) = N'AllocationDisputeResolutions'
            """;
        command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    private async Task<int> OpenTransactionCountAsync(int sessionId)
    {
        if (sessionId == 0)
        {
            return 0;
        }

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT open_transaction_count FROM sys.dm_exec_sessions WHERE session_id = @sessionId";
        command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<SqlException> ExecuteAsync(
        SqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private static string FindMigration()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "backend",
                "Modules",
                "FinOpsDesk.Modules.Reconciliation",
                "Infrastructure",
                "Persistence",
                "Migrations");
            if (Directory.Exists(candidate))
            {
                return Directory.GetFiles(candidate, "*_AddAllocationDisputeResolutions.cs").Single();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Resolution migration source was not found.");
    }

    private static HttpClient Client(FinOpsDeskApiFactory factory, OrganizationId organizationId, string? subject)
    {
        return factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private static string ResolutionPath(Guid disputeId)
    {
        return "/api/allocation-disputes/" + disputeId.ToString("D") + "/resolution";
    }

    private static HttpRequestMessage Resolve(Guid disputeId, string outcome, string reason, Guid key)
    {
        return ResolveRaw(
            disputeId,
            "{\"outcome\":" + JsonSerializer.Serialize(outcome) + ",\"reason\":" + JsonSerializer.Serialize(reason) + "}",
            key.ToString("D"));
    }

    private static HttpRequestMessage ResolveRaw(Guid disputeId, string json, string? key)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ResolutionPath(disputeId))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static HttpRequestMessage Reverse(Guid allocationId, Guid key)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/allocations/" + allocationId.ToString("D") + "/reverse")
        {
            Content = new StringContent("{\"reason\":\"customer refund\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return request;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ReadCodeAsync(response));
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }

    private sealed record Confirmed(AllocationId AllocationId);

    private sealed record FinancialState(
        AllocationStatus Status,
        decimal InvoiceAllocated,
        decimal InvoiceOutstanding,
        decimal BankAllocated,
        decimal BankUnapplied,
        int AuditCount,
        string RowVersion);

    private enum ResolutionSaveMode
    {
        HoldUncommittedInsert = 1,
        RollbackThenThrow = 2,
    }

    private sealed class ResolutionLookupPause
    {
        private int _misses;

        private int _recoveryMisses;

        public bool PauseFirstMiss { get; init; }

        public bool PauseAfterLoserRollback { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldPauseFirstMiss()
        {
            return PauseFirstMiss && Interlocked.Increment(ref _misses) == 1;
        }

        public bool ShouldPauseRecovery()
        {
            return PauseAfterLoserRollback && Interlocked.Increment(ref _recoveryMisses) == 1;
        }
    }

    private sealed class ResolutionSavePause
    {
        private int _paused;

        public ResolutionSavePause(ResolutionSaveMode mode)
        {
            Mode = mode;
        }

        public ResolutionSaveMode Mode { get; }

        public bool HoldAfterInsert { get; init; }

        public TaskCompletionSource<int> PausedSession { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryMarkFirstPause()
        {
            return Interlocked.Increment(ref _paused) == 1;
        }
    }

    private sealed class ResolutionObservation
    {
        private int _keyReads;

        private int _keyMisses;

        private int _writesEntered;

        private int _recognized;

        private int _loserRolledBack;

        private int _loserSession;

        private int _recoveryTransactionCount = -1;

        public int KeyReads => Volatile.Read(ref _keyReads);

        public int KeyMisses => Volatile.Read(ref _keyMisses);

        public int WritesEntered => Volatile.Read(ref _writesEntered);

        public int RecognizedCollisions => Volatile.Read(ref _recognized);

        public bool LoserRolledBack => Volatile.Read(ref _loserRolledBack) == 1;

        public int LoserSession
        {
            get => Volatile.Read(ref _loserSession);
            set => Volatile.Write(ref _loserSession, value);
        }

        public int SqlNumber { get; private set; }

        public string ConstraintName { get; private set; } = string.Empty;

        public string ConstraintMessage { get; private set; } = string.Empty;

        public int RecoveryTransactionCount => Volatile.Read(ref _recoveryTransactionCount);

        public Type? HandlerExceptionType { get; private set; }

        public bool FailedCommandHadTransaction { get; private set; }

        public int FailedSession { get; private set; }

        public int RecoverySession { get; private set; }

        public bool RecoveryContextIsFresh { get; private set; }

        public bool RecoveryReadObserved { get; private set; }

        public bool RecoverySessionFound { get; private set; }

        public string FailedContextId { get; private set; } = string.Empty;

        public string RecoveryContextId { get; private set; } = string.Empty;

        public void RecordKeyRead(bool missed)
        {
            Interlocked.Increment(ref _keyReads);
            if (missed)
            {
                Interlocked.Increment(ref _keyMisses);
            }
        }

        public void EnterWrite()
        {
            Interlocked.Increment(ref _writesEntered);
        }

        public void MarkLoserRolledBack(int sessionId)
        {
            Volatile.Write(ref _loserSession, sessionId);
            Volatile.Write(ref _loserRolledBack, 1);
        }

        public void RecordCollision(Exception exception)
        {
            string? name = AllocationDisputeResolutionConflict.RecognizeUnique(exception);
            if (name is null)
            {
                return;
            }

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SqlException sqlException)
                {
                    SqlNumber = sqlException.Number;
                    ConstraintMessage = sqlException.Message;
                    break;
                }
            }

            ConstraintName = name;
            Interlocked.Increment(ref _recognized);
        }

        public void RecordRecoveryTransactionCount(int count)
        {
            Volatile.Write(ref _recoveryTransactionCount, count);
        }

        public void RecordHandlerException(Exception exception)
        {
            HandlerExceptionType = exception.GetType();
        }

        public void RecordFailedCommand(int sessionId, bool hadTransaction, string contextId)
        {
            FailedSession = sessionId;
            FailedCommandHadTransaction = hadTransaction;
            FailedContextId = contextId;
        }

        public void RecordRecoveryRead(
            bool freshContext,
            int sessionId,
            bool sessionFound,
            int transactionCount,
            string contextId)
        {
            RecoveryContextIsFresh = freshContext;
            RecoverySession = sessionId;
            RecoverySessionFound = sessionFound;
            RecoveryContextId = contextId;
            RecoveryReadObserved = true;
            RecordRecoveryTransactionCount(transactionCount);
        }
    }

    private sealed class ForcedResolutionConflict : IAllocationDisputeResolutionConflict
    {
        public bool IsRecognizedUniqueConflict(Exception exception)
        {
            return exception is UniqueConstraintConflictException;
        }

        public bool IsDisputeForeignKeyConflict(Exception exception)
        {
            return false;
        }
    }

    private sealed class ObservingResolutionConflict : IAllocationDisputeResolutionConflict
    {
        private readonly AllocationDisputeResolutionConflict _inner;

        private readonly ResolutionObservation _observation;

        public ObservingResolutionConflict(
            AllocationDisputeResolutionConflict inner,
            ResolutionObservation observation)
        {
            _inner = inner;
            _observation = observation;
        }

        public bool IsRecognizedUniqueConflict(Exception exception)
        {
            _observation.RecordHandlerException(exception);
            bool recognized = _inner.IsRecognizedUniqueConflict(exception);
            if (recognized)
            {
                _observation.RecordCollision(exception);
            }

            return recognized;
        }

        public bool IsDisputeForeignKeyConflict(Exception exception)
        {
            return _inner.IsDisputeForeignKeyConflict(exception);
        }
    }

    private sealed class PausingResolutionLookup : IAllocationDisputeResolutionLookup
    {
        private readonly AllocationDisputeResolutionStore _inner;

        private readonly ResolutionLookupPause? _pause;

        private readonly ResolutionObservation? _observation;

        public PausingResolutionLookup(
            AllocationDisputeResolutionStore inner,
            ResolutionLookupPause? pause,
            ResolutionObservation? observation)
        {
            _inner = inner;
            _pause = pause;
            _observation = observation;
        }

        public async Task<AllocationDisputeResolution?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (_observation is { LoserRolledBack: true, RecoveryTransactionCount: -1 })
            {
                _observation.RecordRecoveryTransactionCount(await OpenTransactionsAsync(_observation.LoserSession));
            }

            AllocationDisputeResolution? found = await _inner.FindByIdempotencyKeyAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);
            _observation?.RecordKeyRead(found is null);
            if (found is null && _pause is not null)
            {
                bool pause = _pause.PauseAfterLoserRollback
                    ? _observation?.LoserRolledBack == true && _pause.ShouldPauseRecovery()
                    : _pause.ShouldPauseFirstMiss();
                if (pause)
                {
                    _pause.Entered.TrySetResult();
                    await _pause.Resume.Task.WaitAsync(cancellationToken);
                }
            }

            return found;
        }

        public Task<AllocationDisputeResolution?> FindByDisputeAsync(
            OrganizationId organizationId,
            AllocationDisputeId disputeId,
            CancellationToken cancellationToken)
        {
            return _inner.FindByDisputeAsync(organizationId, disputeId, cancellationToken);
        }

        public Task<bool> DisputeExistsAsync(
            OrganizationId organizationId,
            AllocationDisputeId disputeId,
            CancellationToken cancellationToken)
        {
            return _inner.DisputeExistsAsync(organizationId, disputeId, cancellationToken);
        }

        private static async Task<int> OpenTransactionsAsync(int sessionId)
        {
            if (sessionId == 0)
            {
                return 0;
            }

            string? connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FinOpsDesk");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return -2;
            }

            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT open_transaction_count FROM sys.dm_exec_sessions WHERE session_id = @sessionId";
            command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
            object? value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
    }

    private sealed class ResolutionSaveGate : IUnitOfWork
    {
        private readonly ReconciliationDbContext _context;

        private readonly ResolutionSavePause _pause;

        private readonly ResolutionObservation? _observation;

        public ResolutionSaveGate(
            ReconciliationDbContext context,
            ResolutionSavePause pause,
            ResolutionObservation? observation)
        {
            _context = context;
            _pause = pause;
            _observation = observation;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            bool resolutionInsert = _context.ChangeTracker
                .Entries<AllocationDisputeResolution>()
                .Any(entry => entry.State == EntityState.Added);
            if (!resolutionInsert)
            {
                await ((IUnitOfWork)_context).SaveChangesAsync(cancellationToken);
                return;
            }

            _observation?.EnterWrite();
            if (_pause.Mode == ResolutionSaveMode.RollbackThenThrow)
            {
                int sessionId = await ReadSessionIdAsync(cancellationToken);
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                _observation?.MarkLoserRolledBack(sessionId);
                throw new UniqueConstraintConflictException("forced");
            }

            await using var held = await _context.Database.BeginTransactionAsync(cancellationToken);
            if (_pause.TryMarkFirstPause())
            {
                int sessionId = await ReadSessionIdAsync(cancellationToken);
                _pause.PausedSession.TrySetResult(sessionId);
                await _pause.Resume.Task.WaitAsync(cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                if (_pause.HoldAfterInsert)
                {
                    _pause.Inserted.TrySetResult();
                    await _pause.Release.Task.WaitAsync(cancellationToken);
                }

                await held.CommitAsync(cancellationToken);
                return;
            }

            int loserSession = await ReadSessionIdAsync(cancellationToken);
            try
            {
                if (_pause.HoldAfterInsert)
                {
                    await _pause.Inserted.Task.WaitAsync(cancellationToken);
                }

                await _context.SaveChangesAsync(cancellationToken);
                await held.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                await held.RollbackAsync(cancellationToken);
                _observation?.MarkLoserRolledBack(loserSession);
                throw new UniqueConstraintConflictException(
                    "A unique constraint rejected the commit.",
                    exception);
            }
        }

        private async Task<int> ReadSessionIdAsync(CancellationToken cancellationToken)
        {
            System.Data.Common.DbConnection connection = _context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using System.Data.Common.DbCommand command = connection.CreateCommand();
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT @@SPID";
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
    }

    private sealed class ProductionInsertBarrier : DbCommandInterceptor
    {
        private readonly string _connectionString;

        private readonly ResolutionObservation _observation;

        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _entered;

        private int _failedSession;

        private DbContext? _failedContext;

        private int _recoveryObserved;

        public ProductionInsertBarrier(string connectionString, ResolutionObservation observation)
        {
            _connectionString = connectionString;
            _observation = observation;
        }

        public Task BothEntered => _bothEntered.Task;

        public void Release()
        {
            _bothEntered.TrySetResult();
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForCompetingInsertAsync(command, cancellationToken);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsResolutionInsert(command.CommandText))
            {
                await WaitForCompetingInsertAsync(command, cancellationToken);
            }
            else
            {
                await ObserveRecoveryReadAsync(command, eventData, cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (IsResolutionInsert(command.CommandText))
            {
                _failedContext = eventData.Context;
                _failedSession = ReadStoredSession(command);
                _observation.RecordFailedCommand(
                    _failedSession,
                    command.Transaction is not null,
                    eventData.Context?.ContextId.ToString() ?? string.Empty);
            }

            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }

        private async Task WaitForCompetingInsertAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!IsResolutionInsert(command.CommandText))
            {
                return;
            }

            int sessionId = await ReadSessionIdAsync(command, cancellationToken);
            StoreSession(command, sessionId);
            _observation.EnterWrite();
            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.TrySetResult();
            }

            await _bothEntered.Task.WaitAsync(cancellationToken);
        }

        private async Task ObserveRecoveryReadAsync(
            DbCommand command,
            CommandEventData eventData,
            CancellationToken cancellationToken)
        {
            if (_failedContext is null || !IsResolutionKeyRead(command.CommandText))
            {
                return;
            }

            if (Interlocked.Exchange(ref _recoveryObserved, 1) == 1)
            {
                return;
            }

            int sessionId = await ReadSessionIdAsync(command, cancellationToken);
            (bool found, int count) = await ReadOpenTransactionsAsync(_failedSession, cancellationToken);
            _observation.RecordRecoveryRead(
                eventData.Context is not null && !ReferenceEquals(eventData.Context, _failedContext),
                sessionId,
                found,
                count,
                eventData.Context?.ContextId.ToString() ?? string.Empty);
        }

        private static bool IsResolutionInsert(string commandText)
        {
            return commandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && commandText.Contains("[AllocationDisputeResolutions]", StringComparison.Ordinal);
        }

        private static bool IsResolutionKeyRead(string commandText)
        {
            return commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && commandText.Contains("[AllocationDisputeResolutions]", StringComparison.Ordinal)
                && commandText.Contains("[IdempotencyKey]", StringComparison.Ordinal);
        }

        private static void StoreSession(DbCommand command, int sessionId)
        {
            SessionSlots.Set(command, sessionId);
        }

        private static int ReadStoredSession(DbCommand command)
        {
            return SessionSlots.Get(command);
        }

        private static async Task<int> ReadSessionIdAsync(DbCommand command, CancellationToken cancellationToken)
        {
            DbConnection connection = command.Connection ?? throw new InvalidOperationException("The command has no connection.");
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using DbCommand probe = connection.CreateCommand();
            probe.Transaction = command.Transaction;
            probe.CommandText = "SELECT @@SPID";
            object? value = await probe.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private async Task<(bool Found, int Count)> ReadOpenTransactionsAsync(int sessionId, CancellationToken cancellationToken)
        {
            if (sessionId == 0)
            {
                return (false, -1);
            }

            await using SqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT open_transaction_count FROM sys.dm_exec_sessions WHERE session_id = @sessionId";
            command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull
                ? (false, -1)
                : (true, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }

        private static class SessionSlots
        {
            private static readonly ConditionalWeakTable<DbCommand, StrongBox<int>> Slots = new();

            public static void Set(DbCommand command, int sessionId)
            {
                Slots.Add(command, new StrongBox<int>(sessionId));
            }

            public static int Get(DbCommand command)
            {
                return Slots.TryGetValue(command, out StrongBox<int>? box) ? box.Value : 0;
            }
        }
    }
}