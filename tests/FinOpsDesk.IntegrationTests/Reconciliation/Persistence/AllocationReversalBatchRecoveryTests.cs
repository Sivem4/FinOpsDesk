using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalBatchRecoveryTests
{
    private const string BatchActor = "batch-operator";

    private const string ReversalActor = "reversal-operator";

    private const string ResumeActor = "resume-operator";

    private const string LaterActor = "later-operator";

    private const string ReversalReason = "customer refund";

    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ReversedAt = CreatedAt.AddMinutes(5);

    private static readonly DateTimeOffset ResumedAt = CreatedAt.AddMinutes(10);

    private readonly SqlServerFixture _fixture;

    public AllocationReversalBatchRecoveryTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Running_receipt_recovers_a_reversed_item_as_historical_confirmation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid batchKey = Guid.NewGuid();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "FULL", 18m);
        BatchExactMatchPair pair = Pair(invoice, bank);
        BatchExecution receipt = await SaveRunningAsync(organizationId, batchKey, pair);
        AllocationId allocationId = await SaveCommittedItemAsync(
            organizationId,
            receipt.Id,
            batchKey,
            invoice,
            bank,
            18m,
            BatchActor,
            writeConfirmationAudit: true);
        await ReverseSuccessAsync(organizationId, allocationId);
        ActivityLog activity = new();
        await using ServiceProvider provider = CreateBatchProvider(organizationId, ResumeActor, ResumedAt, activity);

        ExecuteBatchExactMatchesResponse recovered = await ExecuteSuccessAsync(provider, batchKey, pair);
        ExecuteBatchExactMatchesResponse replayed = await ExecuteSuccessAsync(provider, batchKey, pair);

        ExecuteBatchExactMatchItemResponse item = Assert.Single(recovered.Items);
        Assert.Equal("confirmed", item.Outcome);
        Assert.Equal(allocationId.Value, item.AllocationId);
        Assert.Equal(18m, item.Amount);
        Assert.Equal("PLN", item.Currency);
        Assert.True(recovered.AllConfirmed);
        Assert.Equal(1, recovered.ResultVersion);
        AssertSameResponse(recovered, replayed);
        Assert.Equal(0, activity.CandidateCalls);
        Assert.Empty(activity.ItemKeys);
        await AssertRecoveredReversedItemAsync(
            organizationId,
            batchKey,
            receipt.Id,
            allocationId,
            invoice.Id,
            bank.Id,
            18m,
            BatchActor,
            releasedAmount: 0m,
            confirmationAudits: 1);
    }

    [Fact]
    public async Task Reversed_recovered_item_is_not_executed_again_while_a_pending_item_confirms()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid batchKey = Guid.NewGuid();
        (Invoice invoiceA, BankTransaction bankA) = await SeedPairAsync(organizationId, "MIX-A", 18m);
        (Invoice invoiceB, BankTransaction bankB) = await SeedPairAsync(organizationId, "MIX-B", 27m);
        BatchExactMatchPair pairA = Pair(invoiceA, bankA);
        BatchExactMatchPair pairB = Pair(invoiceB, bankB);
        BatchExecution receipt = await SaveRunningAsync(organizationId, batchKey, pairA, pairB);
        AllocationId allocationA = await SaveCommittedItemAsync(
            organizationId,
            receipt.Id,
            batchKey,
            invoiceA,
            bankA,
            18m,
            BatchActor,
            writeConfirmationAudit: true);
        await ReverseSuccessAsync(organizationId, allocationA);
        ActivityLog activity = new();
        await using ServiceProvider provider = CreateBatchProvider(organizationId, ResumeActor, ResumedAt, activity);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(provider, batchKey, pairA, pairB);

        ExecuteBatchExactMatchItemResponse itemA = Item(response, invoiceA);
        ExecuteBatchExactMatchItemResponse itemB = Item(response, invoiceB);
        Assert.Equal("confirmed", itemA.Outcome);
        Assert.Equal(allocationA.Value, itemA.AllocationId);
        Assert.Equal(18m, itemA.Amount);
        Assert.Equal("PLN", itemA.Currency);
        Assert.Equal("confirmed", itemB.Outcome);
        Assert.NotEqual(allocationA.Value, itemB.AllocationId);
        Assert.Equal(27m, itemB.Amount);
        Assert.Equal("PLN", itemB.Currency);
        Assert.True(response.AllConfirmed);
        Assert.Equal(2, response.ConfirmedCount);
        Assert.Equal(1, activity.CandidateCalls);
        Guid pendingKey = BatchExactMatchItemKey.Derive(organizationId, batchKey, invoiceB.Id, bankB.Id);
        Assert.Equal(pendingKey, Assert.Single(activity.ItemKeys));
        await AssertRecoveredReversedItemAsync(
            organizationId,
            batchKey,
            receipt.Id,
            allocationA,
            invoiceA.Id,
            bankA.Id,
            18m,
            BatchActor,
            releasedAmount: 0m,
            confirmationAudits: 2);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation created = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.IdempotencyKey == pendingKey);
        FinancialAuditEntry createdAudit = await context.FinancialAuditEntries.SingleAsync(
            entry => entry.AllocationId == created.Id);
        Assert.Equal(itemB.AllocationId, created.Id.Value);
        Assert.Equal(AllocationStatus.Confirmed, created.Status);
        Assert.Equal(27m, created.Amount.Amount);
        Assert.Equal(ResumeActor, createdAudit.ActorId);
        Assert.Equal(ResumedAt, createdAudit.RecordedAt);
        Assert.Equal(receipt.Id, createdAudit.BatchExecutionId);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, createdAudit.IdempotencyOperation);
        Assert.Null(createdAudit.Reason);
        Assert.Equal(27m, (await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceB.Id)).AllocatedAmount.Amount);
        Assert.Equal(27m, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankB.Id)).AllocatedAmount.Amount);
        Assert.Equal(2, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Historical_batch_allocation_without_confirmation_audit_stays_unbackfilled_after_reversal()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid batchKey = Guid.NewGuid();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "HIST", 22m);
        BatchExactMatchPair pair = Pair(invoice, bank);
        BatchExecution receipt = await SaveRunningAsync(organizationId, batchKey, pair);
        AllocationId allocationId = await SaveCommittedItemAsync(
            organizationId,
            receipt.Id,
            batchKey,
            invoice,
            bank,
            22m,
            BatchActor,
            writeConfirmationAudit: false);
        await ReverseSuccessAsync(organizationId, allocationId);

        await using ServiceProvider provider = CreateBatchProvider(organizationId, ResumeActor, ResumedAt);
        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(provider, batchKey, pair);

        Assert.Equal(allocationId.Value, Assert.Single(response.Items).AllocationId);
        Assert.Equal("confirmed", Assert.Single(response.Items).Outcome);
        await AssertRecoveredReversedItemAsync(
            organizationId,
            batchKey,
            receipt.Id,
            allocationId,
            invoice.Id,
            bank.Id,
            22m,
            confirmationActor: null,
            releasedAmount: 0m,
            confirmationAudits: 0);
    }

    [Fact]
    public async Task Another_organization_cannot_recover_a_reversed_batch_allocation()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId stranger = OrganizationId.New();
        Guid batchKey = Guid.NewGuid();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "TENANT", 15m);
        BatchExactMatchPair pair = Pair(invoice, bank);
        BatchExecution receipt = await SaveRunningAsync(owner, batchKey, pair);
        AllocationId allocationId = await SaveCommittedItemAsync(
            owner,
            receipt.Id,
            batchKey,
            invoice,
            bank,
            15m,
            BatchActor,
            writeConfirmationAudit: true);
        await ReverseSuccessAsync(owner, allocationId);
        ReceiptView before = await ReadReceiptAsync(owner, batchKey);

        await using ServiceProvider foreignBatch = CreateBatchProvider(stranger, ResumeActor, ResumedAt);
        ExecuteBatchExactMatchesResponse foreign = await ExecuteSuccessAsync(foreignBatch, batchKey, pair);
        Result<ReverseAllocationResponse> foreignReversal = await ReverseAsync(stranger, allocationId, Guid.NewGuid());

        ExecuteBatchExactMatchItemResponse foreignItem = Assert.Single(foreign.Items);
        Assert.Equal("notFound", foreignItem.Outcome);
        Assert.Null(foreignItem.AllocationId);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationNotFound.Code,
            foreignReversal.Error?.Code);
        ReceiptView after = await ReadReceiptAsync(owner, batchKey);
        Assert.Equal(before, after);
        await AssertRecoveredReversedItemAsync(
            owner,
            batchKey,
            receipt.Id,
            allocationId,
            invoice.Id,
            bank.Id,
            15m,
            BatchActor,
            releasedAmount: 0m,
            confirmationAudits: 1,
            expectCompleted: false);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == stranger));
        Assert.Equal(0, await context.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == stranger));
    }

    [Fact]
    public async Task Completed_receipt_stays_frozen_after_reversal()
    {
        CompletedBatch completed = await ExecuteCompletedBatchAsync(19m, "FROZEN");
        await ReverseSuccessAsync(completed.OrganizationId, completed.AllocationId);
        string payloadAfterReversal = (await ReadReceiptAsync(completed.OrganizationId, completed.BatchKey)).ResultPayload!;
        await using ServiceProvider replayProvider = CreateBatchProvider(
            completed.OrganizationId,
            ResumeActor,
            ReversedAt.AddHours(1));

        ExecuteBatchExactMatchesResponse replay = await ExecuteSuccessAsync(
            replayProvider,
            completed.BatchKey,
            completed.Pair);

        AssertSameResponse(completed.Response, replay);
        ReceiptView stored = await ReadReceiptAsync(completed.OrganizationId, completed.BatchKey);
        Assert.Equal(BatchExecutionStatus.Completed, stored.Status);
        Assert.Equal(completed.Payload, stored.ResultPayload);
        Assert.Equal(payloadAfterReversal, stored.ResultPayload);
        Assert.Equal(completed.CompletedAt, stored.CompletedAt);
        Assert.Equal(completed.ResultVersion, stored.ResultVersion);
        Assert.Equal(completed.ReceiptId, stored.Id);
        await AssertRecoveredReversedItemAsync(
            completed.OrganizationId,
            completed.BatchKey,
            completed.ReceiptId,
            completed.AllocationId,
            completed.Pair.InvoiceId,
            completed.Pair.BankTransactionId,
            19m,
            BatchActor,
            releasedAmount: 0m,
            confirmationAudits: 1,
            completedAt: CreatedAt);
    }

    [Fact]
    public async Task New_batch_key_can_allocate_funds_released_by_reversal()
    {
        CompletedBatch completed = await ExecuteCompletedBatchAsync(21m, "REOPEN");
        await ReverseSuccessAsync(completed.OrganizationId, completed.AllocationId);
        string frozenPayload = (await ReadReceiptAsync(completed.OrganizationId, completed.BatchKey)).ResultPayload!;
        Guid newKey = Guid.NewGuid();
        await using ServiceProvider provider = CreateBatchProvider(
            completed.OrganizationId,
            LaterActor,
            ReversedAt.AddHours(1));

        ExecuteBatchExactMatchesResponse created = await ExecuteSuccessAsync(provider, newKey, completed.Pair);

        ExecuteBatchExactMatchItemResponse item = Assert.Single(created.Items);
        Assert.Equal("confirmed", item.Outcome);
        Assert.NotEqual(completed.AllocationId.Value, item.AllocationId);
        Assert.Equal(21m, item.Amount);
        Assert.Equal(frozenPayload, (await ReadReceiptAsync(completed.OrganizationId, completed.BatchKey)).ResultPayload);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid reopenedKey = BatchExactMatchItemKey.Derive(
            completed.OrganizationId,
            newKey,
            completed.Pair.InvoiceId,
            completed.Pair.BankTransactionId);
        Allocation original = await context.Allocations.SingleAsync(candidate => candidate.Id == completed.AllocationId);
        Allocation reopened = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == completed.OrganizationId && candidate.IdempotencyKey == reopenedKey);
        Assert.Equal(item.AllocationId, reopened.Id.Value);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == completed.OrganizationId)
            .ToListAsync();
        FinancialAuditEntry reopenedAudit = Assert.Single(audits, entry => entry.AllocationId == reopened.Id);
        Assert.Equal(AllocationStatus.Reversed, original.Status);
        Assert.Equal(AllocationStatus.Confirmed, reopened.Status);
        Assert.Equal(reopenedKey, reopened.IdempotencyKey);
        Assert.Equal(LaterActor, reopenedAudit.ActorId);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, reopenedAudit.IdempotencyOperation);
        Assert.Equal(2, audits.Count(entry => entry.Action == FinancialAuditAction.Confirmation));
        Assert.Equal(1, audits.Count(entry => entry.Action == FinancialAuditAction.Reversal));
        Assert.Equal(BatchActor, Assert.Single(audits, entry => entry.AllocationId == original.Id && entry.Action == FinancialAuditAction.Confirmation).ActorId);
        Assert.Equal(ReversalActor, Assert.Single(audits, entry => entry.Action == FinancialAuditAction.Reversal).ActorId);
        Assert.Equal(21m, (await context.Invoices.SingleAsync(candidate => candidate.Id == completed.Pair.InvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(21m, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == completed.Pair.BankTransactionId)).AllocatedAmount.Amount);
        Assert.Equal(2, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == completed.OrganizationId));
    }

    private async Task AssertRecoveredReversedItemAsync(
        OrganizationId organizationId,
        Guid batchKey,
        BatchExecutionId receiptId,
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount,
        string? confirmationActor,
        decimal releasedAmount,
        int confirmationAudits,
        bool expectCompleted = true,
        DateTimeOffset? completedAt = null)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankTransactionId);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        ReceiptView receipt = await ReadReceiptAsync(organizationId, batchKey);
        Guid confirmationKey = BatchExactMatchItemKey.Derive(
            organizationId,
            batchKey,
            invoiceId,
            bankTransactionId);

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(confirmationKey, allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, allocation.IdempotencyOperation);
        Assert.Equal(CreatedAt, allocation.ConfirmedAt);
        Assert.Equal(ReversedAt, allocation.ReversedAt);
        Assert.Equal(ReversalReason, allocation.ReversalReason);
        Assert.Equal(amount, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(releasedAmount, invoice.AllocatedAmount.Amount);
        Assert.Equal(releasedAmount, bank.AllocatedAmount.Amount);
        Assert.Equal(confirmationAudits, audits.Count(entry => entry.Action == FinancialAuditAction.Confirmation));
        FinancialAuditEntry reversal = Assert.Single(audits, entry => entry.Action == FinancialAuditAction.Reversal);
        Assert.Equal(allocationId, reversal.AllocationId);
        Assert.Equal(ReversalActor, reversal.ActorId);
        Assert.Equal(ReversedAt, reversal.RecordedAt);
        Assert.Equal(ReversalReason, reversal.Reason);
        Assert.Equal(AllocationIdempotencyOperation.Reversal, reversal.IdempotencyOperation);
        Assert.Equal(amount, reversal.Amount.Amount);
        if (confirmationActor is not null)
        {
            FinancialAuditEntry confirmation = Assert.Single(
                audits,
                entry => entry.AllocationId == allocationId && entry.Action == FinancialAuditAction.Confirmation);
            Assert.Equal(confirmationActor, confirmation.ActorId);
            Assert.Equal(CreatedAt, confirmation.RecordedAt);
            Assert.Equal(confirmationKey, confirmation.IdempotencyKey);
            Assert.Equal(receiptId, confirmation.BatchExecutionId);
            Assert.Null(confirmation.Reason);
            Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, confirmation.IdempotencyOperation);
        }

        Assert.Equal(receiptId, receipt.Id);
        if (expectCompleted)
        {
            Assert.Equal(BatchExecutionStatus.Completed, receipt.Status);
            Assert.Equal(1, receipt.ResultVersion);
            Assert.Equal(completedAt ?? ResumedAt, receipt.CompletedAt);
            Assert.False(string.IsNullOrWhiteSpace(receipt.ResultPayload));
        }
        else
        {
            Assert.Equal(BatchExecutionStatus.Running, receipt.Status);
            Assert.Null(receipt.ResultVersion);
            Assert.Null(receipt.CompletedAt);
            Assert.Null(receipt.ResultPayload);
        }
    }

    private async Task<CompletedBatch> ExecuteCompletedBatchAsync(decimal amount, string reference)
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid batchKey = Guid.NewGuid();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, reference, amount);
        BatchExactMatchPair pair = Pair(invoice, bank);
        await using ServiceProvider provider = CreateBatchProvider(organizationId, BatchActor, CreatedAt);
        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(provider, batchKey, pair);
        ReceiptView receipt = await ReadReceiptAsync(organizationId, batchKey);
        return new CompletedBatch(
            organizationId,
            batchKey,
            receipt.Id,
            pair,
            response,
            new AllocationId(Assert.Single(response.Items).AllocationId!.Value),
            receipt.ResultPayload!,
            receipt.CompletedAt!.Value,
            receipt.ResultVersion!.Value);
    }

    private async Task ReverseSuccessAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid? reversalKey = null)
    {
        Result<ReverseAllocationResponse> result = await ReverseAsync(organizationId, allocationId, reversalKey);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(ReversalReason, result.Value.Reason);
        Assert.Equal(ReversedAt, result.Value.ReversedAt);
    }

    private async Task<Result<ReverseAllocationResponse>> ReverseAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid? reversalKey = null)
    {
        await using ServiceProvider provider = CreateBatchProvider(organizationId, ReversalActor, ReversedAt);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ReverseAllocationHandler handler = scope.ServiceProvider.GetRequiredService<ReverseAllocationHandler>();
        return await handler.Handle(
            new ReverseAllocationCommand(allocationId, ReversalReason, reversalKey ?? Guid.NewGuid()),
            CancellationToken.None);
    }

    private async Task<(Invoice Invoice, BankTransaction Bank)> SeedPairAsync(
        OrganizationId organizationId,
        string reference,
        decimal amount)
    {
        string number = reference + "-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = new(InvoiceId.New(), organizationId, number, new Money(amount, PersistenceTestData.Pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, PersistenceTestData.Pln),
            new DateOnly(2026, 10, 28),
            number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        return (invoice, bank);
    }

    private async Task<BatchExecution> SaveRunningAsync(
        OrganizationId organizationId,
        Guid batchKey,
        params BatchExactMatchPair[] pairs)
    {
        BatchExecution execution = BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            batchKey,
            BatchExactMatchIdentity.Create(pairs).Fingerprint,
            CreatedAt);
        await using ServiceProvider provider = CreateBatchProvider(organizationId, BatchActor, CreatedAt);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>()
            .AddRunningAsync(execution, CancellationToken.None);
        return execution;
    }

    private async Task<AllocationId> SaveCommittedItemAsync(
        OrganizationId organizationId,
        BatchExecutionId receiptId,
        Guid batchKey,
        Invoice invoice,
        BankTransaction bank,
        decimal amount,
        string actorId,
        bool writeConfirmationAudit)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bank.Id);
        Money confirmed = new(amount, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(storedInvoice, storedBank, confirmed).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            confirmed,
            CreatedAt,
            BatchExactMatchItemKey.Derive(organizationId, batchKey, storedInvoice.Id, storedBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        context.Allocations.Add(allocation);
        if (writeConfirmationAudit)
        {
            new FinancialAuditWriter(context).AppendConfirmation(allocation, actorId, receiptId);
        }

        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private ServiceProvider CreateBatchProvider(
        OrganizationId organizationId,
        string actorId,
        DateTimeOffset utcNow,
        ActivityLog? activity = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<ICurrentOrganizationContext>(new TestCurrentOrganizationContext(organizationId));
        services.AddSingleton<ICurrentActorContext>(new TestCurrentActorContext(actorId));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
        services.AddReconciliationModule(_fixture.ConnectionString);
        if (activity is not null)
        {
            services.AddScoped<ExactMatchCandidateQueries>();
            services.AddScoped<IExactMatchCandidateQueries>(serviceProvider =>
                new CountingCandidateQueries(
                    serviceProvider.GetRequiredService<ExactMatchCandidateQueries>(),
                    activity));
            services.AddScoped<BatchExactMatchItemExecutor>();
            services.AddScoped<IBatchExactMatchItemExecutor>(serviceProvider =>
                new CountingItemExecutor(
                    serviceProvider.GetRequiredService<BatchExactMatchItemExecutor>(),
                    activity));
        }

        return services.BuildServiceProvider();
    }

    private static async Task<ExecuteBatchExactMatchesResponse> ExecuteSuccessAsync(
        ServiceProvider provider,
        Guid batchKey,
        params BatchExactMatchPair[] pairs)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ExecuteBatchExactMatchesHandler handler = scope.ServiceProvider
            .GetRequiredService<ExecuteBatchExactMatchesHandler>();
        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(batchKey, pairs),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private async Task<ReceiptView> ReadReceiptAsync(OrganizationId organizationId, Guid batchKey)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BatchExecution execution = await context.BatchExecutions.SingleAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.IdempotencyKey == batchKey);
        return new ReceiptView(
            execution.Id,
            execution.Status,
            execution.ResultVersion,
            execution.ResultPayload,
            execution.CompletedAt);
    }

    private static void AssertSameResponse(
        ExecuteBatchExactMatchesResponse left,
        ExecuteBatchExactMatchesResponse right)
    {
        Assert.Equal(left.ResultVersion, right.ResultVersion);
        Assert.Equal(left.RequestedCount, right.RequestedCount);
        Assert.Equal(left.ConfirmedCount, right.ConfirmedCount);
        Assert.Equal(left.RejectedCount, right.RejectedCount);
        Assert.Equal(left.InterruptedCount, right.InterruptedCount);
        Assert.Equal(left.AllConfirmed, right.AllConfirmed);
        Assert.Equal(left.HasRejections, right.HasRejections);
        Assert.Equal(left.Partial, right.Partial);
        Assert.Equal(left.Items.Count, right.Items.Count);
        for (int index = 0; index < left.Items.Count; index++)
        {
            Assert.Equal(left.Items[index], right.Items[index]);
        }
    }

    private static ExecuteBatchExactMatchItemResponse Item(
        ExecuteBatchExactMatchesResponse response,
        Invoice invoice)
    {
        return Assert.Single(response.Items, item => item.InvoiceId == invoice.Id.Value);
    }

    private static BatchExactMatchPair Pair(Invoice invoice, BankTransaction bank)
    {
        return new BatchExactMatchPair(invoice.Id, bank.Id);
    }

    private sealed record CompletedBatch(
        OrganizationId OrganizationId,
        Guid BatchKey,
        BatchExecutionId ReceiptId,
        BatchExactMatchPair Pair,
        ExecuteBatchExactMatchesResponse Response,
        AllocationId AllocationId,
        string Payload,
        DateTimeOffset CompletedAt,
        int ResultVersion);

    private sealed record ReceiptView(
        BatchExecutionId Id,
        BatchExecutionStatus Status,
        int? ResultVersion,
        string? ResultPayload,
        DateTimeOffset? CompletedAt);

    private sealed class ActivityLog
    {
        public int CandidateCalls { get; set; }

        public List<Guid> ItemKeys { get; } = [];
    }

    private sealed class CountingCandidateQueries(
        ExactMatchCandidateQueries inner,
        ActivityLog activity) : IExactMatchCandidateQueries
    {
        public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            activity.CandidateCalls++;
            return inner.GetOpenCandidatesAsync(organizationId, limit, cancellationToken);
        }
    }

    private sealed class CountingItemExecutor(
        BatchExactMatchItemExecutor inner,
        ActivityLog activity) : IBatchExactMatchItemExecutor
    {
        public Task<BatchItemAttempt> ExecuteAsync(
            OrganizationId organizationId,
            Guid batchIdempotencyKey,
            BatchExecutionId batchExecutionId,
            string actorId,
            InvoiceId invoiceId,
            BankTransactionId bankTransactionId,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken)
        {
            activity.ItemKeys.Add(BatchExactMatchItemKey.Derive(
                organizationId,
                batchIdempotencyKey,
                invoiceId,
                bankTransactionId));
            return inner.ExecuteAsync(
                organizationId,
                batchIdempotencyKey,
                batchExecutionId,
                actorId,
                invoiceId,
                bankTransactionId,
                confirmedAt,
                cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}