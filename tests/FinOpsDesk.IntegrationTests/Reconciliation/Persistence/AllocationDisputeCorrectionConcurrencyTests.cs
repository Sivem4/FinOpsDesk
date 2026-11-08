using System.Data;
using System.Reflection;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit.Abstractions;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeCorrectionConcurrencyTests
{
    private const string Reason = "Customer evidence accepted";

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CorrectedAt = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    private readonly ITestOutputHelper _output;

    public AllocationDisputeCorrectionConcurrencyTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Commit_between_absent_key_reads_and_dispute_read_replays_the_same_correction()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("reversal-by-key");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = StartCorrectionAsync(
            provider,
            seeded,
            "actor-b",
            seeded.Key,
            gate,
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        Result<CorrectAllocationDisputeResponse> committed = await CorrectAsync(
            provider,
            seeded,
            "actor-a",
            seeded.Key,
            timeout.Token);
        gate.Release();
        Result<CorrectAllocationDisputeResponse> replay = await waiting.WaitAsync(timeout.Token);

        Assert.True(committed.IsSuccess);
        Assert.False(committed.Value.Replayed);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.Replayed);
        Assert.Equal("actor-a", replay.Value.ActorId);
        Assert.Equal(seeded.DisputeId.Value, replay.Value.DisputeId);
        Assert.Equal(seeded.AllocationId.Value, replay.Value.AllocationId);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    [Fact]
    public async Task Commit_before_reversed_status_read_replays_instead_of_already_reversed()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("correction-by-dispute");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = StartCorrectionAsync(
            provider,
            seeded,
            "actor-b",
            seeded.Key,
            gate,
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token);
        gate.Release();
        Result<CorrectAllocationDisputeResponse> replay = await waiting.WaitAsync(timeout.Token);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.Replayed);
        Assert.Equal("actor-a", replay.Value.ActorId);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    [Fact]
    public async Task Other_key_committed_before_status_read_is_already_corrected()
    {
        Seeded seeded = await SeedAsync();
        Guid otherKey = Guid.NewGuid();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("correction-by-dispute");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = StartCorrectionAsync(
            provider,
            seeded,
            "actor-b",
            otherKey,
            gate,
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token);
        gate.Release();
        Result<CorrectAllocationDisputeResponse> loser = await waiting.WaitAsync(timeout.Token);

        Assert.True(loser.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionAlreadyCorrected.Code,
            loser.Error.Code);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    /// <summary>
    /// Classification test. The unique exception is a stand-in, not a SQL Server collision.
    /// The reads after rollback still use SQL Server.
    /// </summary>
    [Fact]
    public async Task Recovery_sees_a_commit_between_its_own_key_reads()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("recovery-reversal-by-key");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    seeded.OrganizationId,
                    "actor-b",
                    new ThrowingUnitOfWork(),
                    gate);
                return await handler.Handle(
                    new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, seeded.Key),
                    timeout.Token);
            },
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token);
        gate.Release();
        Result<CorrectAllocationDisputeResponse> replay = await waiting.WaitAsync(timeout.Token);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.Replayed);
        Assert.Equal("actor-a", replay.Value.ActorId);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    /// <summary>
    /// Classification test. The unique exception is a stand-in, not a SQL Server collision.
    /// </summary>
    [Fact]
    public async Task Recovery_sees_another_correction_key_as_already_corrected()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("recovery-reversal-by-key");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    seeded.OrganizationId,
                    "actor-b",
                    new ThrowingUnitOfWork(),
                    gate);
                return await handler.Handle(
                    new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, Guid.NewGuid()),
                    timeout.Token);
            },
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token);
        gate.Release();
        Result<CorrectAllocationDisputeResponse> loser = await waiting.WaitAsync(timeout.Token);

        Assert.True(loser.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionAlreadyCorrected.Code,
            loser.Error.Code);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    /// <summary>
    /// Classification test. The unique exception is a stand-in, not a SQL Server collision.
    /// </summary>
    [Fact]
    public async Task Recovery_sees_a_plain_reversal_before_the_status_read()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("recovery-correction-by-dispute");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<CorrectAllocationDisputeResponse>> waiting = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    seeded.OrganizationId,
                    "actor-b",
                    new ThrowingUnitOfWork(),
                    gate);
                return await handler.Handle(
                    new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, seeded.Key),
                    timeout.Token);
            },
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ReverseAllocationHandler reversal = new(
                new TestCurrentOrganizationContext(seeded.OrganizationId),
                new TestCurrentActorContext("actor-a"),
                scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
                scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
                scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
                scope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
                scope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
                scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                new FixedTimeProvider(CorrectedAt));
            Result<ReverseAllocationResponse> reversed = await reversal.Handle(
                new ReverseAllocationCommand(seeded.AllocationId, Reason, Guid.NewGuid()),
                timeout.Token);
            Assert.True(reversed.IsSuccess);
            Assert.False(reversed.Value!.Replayed);
        }

        gate.Release();
        Result<CorrectAllocationDisputeResponse> loser = await waiting.WaitAsync(timeout.Token);

        Assert.True(loser.IsFailure);
        Assert.Equal(ReconciliationErrors.AllocationAlreadyReversed.Code, loser.Error.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == seeded.OrganizationId));
        Assert.Equal(1, await ReversalAuditCountAsync(context, seeded));
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        Assert.Equal(0m, invoice.AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Same_key_on_a_different_allocation_is_a_payload_conflict()
    {
        Seeded first = await SeedAsync();
        Seeded second = await SeedInOrganizationAsync(first.OrganizationId);
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ReverseAllocationHandler reversal = new(
                new TestCurrentOrganizationContext(first.OrganizationId),
                new TestCurrentActorContext("actor-a"),
                scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
                scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
                scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
                scope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
                scope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
                scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                new FixedTimeProvider(CorrectedAt));
            Result<ReverseAllocationResponse> reversed = await reversal.Handle(
                new ReverseAllocationCommand(first.AllocationId, Reason, first.Key),
                CancellationToken.None);
            Assert.True(reversed.IsSuccess);
            Assert.False(reversed.Value!.Replayed);
        }

        Result<CorrectAllocationDisputeResponse> correction = await CorrectAsync(
            provider,
            second,
            "actor-b",
            first.Key,
            CancellationToken.None);

        Assert.True(correction.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyPayloadConflict.Code,
            correction.Error.Code);
        await AssertUnchangedAsync(second);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == first.OrganizationId));
        Assert.Equal(1, await ReversalAuditCountAsync(context, first));
    }

    [Fact]
    public async Task Changed_dispute_allocation_rolls_back_the_named_foreign_key()
    {
        Seeded seeded = await SeedAsync();
        Seeded replacement = await SeedInOrganizationAsync(seeded.OrganizationId);
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using StepGate gate = new("locked-allocation-status");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<Result<CorrectAllocationDisputeResponse>> waiting = StartCorrectionAsync(
            provider,
            seeded,
            "actor-a",
            seeded.Key,
            gate,
            timeout.Token);
        await gate.Arrived.Task.WaitAsync(timeout.Token);
        await using (SqlConnection connection = new(_fixture.ConnectionString))
        {
            await connection.OpenAsync(timeout.Token);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE [reconciliation].[AllocationDisputes]
                SET [AllocationId] = @allocationId
                WHERE [OrganizationId] = @organizationId AND [Id] = @disputeId
                """;
            command.Parameters.Add(new SqlParameter("@allocationId", replacement.AllocationId.Value));
            command.Parameters.Add(new SqlParameter("@organizationId", seeded.OrganizationId.Value));
            command.Parameters.Add(new SqlParameter("@disputeId", seeded.DisputeId.Value));
            Assert.Equal(1, await command.ExecuteNonQueryAsync(timeout.Token));
        }

        gate.Release();
        Result<CorrectAllocationDisputeResponse> result = await waiting.WaitAsync(timeout.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionConcurrencyConflict.Code,
            result.Error.Code);
        await AssertUnchangedAsync(seeded);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == seeded.OrganizationId));
    }

    [Fact]
    public async Task Sql_rejects_correction_update_and_delete()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        Result<CorrectAllocationDisputeResponse> created = await CorrectAsync(
            provider,
            seeded,
            "actor-a",
            seeded.Key,
            CancellationToken.None);
        Assert.True(created.IsSuccess);

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        SqlException updated = await RejectAsync(
            connection,
            """
            UPDATE [reconciliation].[AllocationDisputeCorrections]
            SET [Reason] = N'changed'
            WHERE [OrganizationId] = @organizationId AND [AllocationDisputeId] = @disputeId
            """,
            seeded);
        SqlException deleted = await RejectAsync(
            connection,
            """
            DELETE FROM [reconciliation].[AllocationDisputeCorrections]
            WHERE [OrganizationId] = @organizationId AND [AllocationDisputeId] = @disputeId
            """,
            seeded);
        Assert.Equal(AllocationDisputeCorrectionConfiguration.ImmutabilityErrorNumber, updated.Number);
        Assert.Equal(AllocationDisputeCorrectionConfiguration.ImmutabilityErrorNumber, deleted.Number);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    [Fact]
    public async Task SaveChanges_leaves_the_explicit_transaction_open_until_owner_rollback()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CountingSignals signals = new();
        ProbeUnitOfWork probe = new(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            _fixture.ConnectionString,
            seeded.OrganizationId,
            seeded.AllocationId);
        CorrectAllocationDisputeHandler handler = CreateHandler(
            scope.ServiceProvider,
            seeded.OrganizationId,
            "actor-a",
            probe,
            signals);

        UniqueConstraintConflictException exception = await Assert.ThrowsAsync<UniqueConstraintConflictException>(
            () => handler.Handle(
                new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, seeded.Key),
                CancellationToken.None));

        Assert.True(probe.SawLockTimeout);
        Assert.Contains("unrecognized unique conflict", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, signals.CountPrefix("recovery-"));
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task Failed_rollback_does_not_start_recovery()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        CountingSignals signals = new();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IAllocationDisputeCorrectionWork inner = scope.ServiceProvider.GetRequiredService<IAllocationDisputeCorrectionWork>();
        CorrectAllocationDisputeHandler handler = CreateHandler(
            scope.ServiceProvider,
            seeded.OrganizationId,
            "actor-a",
            new ThrowingUnitOfWork(),
            signals,
            new RollbackFailsWork(inner));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, seeded.Key),
            CancellationToken.None));

        Assert.Equal("rollback failed", exception.Message);
        Assert.Equal(0, signals.CountPrefix("recovery-"));
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task Two_corrections_of_one_dispute_leave_one_financial_effect()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Result<CorrectAllocationDisputeResponse>[] results = await Task.WhenAll(
            CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token),
            CorrectAsync(provider, seeded, "actor-b", Guid.NewGuid(), timeout.Token));

        Result<CorrectAllocationDisputeResponse> winner = Assert.Single(
            results,
            result => result.IsSuccess && result.Value.Replayed == false);
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error.Code == ReconciliationApplicationErrors.AllocationDisputeCorrectionAlreadyCorrected.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        AllocationDisputeCorrection correction = Assert.Single(await context.AllocationDisputeCorrections
            .Where(row => row.OrganizationId == seeded.OrganizationId)
            .ToListAsync());
        Assert.Equal(winner.Value!.ActorId, correction.ActorId);
        Assert.Equal(1, await ReversalAuditCountAsync(context, seeded));
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        Assert.Equal(0m, invoice.AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Two_disputes_of_one_allocation_reverse_the_amount_once()
    {
        Seeded seeded = await SeedAsync();
        AllocationDisputeId secondDispute = await SeedSecondDisputeAsync(seeded);
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Result<CorrectAllocationDisputeResponse>[] results = await Task.WhenAll(
            CorrectAsync(provider, seeded, "actor-a", seeded.Key, timeout.Token, seeded.DisputeId),
            CorrectAsync(provider, seeded, "actor-b", Guid.NewGuid(), timeout.Token, secondDispute));

        Assert.Single(results, result => result.IsSuccess && result.Value.Replayed == false);
        Assert.Single(
            results,
            result => result.IsFailure && result.Error.Code == ReconciliationErrors.AllocationAlreadyReversed.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(1, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == seeded.OrganizationId));
        Assert.Equal(1, await ReversalAuditCountAsync(context, seeded));
        Allocation stored = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
        Assert.Equal(AllocationStatus.Reversed, stored.Status);
    }

    [Fact]
    public async Task Ordinary_reversal_after_correction_replays_the_financial_key()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        Result<CorrectAllocationDisputeResponse> correction = await CorrectAsync(
            provider,
            seeded,
            "actor-a",
            seeded.Key,
            CancellationToken.None);
        Assert.False(correction.Value!.Replayed);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ReverseAllocationHandler reversal = new(
            new TestCurrentOrganizationContext(seeded.OrganizationId),
            new TestCurrentActorContext("actor-b"),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
            scope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
            scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(CorrectedAt.AddHours(1)));
        Result<ReverseAllocationResponse> replay = await reversal.Handle(
            new ReverseAllocationCommand(seeded.AllocationId, Reason, seeded.Key),
            CancellationToken.None);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(correction.Value!.ReversedAt, replay.Value!.ReversedAt);
        await AssertSingleEffectAsync(seeded, "actor-a");
    }

    [Fact]
    public async Task Same_key_used_by_an_earlier_reversal_does_not_attach_the_dispute()
    {
        Seeded seeded = await SeedAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ReverseAllocationHandler reversal = new(
            new TestCurrentOrganizationContext(seeded.OrganizationId),
            new TestCurrentActorContext("actor-a"),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
            scope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
            scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(CorrectedAt));
        Result<ReverseAllocationResponse> reversed = await reversal.Handle(
            new ReverseAllocationCommand(seeded.AllocationId, Reason, seeded.Key),
            CancellationToken.None);
        Assert.True(reversed.IsSuccess);
        Assert.False(reversed.Value!.Replayed);

        Result<CorrectAllocationDisputeResponse> correction = await CorrectAsync(
            provider,
            seeded,
            "actor-b",
            seeded.Key,
            CancellationToken.None);

        Assert.True(correction.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyPayloadConflict.Code,
            correction.Error.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == seeded.OrganizationId));
        Assert.Equal(1, await ReversalAuditCountAsync(context, seeded));
    }

    [Fact]
    public async Task Mismatched_audit_actor_is_not_a_successful_replay()
    {
        Seeded seeded = await SeedAsync();
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
            Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
            BankTransaction bank = await context.BankTransactions.SingleAsync(row => row.Id == seeded.BankId);
            Assert.True(AllocationReversal.Apply(allocation, invoice, bank, Reason, CorrectedAt).IsSuccess);
            AllocationReversalIdempotency.Assign(context, allocation, seeded.Key);
            new FinancialAuditWriter(context).AppendReversal(allocation, "audit-actor", seeded.Key);
            context.AllocationDisputeCorrections.Add(AllocationDisputeCorrection.Create(
                seeded.OrganizationId,
                seeded.DisputeId,
                seeded.AllocationId,
                Reason,
                ReviewCaseActorSource.DevelopmentJwt,
                "link-actor",
                CorrectedAt,
                seeded.Key));
            await context.SaveChangesAsync();
        }

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        Result<CorrectAllocationDisputeResponse> replay = await CorrectAsync(
            provider,
            seeded,
            "actor-b",
            seeded.Key,
            CancellationToken.None);

        Assert.True(replay.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict.Code,
            replay.Error.Code);
        await using ReconciliationDbContext check = _fixture.CreateContext();
        FinancialAuditEntry audit = Assert.Single(await check.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == seeded.OrganizationId
                && entry.AllocationId == seeded.AllocationId
                && entry.Action == FinancialAuditAction.Reversal)
            .ToListAsync());
        Assert.Equal("audit-actor", audit.ActorId);
        Assert.Equal("link-actor", (await check.AllocationDisputeCorrections.SingleAsync(
            row => row.OrganizationId == seeded.OrganizationId)).ActorId);
        Invoice storedInvoice = await check.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        Assert.Equal(0m, storedInvoice.AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Correction_and_reversal_overlap_until_one_command_reverses()
    {
        Seeded seeded = await SeedAsync();
        Guid reversalKey = Guid.NewGuid();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using AsyncServiceScope reversalScope = provider.CreateAsyncScope();
        PausingUnitOfWork reversalSave = new(reversalScope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        ReverseAllocationHandler reversal = new(
            new TestCurrentOrganizationContext(seeded.OrganizationId),
            new TestCurrentActorContext("actor-b"),
            reversalScope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            reversalScope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            reversalScope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            reversalScope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
            reversalScope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
            reversalScope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            reversalSave,
            new FixedTimeProvider(CorrectedAt));
        Task<Result<ReverseAllocationResponse>> reversalTask = reversal.Handle(
            new ReverseAllocationCommand(seeded.AllocationId, "different reason", reversalKey),
            timeout.Token);
        await reversalSave.Entered.Task.WaitAsync(timeout.Token);

        Result<CorrectAllocationDisputeResponse> correction = await CorrectAsync(
            provider,
            seeded,
            "actor-a",
            seeded.Key,
            timeout.Token);
        reversalSave.Release();
        Result<ReverseAllocationResponse> reversed = await reversalTask.WaitAsync(timeout.Token);

        Assert.True(correction.IsSuccess);
        Assert.False(correction.Value!.Replayed);
        Assert.Equal("actor-a", correction.Value.ActorId);
        Assert.True(reversed.IsFailure);
        Assert.Equal(ReconciliationErrors.AllocationAlreadyReversed.Code, reversed.Error.Code);
        await AssertSingleEffectAsync(seeded, "actor-a");
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.NotEqual(
            reversalKey,
            context.Entry(await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId))
                .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
                .CurrentValue);
    }

    [Fact]
    public async Task Same_key_on_another_allocation_collides_in_production_save()
    {
        Seeded winner = await SeedAsync();
        Seeded loser = await SeedInOrganizationAsync(winner.OrganizationId);
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        CollisionObservation observation = new(_fixture.ConnectionString, loser);
        Task<Result<CorrectAllocationDisputeResponse>> waiting = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ObservingUnitOfWork unitOfWork = new(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), observation);
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    loser.OrganizationId,
                    "actor-b",
                    unitOfWork,
                    observation);
                return await handler.Handle(
                    new CorrectAllocationDisputeCommand(loser.DisputeId, Reason, winner.Key),
                    timeout.Token);
            },
            timeout.Token);
        await observation.LockedAbsence.Task.WaitAsync(timeout.Token);
        Result<CorrectAllocationDisputeResponse> committed = await CorrectAsync(
            provider,
            winner,
            "actor-a",
            winner.Key,
            timeout.Token);
        Assert.True(committed.IsSuccess);
        observation.Release();
        Result<CorrectAllocationDisputeResponse> collided = await waiting.WaitAsync(timeout.Token);

        Assert.True(collided.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyPayloadConflict.Code,
            collided.Error.Code);
        Assert.True(observation.RecoveryStartedAfterRollback);
        Assert.True(observation.LockWasFreeAtRecovery);
        SqlException sql = AssertSqlUnique(observation.Captured);
        _output.WriteLine(
            "same-key collision sql=" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " constraint=" + observation.ConstraintName);
        Assert.True(sql.Number is 2601 or 2627);
        Assert.True(
            observation.ConstraintName == AllocationConfiguration.ReversalIdempotencyIndexName
            || observation.ConstraintName == AllocationDisputeCorrectionConfiguration.IdempotencyKeyIndexName
            || observation.ConstraintName == AllocationDisputeCorrectionConfiguration.PrimaryKeyName,
            observation.ConstraintName);
        await AssertSingleEffectAsync(winner, "actor-a");
        await AssertAllocationStillConfirmedAsync(loser);
    }

    [Fact]
    public async Task Shared_invoice_rowversion_returns_concurrency_conflict()
    {
        (Seeded loser, Seeded winner) = await SeedSharingInvoiceAsync();
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<Result<CorrectAllocationDisputeResponse>> waiting = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                PausingInvoiceRepository pause = new(
                    scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
                    loser.InvoiceId);
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    loser.OrganizationId,
                    "actor-b",
                    scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    new NoAllocationDisputeCorrectionReadSignals(),
                    invoiceRepository: pause);
                Task<Result<CorrectAllocationDisputeResponse>> handle = handler.Handle(
                    new CorrectAllocationDisputeCommand(loser.DisputeId, Reason, loser.Key),
                    timeout.Token);
                await pause.BalanceRead.Task.WaitAsync(timeout.Token);
                await using AsyncServiceScope reversalScope = provider.CreateAsyncScope();
                ReverseAllocationHandler reversal = new(
                    new TestCurrentOrganizationContext(winner.OrganizationId),
                    new TestCurrentActorContext("actor-a"),
                    reversalScope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
                    reversalScope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
                    reversalScope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
                    reversalScope.ServiceProvider.GetRequiredService<IAllocationReversalLookup>(),
                    reversalScope.ServiceProvider.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
                    reversalScope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
                    reversalScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    new FixedTimeProvider(CorrectedAt));
                Result<ReverseAllocationResponse> reversed = await reversal.Handle(
                    new ReverseAllocationCommand(winner.AllocationId, Reason, winner.Key),
                    timeout.Token);
                Assert.True(reversed.IsSuccess);
                Assert.False(reversed.Value!.Replayed);
                pause.Release();
                return await handle.WaitAsync(timeout.Token);
            },
            timeout.Token);
        Result<CorrectAllocationDisputeResponse> result = await waiting.WaitAsync(timeout.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, result.Error.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation losingAllocation = await context.Allocations.SingleAsync(row => row.Id == loser.AllocationId);
        Allocation winningAllocation = await context.Allocations.SingleAsync(row => row.Id == winner.AllocationId);
        Assert.Equal(AllocationStatus.Confirmed, losingAllocation.Status);
        Assert.Null(context.Entry(losingAllocation).Property<Guid?>(
            AllocationConfiguration.ReversalIdempotencyKeyPropertyName).CurrentValue);
        Assert.Equal(AllocationStatus.Reversed, winningAllocation.Status);
        Assert.Equal(winner.Key, context.Entry(winningAllocation).Property<Guid?>(
            AllocationConfiguration.ReversalIdempotencyKeyPropertyName).CurrentValue);
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == loser.InvoiceId);
        BankTransaction losingBank = await context.BankTransactions.SingleAsync(row => row.Id == loser.BankId);
        BankTransaction winningBank = await context.BankTransactions.SingleAsync(row => row.Id == winner.BankId);
        Assert.Equal(125.50m, invoice.AllocatedAmount.Amount);
        Assert.Equal(125.50m, losingBank.AllocatedAmount.Amount);
        Assert.Equal(0m, winningBank.AllocatedAmount.Amount);
        Assert.Equal(0, await ReversalAuditCountAsync(context, loser));
        Assert.Equal(1, await ReversalAuditCountAsync(context, winner));
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.AllocationDisputeId == loser.DisputeId));
    }

    [Fact]
    public async Task Unrecognized_unique_conflict_rolls_back_without_recovery()
    {
        Seeded seeded = await SeedAsync();
        await PlantForeignReversalAuditAsync(seeded);
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        CountingSignals signals = new();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CorrectAllocationDisputeHandler handler = CreateHandler(
            scope.ServiceProvider,
            seeded.OrganizationId,
            "actor-a",
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            signals);

        UniqueConstraintConflictException exception = await Assert.ThrowsAsync<UniqueConstraintConflictException>(
            () => handler.Handle(
                new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, seeded.Key),
                CancellationToken.None));

        SqlException sql = FindSqlException(exception);
        _output.WriteLine(
            "foreign unique sql=" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " message=" + sql.Message);
        Assert.True(sql.Number is 2601 or 2627);
        Assert.Contains(
            FinancialAuditEntryConfiguration.UniqueIndexName,
            sql.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(AllocationConfiguration.ReversalIdempotencyIndexName, sql.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            AllocationDisputeCorrectionConfiguration.IdempotencyKeyIndexName,
            sql.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            AllocationDisputeCorrectionConfiguration.PrimaryKeyName,
            sql.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, signals.CountPrefix("recovery-"));
        await AssertAllocationStillConfirmedAsync(seeded);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry planted = Assert.Single(await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == seeded.OrganizationId
                && entry.AllocationId == seeded.AllocationId
                && entry.Action == FinancialAuditAction.Reversal)
            .ToListAsync());
        Assert.Equal("planted-actor", planted.ActorId);
        Assert.False(await AllocationLockIsHeldAsync(seeded));
    }

    private Task<Seeded> SeedAsync()
    {
        return SeedInOrganizationAsync(OrganizationId.New());
    }

    private async Task<Seeded> SeedInOrganizationAsync(OrganizationId organizationId)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = new(
            InvoiceId.New(),
            organizationId,
            "INV-" + suffix,
            new Money(125.50m, PersistenceTestData.Pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(125.50m, PersistenceTestData.Pln),
            new DateOnly(2026, 11, 8),
            "INV-" + suffix);
        Assert.True(AllocationConfirmation.Apply(invoice, bank, new Money(125.50m, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(125.50m, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            organizationId,
            allocation.Id,
            "The counterparty challenges this allocation.",
            ReviewCaseActorSource.DevelopmentJwt,
            "registrar",
            ConfirmedAt,
            Guid.NewGuid());
        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            organizationId,
            dispute.Id,
            AllocationDisputeResolutionOutcome.Upheld,
            "The challenge stands.",
            ReviewCaseActorSource.DevelopmentJwt,
            "judge",
            ConfirmedAt.AddHours(1),
            Guid.NewGuid());
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank, allocation, dispute, resolution);
        new FinancialAuditWriter(context).AppendConfirmation(allocation, "confirm-operator", batchExecutionId: null);
        await context.SaveChangesAsync();
        return new Seeded(organizationId, allocation.Id, invoice.Id, bank.Id, dispute.Id, Guid.NewGuid());
    }

    private async Task<(Seeded Loser, Seeded Winner)> SeedSharingInvoiceAsync()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = new(
            InvoiceId.New(),
            organizationId,
            "INV-" + Guid.NewGuid().ToString("N")[..8],
            new Money(251.00m, PersistenceTestData.Pln));
        BankTransaction losingBank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(125.50m, PersistenceTestData.Pln),
            new DateOnly(2026, 11, 8),
            "BANK-LOSER");
        BankTransaction winningBank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(125.50m, PersistenceTestData.Pln),
            new DateOnly(2026, 11, 8),
            "BANK-WINNER");
        Money amount = new(125.50m, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(invoice, losingBank, amount).IsSuccess);
        Assert.True(AllocationConfirmation.Apply(invoice, winningBank, amount).IsSuccess);
        Allocation losingAllocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            losingBank.Id,
            amount,
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        Allocation winningAllocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            winningBank.Id,
            amount,
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        AllocationDispute losingDispute = CreateUpheldDispute(organizationId, losingAllocation.Id);
        AllocationDispute winningDispute = CreateUpheldDispute(organizationId, winningAllocation.Id);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(
            invoice,
            losingBank,
            winningBank,
            losingAllocation,
            winningAllocation,
            losingDispute,
            winningDispute);
        context.Add(ResolutionFor(organizationId, losingDispute.Id));
        context.Add(ResolutionFor(organizationId, winningDispute.Id));
        FinancialAuditWriter audit = new(context);
        audit.AppendConfirmation(losingAllocation, "confirm-operator", batchExecutionId: null);
        audit.AppendConfirmation(winningAllocation, "confirm-operator", batchExecutionId: null);
        await context.SaveChangesAsync();
        return (
            new Seeded(organizationId, losingAllocation.Id, invoice.Id, losingBank.Id, losingDispute.Id, Guid.NewGuid()),
            new Seeded(organizationId, winningAllocation.Id, invoice.Id, winningBank.Id, winningDispute.Id, Guid.NewGuid()));
    }

    private static AllocationDispute CreateUpheldDispute(OrganizationId organizationId, AllocationId allocationId)
    {
        return AllocationDispute.Create(
            AllocationDisputeId.New(),
            organizationId,
            allocationId,
            "The counterparty challenges this allocation.",
            ReviewCaseActorSource.DevelopmentJwt,
            "registrar",
            ConfirmedAt,
            Guid.NewGuid());
    }

    private static AllocationDisputeResolution ResolutionFor(
        OrganizationId organizationId,
        AllocationDisputeId disputeId)
    {
        return AllocationDisputeResolution.Create(
            organizationId,
            disputeId,
            AllocationDisputeResolutionOutcome.Upheld,
            "The challenge stands.",
            ReviewCaseActorSource.DevelopmentJwt,
            "judge",
            ConfirmedAt.AddHours(1),
            Guid.NewGuid());
    }

    private async Task PlantForeignReversalAuditAsync(Seeded seeded)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(row => row.Id == seeded.BankId);
        Assert.True(AllocationReversal.Apply(allocation, invoice, bank, Reason, CorrectedAt).IsSuccess);
        FinancialAuditEntry audit = FinancialAuditEntry.ForReversal(
            FinancialAuditEntryId.New(),
            allocation,
            "planted-actor",
            Guid.NewGuid());
        context.ChangeTracker.Clear();
        context.FinancialAuditEntries.Add(audit);
        await context.SaveChangesAsync();
    }

    private async Task<AllocationDisputeId> SeedSecondDisputeAsync(Seeded seeded)
    {
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            seeded.OrganizationId,
            seeded.AllocationId,
            "A second challenge.",
            ReviewCaseActorSource.DevelopmentJwt,
            "registrar",
            ConfirmedAt,
            Guid.NewGuid());
        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            seeded.OrganizationId,
            dispute.Id,
            AllocationDisputeResolutionOutcome.Upheld,
            "The second challenge stands.",
            ReviewCaseActorSource.DevelopmentJwt,
            "judge",
            ConfirmedAt.AddHours(1),
            Guid.NewGuid());
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(dispute, resolution);
        await context.SaveChangesAsync();
        return dispute.Id;
    }

    private Task<Result<CorrectAllocationDisputeResponse>> StartCorrectionAsync(
        ServiceProvider provider,
        Seeded seeded,
        string actorId,
        Guid key,
        IAllocationDisputeCorrectionReadSignals signals,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                CorrectAllocationDisputeHandler handler = CreateHandler(
                    scope.ServiceProvider,
                    seeded.OrganizationId,
                    actorId,
                    scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    signals);
                return await handler.Handle(
                    new CorrectAllocationDisputeCommand(seeded.DisputeId, Reason, key),
                    cancellationToken);
            },
            cancellationToken);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>> CorrectAsync(
        ServiceProvider provider,
        Seeded seeded,
        string actorId,
        Guid key,
        CancellationToken cancellationToken,
        AllocationDisputeId? disputeId = null)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CorrectAllocationDisputeHandler handler = CreateHandler(
            scope.ServiceProvider,
            seeded.OrganizationId,
            actorId,
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new NoAllocationDisputeCorrectionReadSignals());
        return await handler.Handle(
            new CorrectAllocationDisputeCommand(disputeId ?? seeded.DisputeId, Reason, key),
            cancellationToken);
    }

    private static CorrectAllocationDisputeHandler CreateHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string actorId,
        IUnitOfWork unitOfWork,
        IAllocationDisputeCorrectionReadSignals signals,
        IAllocationDisputeCorrectionWork? work = null,
        IInvoiceRepository? invoiceRepository = null)
    {
        return new CorrectAllocationDisputeHandler(
            new TestCurrentOrganizationContext(organizationId),
            new FixedAttribution(actorId),
            services.GetRequiredService<IAllocationDisputeCorrectionReads>(),
            work ?? services.GetRequiredService<IAllocationDisputeCorrectionWork>(),
            signals,
            services.GetRequiredService<IAllocationDisputeCorrectionConflict>(),
            services.GetRequiredService<IAllocationRepository>(),
            invoiceRepository ?? services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IFinancialAuditWriter>(),
            unitOfWork,
            new FixedTimeProvider(CorrectedAt));
    }

    private async Task AssertSingleEffectAsync(Seeded seeded, string actorId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(Reason, allocation.ReversalReason);
        Assert.Equal(CorrectedAt, allocation.ReversedAt);
        Assert.Equal(seeded.Key, context.Entry(allocation).Property<Guid?>(
            AllocationConfiguration.ReversalIdempotencyKeyPropertyName).CurrentValue);
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(row => row.Id == seeded.BankId);
        Assert.Equal(0m, invoice.AllocatedAmount.Amount);
        Assert.Equal(0m, bank.AllocatedAmount.Amount);
        FinancialAuditEntry audit = Assert.Single(
            await context.FinancialAuditEntries
                .Where(entry => entry.OrganizationId == seeded.OrganizationId
                    && entry.AllocationId == seeded.AllocationId
                    && entry.Action == FinancialAuditAction.Reversal)
                .ToListAsync());
        Assert.Equal(actorId, audit.ActorId);
        Assert.Equal(seeded.Key, audit.IdempotencyKey);
        AllocationDisputeCorrection correction = Assert.Single(
            await context.AllocationDisputeCorrections
                .Where(row => row.OrganizationId == seeded.OrganizationId)
                .ToListAsync());
        Assert.Equal(seeded.DisputeId, correction.AllocationDisputeId);
        Assert.Equal(actorId, correction.ActorId);
        Assert.Equal(seeded.Key, correction.IdempotencyKey);
    }

    private async Task AssertUnchangedAsync(Seeded seeded)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        Assert.Equal(125.50m, invoice.AllocatedAmount.Amount);
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == seeded.OrganizationId));
        Assert.Equal(0, await ReversalAuditCountAsync(context, seeded));
    }

    private async Task AssertAllocationStillConfirmedAsync(Seeded seeded)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(row => row.Id == seeded.AllocationId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Assert.Null(context.Entry(allocation).Property<Guid?>(
            AllocationConfiguration.ReversalIdempotencyKeyPropertyName).CurrentValue);
        Invoice invoice = await context.Invoices.SingleAsync(row => row.Id == seeded.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(row => row.Id == seeded.BankId);
        Assert.Equal(125.50m, invoice.AllocatedAmount.Amount);
        Assert.Equal(125.50m, bank.AllocatedAmount.Amount);
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.AllocationDisputeId == seeded.DisputeId));
    }

    private Task<bool> AllocationLockIsHeldAsync(Seeded seeded)
    {
        return AllocationLockIsHeldAsync(_fixture.ConnectionString, seeded);
    }

    private static async Task<bool> AllocationLockIsHeldAsync(string connectionString, Seeded seeded)
    {
        try
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SET LOCK_TIMEOUT 0;
                SELECT [Status]
                FROM [reconciliation].[Allocations] WITH (UPDLOCK, ROWLOCK)
                WHERE [OrganizationId] = @organizationId AND [Id] = @allocationId
                """;
            command.Parameters.Add(new SqlParameter("@organizationId", seeded.OrganizationId.Value));
            command.Parameters.Add(new SqlParameter("@allocationId", seeded.AllocationId.Value));
            await command.ExecuteScalarAsync();
            return false;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return true;
        }
    }

    private static SqlException FindSqlException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        throw new InvalidOperationException("The failure did not contain a SQL Server error.", exception);
    }

    private static class ClassificationSql
    {
        private static readonly SqlException PrimaryKey = Create(
            2601,
            "Cannot insert duplicate key row in object 'reconciliation.AllocationDisputeCorrections' with unique index '"
            + AllocationDisputeCorrectionConfiguration.PrimaryKeyName
            + "'.");

        public static SqlException RecognizedPrimaryKey()
        {
            return PrimaryKey;
        }

        private static SqlException Create(int number, string message)
        {
            Type errorType = typeof(SqlError);
            object error = Activator.CreateInstance(
                errorType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [number, (byte)1, (byte)14, "server", message, string.Empty, 1, 0, null],
                culture: null)
                ?? throw new InvalidOperationException("SQL error could not be constructed.");
            object collection = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)
                ?? throw new InvalidOperationException("SQL error collection could not be constructed.");
            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(collection, [error]);
            return (SqlException)typeof(SqlException).GetMethod(
                "CreateException",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(SqlErrorCollection), typeof(string)],
                modifiers: null)!
                .Invoke(null, [collection, "16.0.0"])!;
        }
    }

    private static SqlException AssertSqlUnique(Exception? exception)
    {
        Assert.NotNull(exception);
        return FindSqlException(exception);
    }

    private static async Task<SqlException> RejectAsync(SqlConnection connection, string sql, Seeded seeded)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@organizationId", seeded.OrganizationId.Value));
        command.Parameters.Add(new SqlParameter("@disputeId", seeded.DisputeId.Value));
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task<int> ReversalAuditCountAsync(ReconciliationDbContext context, Seeded seeded)
    {
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == seeded.OrganizationId
                && entry.AllocationId == seeded.AllocationId
                && entry.Action == FinancialAuditAction.Reversal);
    }

    private sealed record Seeded(
        OrganizationId OrganizationId,
        AllocationId AllocationId,
        InvoiceId InvoiceId,
        BankTransactionId BankId,
        AllocationDisputeId DisputeId,
        Guid Key);

    private sealed class FixedAttribution : ICurrentActorAttribution
    {
        private readonly string _actorId;

        public FixedAttribution(string actorId)
        {
            _actorId = actorId;
        }

        public bool TryGet(out ReviewCaseActorSource source, out string actorId)
        {
            source = ReviewCaseActorSource.DevelopmentJwt;
            actorId = _actorId;
            return true;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// Test barrier between handler reads. It does not commit, roll back, or classify.
    /// </summary>
    private sealed class StepGate : IAllocationDisputeCorrectionReadSignals, IDisposable
    {
        private readonly string _step;
        private int _seen;

        public StepGate(string step)
        {
            _step = step;
        }

        public TaskCompletionSource<bool> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnReadAsync(string step, CancellationToken cancellationToken)
        {
            if (!string.Equals(step, _step, StringComparison.Ordinal) || Interlocked.Increment(ref _seen) != 1)
            {
                return Task.CompletedTask;
            }

            Arrived.TrySetResult(true);
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public void Dispose()
        {
            Release();
        }
    }

    private sealed class CountingSignals : IAllocationDisputeCorrectionReadSignals
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public int Count(string step)
        {
            lock (_counts)
            {
                return _counts.TryGetValue(step, out int count) ? count : 0;
            }
        }

        public int CountPrefix(string prefix)
        {
            lock (_counts)
            {
                int total = 0;
                foreach (KeyValuePair<string, int> entry in _counts)
                {
                    if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        total += entry.Value;
                    }
                }

                return total;
            }
        }

        public Task OnReadAsync(string step, CancellationToken cancellationToken)
        {
            lock (_counts)
            {
                _counts[step] = Count(step) + 1;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Enters classification after rollback. The exception is a fabricated SQL 2601 that names
    /// <c>PK_AllocationDisputeCorrections</c>, so the production recognizer accepts it.
    /// It is not the result of this command's <c>SaveChangesAsync</c>.
    /// </summary>
    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            throw new UniqueConstraintConflictException(
                "Classification stand-in used only to enter recovery after the explicit transaction can be ended.",
                ClassificationSql.RecognizedPrimaryKey());
        }
    }

    /// <summary>
    /// Calls the real save, proves the allocation lock is still held, then throws an unrecognized unique conflict.
    /// The lock probe is SQL Server. The thrown exception is not a named correction collision.
    /// </summary>
    private sealed class ProbeUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly string _connectionString;
        private readonly OrganizationId _organizationId;
        private readonly AllocationId _allocationId;

        public ProbeUnitOfWork(
            IUnitOfWork inner,
            string connectionString,
            OrganizationId organizationId,
            AllocationId allocationId)
        {
            _inner = inner;
            _connectionString = connectionString;
            _organizationId = organizationId;
            _allocationId = allocationId;
        }

        public bool SawLockTimeout { get; private set; }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            await _inner.SaveChangesAsync(cancellationToken);

            SawLockTimeout = await LockIsHeldAsync(cancellationToken);
            throw new UniqueConstraintConflictException(
                "Test stand-in after the real save. This unrecognized unique conflict must not enter recovery.");
        }

        private async Task<bool> LockIsHeldAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using SqlConnection connection = new(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await using SqlCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    SET LOCK_TIMEOUT 0;
                    SELECT [Status]
                    FROM [reconciliation].[Allocations] WITH (UPDLOCK, ROWLOCK)
                    WHERE [OrganizationId] = @organizationId AND [Id] = @allocationId
                    """;
                command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier)
                {
                    Value = _organizationId.Value,
                });
                command.Parameters.Add(new SqlParameter("@allocationId", SqlDbType.UniqueIdentifier)
                {
                    Value = _allocationId.Value,
                });
                await command.ExecuteScalarAsync(cancellationToken);
                return false;
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                return true;
            }
        }
    }

    private sealed class RollbackFailsWork : IAllocationDisputeCorrectionWork
    {
        private readonly IAllocationDisputeCorrectionWork _inner;

        public RollbackFailsWork(IAllocationDisputeCorrectionWork inner)
        {
            _inner = inner;
        }

        public async Task<IAllocationDisputeCorrectionSession> BeginAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            IAllocationDisputeCorrectionSession session = await _inner.BeginAsync(
                organizationId,
                allocationId,
                cancellationToken);
            return new Session(session);
        }

        private sealed class Session : IAllocationDisputeCorrectionSession
        {
            private readonly IAllocationDisputeCorrectionSession _inner;

            public Session(IAllocationDisputeCorrectionSession inner)
            {
                _inner = inner;
            }

            public bool AllocationFound => _inner.AllocationFound;

            public void Add(AllocationDisputeCorrection correction)
            {
                _inner.Add(correction);
            }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                return _inner.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("rollback failed");
            }

            public ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }

            public Task<AllocationDisputeCorrectionLink?> FindCorrectionByKeyAsync(
                OrganizationId organizationId,
                Guid idempotencyKey,
                CancellationToken cancellationToken)
            {
                return _inner.FindCorrectionByKeyAsync(organizationId, idempotencyKey, cancellationToken);
            }

            public Task<AllocationReversalFacts?> FindReversalByKeyAsync(
                OrganizationId organizationId,
                Guid idempotencyKey,
                CancellationToken cancellationToken)
            {
                return _inner.FindReversalByKeyAsync(organizationId, idempotencyKey, cancellationToken);
            }

            public Task<AllocationDisputeCorrectionLink?> FindCorrectionByDisputeAsync(
                OrganizationId organizationId,
                AllocationDisputeId disputeId,
                CancellationToken cancellationToken)
            {
                return _inner.FindCorrectionByDisputeAsync(organizationId, disputeId, cancellationToken);
            }

            public Task<AllocationReversalFacts?> FindAllocationAsync(
                OrganizationId organizationId,
                AllocationId allocationId,
                CancellationToken cancellationToken)
            {
                return _inner.FindAllocationAsync(organizationId, allocationId, cancellationToken);
            }

            public Task<IReadOnlyList<AllocationReversalAuditFact>> ListReversalAuditsAsync(
                OrganizationId organizationId,
                AllocationId allocationId,
                CancellationToken cancellationToken)
            {
                return _inner.ListReversalAuditsAsync(organizationId, allocationId, cancellationToken);
            }

            public Task<AllocationDisputeTarget?> FindDisputeAsync(
                OrganizationId organizationId,
                AllocationDisputeId disputeId,
                CancellationToken cancellationToken)
            {
                return _inner.FindDisputeAsync(organizationId, disputeId, cancellationToken);
            }

            public Task<AllocationDisputeResolutionOutcome?> FindResolutionOutcomeAsync(
                OrganizationId organizationId,
                AllocationDisputeId disputeId,
                CancellationToken cancellationToken)
            {
                return _inner.FindResolutionOutcomeAsync(organizationId, disputeId, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Pauses inside the reversal command's production save. It does not classify or commit.
    /// </summary>
    private sealed class PausingUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;

        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PausingUnitOfWork(IUnitOfWork inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            await _inner.SaveChangesAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    /// <summary>
    /// Records the production unique failure and rethrows that same exception.
    /// </summary>
    private sealed class ObservingUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;

        private readonly CollisionObservation _observation;

        public ObservingUnitOfWork(IUnitOfWork inner, CollisionObservation observation)
        {
            _inner = inner;
            _observation = observation;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _inner.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintConflictException exception)
            {
                _observation.Capture(exception);
                throw;
            }
        }
    }

    /// <summary>
    /// Test barrier around the locked absence read and the first recovery read.
    /// It does not commit, roll back, or classify.
    /// </summary>
    private sealed class CollisionObservation : IAllocationDisputeCorrectionReadSignals
    {
        private static readonly string[] RecognizedConstraints =
        [
            AllocationConfiguration.ReversalIdempotencyIndexName,
            AllocationDisputeCorrectionConfiguration.IdempotencyKeyIndexName,
            AllocationDisputeCorrectionConfiguration.PrimaryKeyName,
        ];

        private readonly string _connectionString;

        private readonly Seeded _loser;

        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _lockedSeen;

        public CollisionObservation(string connectionString, Seeded loser)
        {
            _connectionString = connectionString;
            _loser = loser;
        }

        public TaskCompletionSource<bool> LockedAbsence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Captured { get; private set; }

        public string ConstraintName { get; private set; } = string.Empty;

        public bool RecoveryStartedAfterRollback { get; private set; }

        public bool LockWasFreeAtRecovery { get; private set; }

        public void Capture(Exception exception)
        {
            Captured = exception;
            SqlException? sql = null;
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SqlException sqlException)
                {
                    sql = sqlException;
                    break;
                }
            }

            if (sql is null)
            {
                return;
            }

            foreach (string name in RecognizedConstraints)
            {
                if (sql.Message.Contains(name, StringComparison.Ordinal))
                {
                    ConstraintName = name;
                    return;
                }
            }

            ConstraintName = sql.Message;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public async Task OnReadAsync(string step, CancellationToken cancellationToken)
        {
            if (string.Equals(step, "locked-allocation-status", StringComparison.Ordinal)
                && Interlocked.Increment(ref _lockedSeen) == 1)
            {
                LockedAbsence.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
                return;
            }

            if (string.Equals(step, "recovery-correction-by-key", StringComparison.Ordinal))
            {
                RecoveryStartedAfterRollback = true;
                LockWasFreeAtRecovery = !await AllocationLockIsHeldAsync(_connectionString, _loser);
            }
        }
    }

    /// <summary>
    /// Returns the loaded invoice, then waits. The rowversion is already in the tracked entity.
    /// </summary>
    private sealed class PausingInvoiceRepository : IInvoiceRepository
    {
        private readonly IInvoiceRepository _inner;

        private readonly InvoiceId _invoiceId;

        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PausingInvoiceRepository(IInvoiceRepository inner, InvoiceId invoiceId)
        {
            _inner = inner;
            _invoiceId = invoiceId;
        }

        public TaskCompletionSource<bool> BalanceRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Invoice?> GetByIdAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            CancellationToken cancellationToken)
        {
            Invoice? invoice = await _inner.GetByIdAsync(organizationId, invoiceId, cancellationToken);
            if (invoiceId == _invoiceId)
            {
                BalanceRead.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }

            return invoice;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }
}