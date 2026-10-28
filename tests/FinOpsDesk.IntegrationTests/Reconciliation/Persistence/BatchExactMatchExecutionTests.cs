using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed partial class BatchExactMatchExecutionTests
{
    private static readonly CurrencyCode Pln = PersistenceTestData.Pln;
    private static readonly DateTimeOffset OpenedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public BatchExactMatchExecutionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task One_owner_blocks_the_same_key_and_completion_uses_that_connection()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "LOCK-1", 25m);
        Guid key = Guid.NewGuid();
        BatchExecution receipt = await SaveRunningAsync(organizationId, key, invoice, bank);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionOwner owner = scope.ServiceProvider.GetRequiredService<IBatchExecutionOwner>();
        Result<IBatchExecutionLease> first = await owner.TryAcquireAsync(
            organizationId,
            key,
            CancellationToken.None);
        Result<IBatchExecutionLease> blocked = await owner.TryAcquireAsync(
            organizationId,
            key,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(ReconciliationApplicationErrors.BatchExecutionInProgress, blocked.Error);

        BatchExecutionResult result = BatchExecutionResult.FromItems(
        [
            Rejected(invoice, bank, BatchExecutionItemOutcome.Interrupted)
        ]);
        DateTimeOffset completedAt = receipt.CreatedAt.AddMinutes(1);
        Assert.True(await first.Value.TryCompleteAsync(result, completedAt, CancellationToken.None));
        await first.Value.CommitAsync(CancellationToken.None);
        await first.Value.DisposeAsync();

        await using ReconciliationDbContext context = _fixture.CreateContext();
        BatchExecution stored = await context.BatchExecutions.SingleAsync(
            candidate => candidate.Id == receipt.Id);
        Assert.Equal(BatchExecutionStatus.Completed, stored.Status);
        Assert.Equal(completedAt, stored.CompletedAt);
        Assert.Equal(BatchExecutionResultDocument.Write(result), stored.ResultPayload);
        Assert.Equal(0, await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Different_keys_do_not_share_a_lock_and_a_released_lock_can_be_acquired()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "LOCK-A", 21m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "LOCK-B", 22m);
        Guid firstKey = Guid.NewGuid();
        Guid secondKey = Guid.NewGuid();
        await SaveRunningAsync(organizationId, firstKey, firstInvoice, firstBank);
        await SaveRunningAsync(organizationId, secondKey, secondInvoice, secondBank);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionOwner owner = scope.ServiceProvider.GetRequiredService<IBatchExecutionOwner>();
        IBatchExecutionLease held = (await owner.TryAcquireAsync(
            organizationId,
            firstKey,
            CancellationToken.None)).Value!;
        Result<IBatchExecutionLease> other = await owner.TryAcquireAsync(
            organizationId,
            secondKey,
            CancellationToken.None);

        Assert.True(other.IsSuccess);
        await other.Value!.DisposeAsync();
        int sessionId = held.OwnerSessionId;
        await KillSessionAsync(sessionId);

        Result<IBatchExecutionLease> reacquired = await owner.TryAcquireAsync(
            organizationId,
            firstKey,
            CancellationToken.None);
        Assert.True(reacquired.IsSuccess);
        await held.DisposeAsync();
        await reacquired.Value!.DisposeAsync();
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, firstKey));
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, secondKey));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Uncommitted_lease_disposal_rolls_back_and_releases_the_lock()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "ROLLBACK", 27m);
        Guid key = Guid.NewGuid();
        BatchExecution receipt = await SaveRunningAsync(organizationId, key, invoice, bank);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionOwner owner = scope.ServiceProvider.GetRequiredService<IBatchExecutionOwner>();
        IBatchExecutionLease lease = (await owner.TryAcquireAsync(
            organizationId,
            key,
            CancellationToken.None)).Value!;
        BatchExecutionResult result = BatchExecutionResult.FromItems(
            [Rejected(invoice, bank, BatchExecutionItemOutcome.Interrupted)]);

        Assert.True(await lease.TryCompleteAsync(
            result,
            receipt.CreatedAt.AddMinutes(1),
            CancellationToken.None));
        await lease.DisposeAsync();

        Result<IBatchExecutionLease> reacquired = await owner.TryAcquireAsync(
            organizationId,
            key,
            CancellationToken.None);
        Assert.True(reacquired.IsSuccess);
        await reacquired.Value!.DisposeAsync();
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        Assert.Null(await CompletedAtAsync(organizationId, key));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Handler_reports_execution_in_progress_while_the_owner_holds_the_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "LOCK-H", 23m);
        Guid key = Guid.NewGuid();
        await SaveRunningAsync(organizationId, key, invoice, bank);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope ownerScope = provider.CreateAsyncScope();
        IBatchExecutionLease lease = (await ownerScope.ServiceProvider
            .GetRequiredService<IBatchExecutionOwner>()
            .TryAcquireAsync(organizationId, key, CancellationToken.None)).Value!;

        Result<ExecuteBatchExactMatchesResponse> blocked = await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.BatchExecutionInProgress, blocked.Error);
        await lease.DisposeAsync();
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Cancellation_releases_the_ownership_lock()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "CANCEL", 24m);
        BlockingSynchronization gate = new();
        await using ServiceProvider provider = CreateProvider(organizationId, synchronization: gate);
        using CancellationTokenSource cancellation = new();
        Task<Result<ExecuteBatchExactMatchesResponse>> running = ExecuteAsync(
            provider,
            organizationId,
            Guid.NewGuid(),
            cancellation.Token,
            Pair(invoice, bank));
        await gate.Entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        BatchExecution receipt = await scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>()
            .BatchExecutions
            .SingleAsync(candidate => candidate.OrganizationId == organizationId);
        Result<IBatchExecutionLease> acquired = await scope.ServiceProvider
            .GetRequiredService<IBatchExecutionOwner>()
            .TryAcquireAsync(organizationId, receipt.IdempotencyKey, CancellationToken.None);

        Assert.True(acquired.IsSuccess);
        Assert.Equal(BatchExecutionStatus.Running, receipt.Status);
        await acquired.Value.DisposeAsync();
    }

    [Fact]
    public async Task Eligible_pair_commits_balances_and_allocation_together()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "PAY-1", 40m);
        Guid key = Guid.NewGuid();

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.True(response.AllConfirmed);
        Assert.False(response.HasRejections);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bank.Id);
        Allocation allocation = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == organizationId);
        Assert.Equal(0m, storedInvoice.OutstandingAmount.Amount);
        Assert.Equal(0m, storedBank.UnappliedAmount.Amount);
        Assert.Equal(40m, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(response.Items[0].AllocationId, allocation.Id.Value);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, allocation.IdempotencyOperation);
        Assert.Equal(
            BatchExactMatchItemKey.Derive(organizationId, key, invoice.Id, bank.Id),
            allocation.IdempotencyKey);
    }

    [Fact]
    public async Task Forced_failure_rolls_back_the_item_and_a_later_recovery_does_not_duplicate_it()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "ATOM-1", 15m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "ATOM-2", 16m);
        ThrowOnCallSynchronization gate = new() { FailOn = 2 };
        Guid key = Guid.NewGuid();
        await using ServiceProvider provider = CreateProvider(organizationId, synchronization: gate);

        Result<ExecuteBatchExactMatchesResponse> failed = await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        Assert.Equal(ReconciliationApplicationErrors.BatchInfrastructureFailure, failed.Error);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create(
            [Pair(firstInvoice, firstBank), Pair(secondInvoice, secondBank)]);
        bool firstCommitted = identity.Pairs[0].InvoiceId == firstInvoice.Id;
        Invoice committedInvoice = firstCommitted ? firstInvoice : secondInvoice;
        Invoice rolledBackInvoice = firstCommitted ? secondInvoice : firstInvoice;
        Assert.Equal(0m, await OutstandingAsync(committedInvoice.Id));
        Assert.Equal(firstCommitted ? 16m : 15m, await OutstandingAsync(rolledBackInvoice.Id));

        gate.FailOn = int.MaxValue;
        ExecuteBatchExactMatchesResponse recovered = (await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank))).Value!;

        Assert.True(recovered.AllConfirmed);
        Assert.Equal(2, await AllocationCountAsync(organizationId));
        Assert.Equal(
            Item(recovered, firstInvoice).AllocationId,
            await AllocationIdForAsync(organizationId, firstInvoice.Id));
    }

    [Fact]
    public async Task Business_rejection_does_not_allocate_and_the_next_pair_can_commit()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice staleInvoice, BankTransaction staleBank) = await SeedPairAsync(
            organizationId,
            "STALE-INV",
            30m,
            bankReference: "OTHER");
        (Invoice validInvoice, BankTransaction validBank) = await SeedPairAsync(organizationId, "VALID", 18m);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            Guid.NewGuid(),
            Pair(staleInvoice, staleBank),
            Pair(validInvoice, validBank));

        Assert.False(response.AllConfirmed);
        Assert.True(response.Partial);
        Assert.Equal("stale", Item(response, staleInvoice).Outcome);
        Assert.Equal("confirmed", Item(response, validInvoice).Outcome);
        Assert.Equal(30m, await OutstandingAsync(staleInvoice.Id));
        Assert.Equal(0m, await OutstandingAsync(validInvoice.Id));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Completed_replay_is_stable_for_the_same_pairs_in_any_order()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "ORD-1", 11m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "ORD-2", 12m);
        Guid key = Guid.NewGuid();
        ExecuteBatchExactMatchesResponse first = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(secondInvoice, secondBank),
            Pair(firstInvoice, firstBank));
        byte[] invoiceVersion = await InvoiceVersionAsync(firstInvoice.Id);
        int allocations = await AllocationCountAsync(organizationId);
        DateTimeOffset? completedAt = await CompletedAtAsync(organizationId, key);

        ExecuteBatchExactMatchesResponse replay = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        Assert.Equal(first.Items[0].AllocationId, replay.Items[0].AllocationId);
        Assert.Equal(first.Items[1].AllocationId, replay.Items[1].AllocationId);
        Assert.Equal(completedAt, await CompletedAtAsync(organizationId, key));
        Assert.Equal(allocations, await AllocationCountAsync(organizationId));
        Assert.Equal(invoiceVersion, await InvoiceVersionAsync(firstInvoice.Id));
    }

    [Fact]
    public async Task Same_key_with_different_pairs_conflicts_and_another_key_is_not_a_replay()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "ONCE", 19m);
        (Invoice otherInvoice, BankTransaction otherBank) = await SeedPairAsync(organizationId, "TWICE", 29m);
        Guid key = Guid.NewGuid();
        await ExecuteSuccessAsync(organizationId, key, Pair(invoice, bank));

        Result<ExecuteBatchExactMatchesResponse> conflict = await ExecuteAsync(
            CreateProvider(organizationId),
            organizationId,
            key,
            Pair(otherInvoice, otherBank));
        ExecuteBatchExactMatchesResponse secondKey = await ExecuteSuccessAsync(
            organizationId,
            Guid.NewGuid(),
            Pair(otherInvoice, otherBank));

        Assert.Equal(ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict, conflict.Error);
        Assert.NotEqual(secondKey.Items[0].AllocationId, (await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(invoice, bank))).Items[0].AllocationId);
        Assert.Equal(2, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Mismatched_recovered_allocation_is_an_integrity_conflict()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "IDENT", 33m);
        (Invoice decoyInvoice, BankTransaction decoyBank) = await SeedPairAsync(organizationId, "DECOY", 34m);
        Guid key = Guid.NewGuid();
        Guid itemKey = BatchExactMatchItemKey.Derive(organizationId, key, invoice.Id, bank.Id);
        await SaveAllocationAsync(
            organizationId,
            decoyInvoice,
            decoyBank,
            34m,
            itemKey,
            AllocationIdempotencyOperation.BatchExactMatch);

        Result<ExecuteBatchExactMatchesResponse> wrongIdentity = await ExecuteAsync(
            CreateProvider(organizationId),
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.BatchAllocationIntegrityConflict, wrongIdentity.Error);
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(33m, await OutstandingAsync(invoice.Id));
    }

    [Fact]
    public async Task Wrong_operation_for_the_item_key_is_an_integrity_conflict()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "OP-1", 36m);
        Guid key = Guid.NewGuid();
        await SaveAllocationAsync(
            organizationId,
            invoice,
            bank,
            36m,
            BatchExactMatchItemKey.Derive(organizationId, key, invoice.Id, bank.Id),
            AllocationIdempotencyOperation.ExactMatch);

        Result<ExecuteBatchExactMatchesResponse> result = await ExecuteAsync(
            CreateProvider(organizationId),
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.BatchAllocationIntegrityConflict, result.Error);
        Assert.Equal(36m, await OutstandingAsync(invoice.Id));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task The_same_idempotency_key_in_another_organization_is_independent()
    {
        OrganizationId first = OrganizationId.New();
        OrganizationId second = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(first, "ORG-A", 14m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(second, "ORG-B", 14m);
        Guid key = Guid.NewGuid();

        ExecuteBatchExactMatchesResponse left = await ExecuteSuccessAsync(first, key, Pair(firstInvoice, firstBank));
        ExecuteBatchExactMatchesResponse right = await ExecuteSuccessAsync(second, key, Pair(secondInvoice, secondBank));

        Assert.NotEqual(left.Items[0].AllocationId, right.Items[0].AllocationId);
        Assert.Equal(1, await AllocationCountAsync(first));
        Assert.Equal(1, await AllocationCountAsync(second));
    }

    [Fact]
    public async Task Recovered_allocation_keeps_its_id_amount_and_currency()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "REPLAY", 41m);
        Guid key = Guid.NewGuid();
        ExecuteBatchExactMatchesResponse first = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(invoice, bank));
        byte[] version = await InvoiceVersionAsync(invoice.Id);

        ExecuteBatchExactMatchesResponse second = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(first.Items[0].AllocationId, second.Items[0].AllocationId);
        Assert.Equal(41m, second.Items[0].Amount);
        Assert.Equal("PLN", second.Items[0].Currency);
        Assert.Equal("confirmed", second.Items[0].Outcome);
        Assert.Equal(version, await InvoiceVersionAsync(invoice.Id));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Eligibility_rejections_do_not_allocate()
    {
        OrganizationId currencyOrganization = OrganizationId.New();
        Invoice plnInvoice = await SaveInvoiceAsync(currencyOrganization, "CUR-1", 10m, Pln);
        BankTransaction eurBank = await SaveBankAsync(
            currencyOrganization,
            10m,
            new CurrencyCode("EUR"),
            "CUR-1");
        await AssertOutcomeAsync(currencyOrganization, "stale", plnInvoice, eurBank);

        OrganizationId amountOrganization = OrganizationId.New();
        (Invoice amountInvoice, _) = await SeedPairAsync(amountOrganization, "AMT", 10m);
        BankTransaction otherAmount = await SaveBankAsync(amountOrganization, 11m, Pln, "AMT");
        await AssertOutcomeAsync(amountOrganization, "stale", amountInvoice, otherAmount);

        OrganizationId settledOrganization = OrganizationId.New();
        (Invoice settledInvoice, BankTransaction settledBank) = await SeedPairAsync(
            settledOrganization,
            "FULL",
            10m);
        await AllocateAsync(settledInvoice.Id, settledBank.Id, 10m);
        await AssertOutcomeAsync(settledOrganization, "stale", settledInvoice, settledBank);

        OrganizationId ambiguousOrganization = OrganizationId.New();
        (Invoice ambiguousInvoice, BankTransaction ambiguousBank) = await SeedPairAsync(
            ambiguousOrganization,
            "AMB",
            10m);
        await SaveInvoiceAsync(ambiguousOrganization, "amb", 10m, Pln);
        await AssertOutcomeAsync(ambiguousOrganization, "skippedAmbiguous", ambiguousInvoice, ambiguousBank);

        OrganizationId bankAmbiguousOrganization = OrganizationId.New();
        (Invoice bankAmbiguousInvoice, BankTransaction bankAmbiguousBank) = await SeedPairAsync(
            bankAmbiguousOrganization,
            "BANK-AMB",
            12m);
        await SaveBankAsync(bankAmbiguousOrganization, 12m, Pln, "bank-amb");
        await AssertOutcomeAsync(
            bankAmbiguousOrganization,
            "skippedAmbiguous",
            bankAmbiguousInvoice,
            bankAmbiguousBank);

        OrganizationId bothOrganization = OrganizationId.New();
        (Invoice bothInvoice, BankTransaction bothBank) = await SeedPairAsync(bothOrganization, "BOTH", 14m);
        await SaveInvoiceAsync(bothOrganization, "both", 14m, Pln);
        await SaveBankAsync(bothOrganization, 14m, Pln, "both");
        await AssertOutcomeAsync(bothOrganization, "skippedAmbiguous", bothInvoice, bothBank);

        OrganizationId normalizedOrganization = OrganizationId.New();
        Invoice lowerInvoice = await SaveInvoiceAsync(normalizedOrganization, "inv-norm", 13m, Pln);
        BankTransaction upperBank = await SaveBankAsync(normalizedOrganization, 13m, Pln, "INV-NORM");
        ExecuteBatchExactMatchesResponse normalized = await ExecuteSuccessAsync(
            normalizedOrganization,
            Guid.NewGuid(),
            Pair(lowerInvoice, upperBank));
        Assert.Equal("confirmed", normalized.Items[0].Outcome);
    }

    [Fact]
    public async Task Equal_remainders_after_partial_settlement_confirm_the_current_balance()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "PART", 20m);
        await AllocateAsync(invoice.Id, bank.Id, 5m);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            Guid.NewGuid(),
            Pair(invoice, bank));

        Assert.Equal("confirmed", response.Items[0].Outcome);
        Assert.Equal(15m, response.Items[0].Amount);
        Assert.Equal("PLN", response.Items[0].Currency);
        Assert.Equal(0m, await OutstandingAsync(invoice.Id));
        Assert.Equal(0m, await UnappliedAsync(bank.Id));
    }

    [Fact]
    public async Task Competitor_probe_boundaries_fail_closed()
    {
        OrganizationId uniqueOrganization = OrganizationId.New();
        (Invoice uniqueInvoice, BankTransaction uniqueBank) = await SeedPairAsync(
            uniqueOrganization,
            "PROBE-U",
            51m);
        await InsertOpenInvoicesAsync(uniqueOrganization, 100, "OTHER-U-", 51m);
        ExecuteBatchExactMatchesResponse unique = await ExecuteSuccessAsync(
            uniqueOrganization,
            Guid.NewGuid(),
            Pair(uniqueInvoice, uniqueBank));
        Assert.Equal("confirmed", unique.Items[0].Outcome);

        OrganizationId limitedOrganization = OrganizationId.New();
        (Invoice limitedInvoice, BankTransaction limitedBank) = await SeedPairAsync(
            limitedOrganization,
            "PROBE-L",
            52m);
        await InsertOpenInvoicesAsync(limitedOrganization, 101, "OTHER-L-", 52m);
        await AssertOutcomeAsync(limitedOrganization, "skippedCoverage", limitedInvoice, limitedBank);

        OrganizationId ambiguousOrganization = OrganizationId.New();
        (Invoice ambiguousInvoice, BankTransaction ambiguousBank) = await SeedPairAsync(
            ambiguousOrganization,
            "PROBE-A",
            53m);
        await InsertOpenInvoicesAsync(ambiguousOrganization, 100, "OTHER-A-", 53m);
        await SaveInvoiceAsync(ambiguousOrganization, "probe-a", 53m, Pln);
        await AssertOutcomeAsync(ambiguousOrganization, "skippedAmbiguous", ambiguousInvoice, ambiguousBank);
    }

    [Fact]
    public async Task Inconsistent_money_creates_neither_a_receipt_nor_an_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "BAD-MONEY", 17m);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({Guid.NewGuid()}, {organizationId.Value}, {"CORRUPT"}, {0m}, {"USD"}, {10m}, {"PLN"})
            """);

        Result<ExecuteBatchExactMatchesResponse> result = await ExecuteAsync(
            CreateProvider(organizationId),
            organizationId,
            Guid.NewGuid(),
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.InconsistentMatchMoney, result.Error);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(17m, await OutstandingAsync(invoice.Id));
    }

    [Fact]
    public async Task Open_reviews_block_either_named_id_and_terminal_reviews_do_not()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "REV-1", 27m);
        ReviewCase invoiceCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            null,
            OpenedAt);
        await SaveReviewAsync(invoiceCase);
        await AssertOutcomeAsync(organizationId, "skippedReview", invoice, bank);
        Assert.Equal(ReviewCaseStatus.Open, await ReviewStatusAsync(invoiceCase.Id));

        OrganizationId bankOrganization = OrganizationId.New();
        (Invoice bankInvoice, BankTransaction bankOnly) = await SeedPairAsync(bankOrganization, "REV-BANK", 26m);
        await SaveReviewAsync(ReviewCase.Open(
            ReviewCaseId.New(),
            bankOrganization,
            ReviewCaseReason.NeedsInvestigation,
            null,
            bankOnly.Id,
            null,
            null,
            OpenedAt));
        await AssertOutcomeAsync(bankOrganization, "skippedReview", bankInvoice, bankOnly);

        OrganizationId competingOrganization = OrganizationId.New();
        (Invoice competingInvoice, BankTransaction competingBank) = await SeedPairAsync(
            competingOrganization,
            "REV-2",
            28m);
        BankTransaction otherBank = await SaveBankAsync(competingOrganization, 99m, Pln, "UNRELATED");
        ReviewCase competing = ReviewCase.Open(
            ReviewCaseId.New(),
            competingOrganization,
            ReviewCaseReason.CompetingMatch,
            competingInvoice.Id,
            otherBank.Id,
            ReviewCaseSource.Exact1,
            null,
            OpenedAt);
        await SaveReviewAsync(competing);
        await AssertOutcomeAsync(competingOrganization, "skippedReview", competingInvoice, competingBank);

        await DismissAsync(invoiceCase.Id);
        ExecuteBatchExactMatchesResponse confirmed = await ExecuteSuccessAsync(
            organizationId,
            Guid.NewGuid(),
            Pair(invoice, bank));
        Assert.Equal("confirmed", confirmed.Items[0].Outcome);
        Assert.Equal(ReviewCaseStatus.Dismissed, await ReviewStatusAsync(invoiceCase.Id));
    }

    [Fact]
    public async Task Expired_running_batch_interrupts_remaining_items_without_a_new_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice committedInvoice, BankTransaction committedBank) = await SeedPairAsync(
            organizationId,
            "AGE-1",
            31m);
        (Invoice pendingInvoice, BankTransaction pendingBank) = await SeedPairAsync(
            organizationId,
            "AGE-2",
            32m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddMinutes(-16);
        await SaveRunningAsync(
            organizationId,
            key,
            createdAt,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));
        await SaveAllocationAsync(
            organizationId,
            committedInvoice,
            committedBank,
            31m,
            BatchExactMatchItemKey.Derive(organizationId, key, committedInvoice.Id, committedBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await AllocateAsync(committedInvoice.Id, committedBank.Id, 31m);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));

        Assert.Equal("confirmed", Item(response, committedInvoice).Outcome);
        Assert.Equal("interrupted", Item(response, pendingInvoice).Outcome);
        Assert.False(response.AllConfirmed);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(32m, await OutstandingAsync(pendingInvoice.Id));
    }

    [Fact]
    public async Task Deadline_between_items_does_not_start_the_next_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "STEP-1", 8m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "STEP-2", 9m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        await SaveRunningAsync(organizationId, key, createdAt, Pair(firstInvoice, firstBank), Pair(secondInvoice, secondBank));
        SteppingTimeProvider time = new(createdAt);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            time,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create(
            [Pair(firstInvoice, firstBank), Pair(secondInvoice, secondBank)]);
        string payload = await PayloadAsync(organizationId, key);
        BatchExecution.EnsureResultMatches(identity.Fingerprint, BatchExecutionResultDocument.Read(payload));

        Assert.Equal(identity.Pairs[0].InvoiceId.Value, response.Items[0].InvoiceId);
        Assert.Equal(identity.Pairs[1].InvoiceId.Value, response.Items[1].InvoiceId);
        Assert.Equal("confirmed", response.Items[0].Outcome);
        Assert.Equal("interrupted", response.Items[1].Outcome);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(createdAt.AddMinutes(15), await CompletedAtAsync(organizationId, key));
        Assert.Equal(identity.Fingerprint.ToArray(), await FingerprintAsync(organizationId, key));
    }

    [Fact]
    public async Task Candidate_query_crossing_the_deadline_does_not_start_an_allocation()
    {
        await AssertClockAdvancedDuringCandidateReadAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Candidate_query_reaching_the_exact_deadline_does_not_start_an_allocation()
    {
        await AssertClockAdvancedDuringCandidateReadAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task Running_recovery_rechecks_current_state_and_a_completed_result_stays_frozen()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "RECHECK", 44m);
        Guid runningKey = Guid.NewGuid();
        await SaveRunningAsync(organizationId, runningKey, invoice, bank);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[BankTransactions]
                SET [Amount] = {45m}
                WHERE [Id] = {bank.Id.Value}
                """);
        }

        await AssertOutcomeAsync(organizationId, runningKey, "stale", invoice, bank);

        (Invoice freshInvoice, BankTransaction freshBank) = await SeedPairAsync(organizationId, "FROZEN", 46m);
        Guid completedKey = Guid.NewGuid();
        ExecuteBatchExactMatchesResponse completed = await ExecuteSuccessAsync(
            organizationId,
            completedKey,
            Pair(freshInvoice, freshBank));
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            freshInvoice.Id,
            null,
            null,
            null,
            OpenedAt);
        await SaveReviewAsync(reviewCase);
        ExecuteBatchExactMatchesResponse replay = await ExecuteSuccessAsync(
            organizationId,
            completedKey,
            Pair(freshInvoice, freshBank));

        Assert.Equal(completed.Items[0].AllocationId, replay.Items[0].AllocationId);
        Assert.Equal("confirmed", replay.Items[0].Outcome);
        Assert.Equal(ReviewCaseStatus.Open, await ReviewStatusAsync(reviewCase.Id));
    }

    [Fact]
    public async Task Over_limit_requests_do_not_start_a_new_receipt_and_running_recovery_skips_coverage()
    {
        OrganizationId freshOrganization = OrganizationId.New();
        await InsertOpenInvoicesAsync(freshOrganization, ExactMatchCandidateLimits.MaxPerSide + 1, "LIMIT-", 3m);
        Guid unusedKey = Guid.NewGuid();
        Result<ExecuteBatchExactMatchesResponse> rejected = await ExecuteAsync(
            CreateProvider(freshOrganization),
            freshOrganization,
            unusedKey,
            new BatchExactMatchPair(new InvoiceId(Guid.NewGuid()), new BankTransactionId(Guid.NewGuid())));

        Assert.Equal(ReconciliationApplicationErrors.BatchCandidateLimitExceeded, rejected.Error);
        Assert.Equal(0, await ReceiptCountAsync(freshOrganization));
        Assert.Equal(0, await AllocationCountAsync(freshOrganization));

        OrganizationId runningOrganization = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(runningOrganization, "LIMIT-RUN", 4m);
        Guid key = Guid.NewGuid();
        await SaveRunningAsync(runningOrganization, key, invoice, bank);
        await InsertOpenInvoicesAsync(
            runningOrganization,
            ExactMatchCandidateLimits.MaxPerSide,
            "LIMIT-RUN-",
            5m);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            runningOrganization,
            key,
            Pair(invoice, bank));

        Assert.Equal("skippedCoverage", response.Items[0].Outcome);
        Assert.False(response.AllConfirmed);
        Assert.Equal(0, await AllocationCountAsync(runningOrganization));
        Assert.Equal(4m, await OutstandingAsync(invoice.Id));
    }

    [Fact]
    public async Task Expired_recovery_finalizes_when_unrelated_money_is_inconsistent()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice committedInvoice, BankTransaction committedBank) = await SeedPairAsync(
            organizationId,
            "AGE-CORRUPT-A",
            61m);
        (Invoice pendingInvoice, BankTransaction pendingBank) = await SeedPairAsync(
            organizationId,
            "AGE-CORRUPT-B",
            62m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        await SaveRunningAsync(
            organizationId,
            key,
            createdAt,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));
        Guid allocationId = await SaveAllocationAsync(
            organizationId,
            committedInvoice,
            committedBank,
            61m,
            BatchExactMatchItemKey.Derive(organizationId, key, committedInvoice.Id, committedBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await AllocateAsync(committedInvoice.Id, committedBank.Id, 61m);
        Guid corruptId = await InsertInconsistentInvoiceAsync(organizationId);
        QueryCounters counters = new();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(createdAt.AddMinutes(15)),
            counters: counters);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));

        Assert.Equal("confirmed", Item(response, committedInvoice).Outcome);
        Assert.Equal(allocationId, Item(response, committedInvoice).AllocationId);
        Assert.Equal(61m, Item(response, committedInvoice).Amount);
        Assert.Equal("PLN", Item(response, committedInvoice).Currency);
        Assert.Equal("interrupted", Item(response, pendingInvoice).Outcome);
        Assert.Null(Item(response, pendingInvoice).AllocationId);
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(62m, await OutstandingAsync(pendingInvoice.Id));
        Assert.Equal(0, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);
        StoredMoneyRow corrupt = await InvoiceMoneyAsync(corruptId);
        Assert.Equal(0m, corrupt.AllocatedAmount);
        Assert.Equal("USD", corrupt.AllocatedCurrency);
        Assert.Equal(10m, corrupt.TotalAmount);
        Assert.Equal("PLN", corrupt.TotalCurrency);
    }

    [Fact]
    public async Task Fully_recovered_batch_completes_despite_unrelated_inconsistent_money()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "BOTH-A", 63m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "BOTH-B", 64m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 12, 30, 0, TimeSpan.Zero);
        await SaveRunningAsync(
            organizationId,
            key,
            createdAt,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));
        Guid firstAllocation = await SaveAllocationAsync(
            organizationId,
            firstInvoice,
            firstBank,
            63m,
            BatchExactMatchItemKey.Derive(organizationId, key, firstInvoice.Id, firstBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        Guid secondAllocation = await SaveAllocationAsync(
            organizationId,
            secondInvoice,
            secondBank,
            64m,
            BatchExactMatchItemKey.Derive(organizationId, key, secondInvoice.Id, secondBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await AllocateAsync(firstInvoice.Id, firstBank.Id, 63m);
        await AllocateAsync(secondInvoice.Id, secondBank.Id, 64m);
        await InsertInconsistentInvoiceAsync(organizationId);
        QueryCounters counters = new();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(createdAt.AddMinutes(1)),
            counters: counters);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(secondInvoice, secondBank),
            Pair(firstInvoice, firstBank));
        string payload = await PayloadAsync(organizationId, key);
        DateTimeOffset? completedAt = await CompletedAtAsync(organizationId, key);

        Assert.True(response.AllConfirmed);
        Assert.Equal(firstAllocation, Item(response, firstInvoice).AllocationId);
        Assert.Equal(63m, Item(response, firstInvoice).Amount);
        Assert.Equal(secondAllocation, Item(response, secondInvoice).AllocationId);
        Assert.Equal(64m, Item(response, secondInvoice).Amount);
        Assert.Equal(2, await AllocationCountAsync(organizationId));
        Assert.NotNull(completedAt);
        Assert.Equal(0, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);

        ExecuteBatchExactMatchesResponse replay = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        Assert.Equal(payload, await PayloadAsync(organizationId, key));
        Assert.Equal(completedAt, await CompletedAtAsync(organizationId, key));
        Assert.Equal(response.Items[0].AllocationId, replay.Items[0].AllocationId);
        Assert.Equal(response.Items[1].AllocationId, replay.Items[1].AllocationId);
        Assert.Equal(0, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);
        Assert.Equal(2, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Fully_recovered_batch_completes_when_the_open_book_exceeds_the_limit()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "LIMIT-A", 65m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "LIMIT-B", 66m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 13, 0, 0, TimeSpan.Zero);
        await SaveRunningAsync(
            organizationId,
            key,
            createdAt,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));
        await SaveAllocationAsync(
            organizationId,
            firstInvoice,
            firstBank,
            65m,
            BatchExactMatchItemKey.Derive(organizationId, key, firstInvoice.Id, firstBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await SaveAllocationAsync(
            organizationId,
            secondInvoice,
            secondBank,
            66m,
            BatchExactMatchItemKey.Derive(organizationId, key, secondInvoice.Id, secondBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await AllocateAsync(firstInvoice.Id, firstBank.Id, 65m);
        await AllocateAsync(secondInvoice.Id, secondBank.Id, 66m);
        await InsertOpenInvoicesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide + 1,
            "LIMIT-RECOVER-",
            7m);
        QueryCounters counters = new();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(createdAt.AddMinutes(1)),
            counters: counters);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        Assert.True(response.AllConfirmed);
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(2, await AllocationCountAsync(organizationId));
        Assert.Equal(0, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);
    }

    [Fact]
    public async Task Unexpired_recovery_stops_when_uncommitted_work_meets_inconsistent_money()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice committedInvoice, BankTransaction committedBank) = await SeedPairAsync(
            organizationId,
            "OPEN-A",
            67m);
        (Invoice pendingInvoice, BankTransaction pendingBank) = await SeedPairAsync(
            organizationId,
            "OPEN-B",
            68m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 13, 30, 0, TimeSpan.Zero);
        await SaveRunningAsync(
            organizationId,
            key,
            createdAt,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));
        Guid allocationId = await SaveAllocationAsync(
            organizationId,
            committedInvoice,
            committedBank,
            67m,
            BatchExactMatchItemKey.Derive(organizationId, key, committedInvoice.Id, committedBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        await AllocateAsync(committedInvoice.Id, committedBank.Id, 67m);
        await InsertInconsistentInvoiceAsync(organizationId);
        QueryCounters counters = new();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(createdAt.AddMinutes(14)),
            counters: counters);

        Result<ExecuteBatchExactMatchesResponse> result = await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(committedInvoice, committedBank),
            Pair(pendingInvoice, pendingBank));

        Assert.Equal(ReconciliationApplicationErrors.InconsistentMatchMoney, result.Error);
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(allocationId, await AllocationIdForAsync(organizationId, committedInvoice.Id));
        Assert.Equal(68m, await OutstandingAsync(pendingInvoice.Id));
        Assert.Equal(0m, await OutstandingAsync(committedInvoice.Id));
        Assert.Equal(1, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);
    }

    [Fact]
    public async Task Expired_identity_mismatch_is_not_finalized_as_interrupted()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "EXPIRED-ID", 69m);
        (Invoice decoyInvoice, BankTransaction decoyBank) = await SeedPairAsync(organizationId, "EXPIRED-DECOY", 70m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);
        await SaveRunningAsync(organizationId, key, createdAt, Pair(invoice, bank));
        await SaveAllocationAsync(
            organizationId,
            decoyInvoice,
            decoyBank,
            70m,
            BatchExactMatchItemKey.Derive(organizationId, key, invoice.Id, bank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        QueryCounters counters = new();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(createdAt.AddMinutes(15)),
            counters: counters);

        Result<ExecuteBatchExactMatchesResponse> result = await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.BatchAllocationIntegrityConflict, result.Error);
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        Assert.Null(await CompletedAtAsync(organizationId, key));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(69m, await OutstandingAsync(invoice.Id));
        Assert.Equal(0, counters.CandidateCalls);
        Assert.Equal(0, counters.ReviewCalls);
    }

    [Fact]
    public async Task Preview_does_not_modify_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "PREVIEW", 6m);
        byte[] version = await InvoiceVersionAsync(invoice.Id);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PreviewBatchExactMatchesHandler handler = scope.ServiceProvider
            .GetRequiredService<PreviewBatchExactMatchesHandler>();

        Result<PreviewBatchExactMatchesResponse> result = await handler.Handle(
            new PreviewBatchExactMatchesQuery(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.InputComplete);
        Assert.Equal(1, result.Value.EligibleCount);
        Assert.Equal(version, await InvoiceVersionAsync(invoice.Id));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(6m, await UnappliedAsync(bank.Id));
    }

    [Fact]
    public async Task Concurrent_updates_of_the_same_pair_leave_one_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "RACE", 26m);
        BarrierSynchronization synchronization = new(2);
        await using ServiceProvider provider = CreateProvider(organizationId, synchronization: synchronization);
        BatchExactMatchPair pair = Pair(invoice, bank);

        Result<ExecuteBatchExactMatchesResponse>[] results = await Task.WhenAll(
            ExecuteAsync(provider, organizationId, Guid.NewGuid(), pair),
            ExecuteAsync(provider, organizationId, Guid.NewGuid(), pair));

        int confirmed = results.Count(result => result.IsSuccess && result.Value.AllConfirmed);
        int conflicted = results.Count(result =>
            result.IsSuccess && result.Value.Items[0].Outcome is "conflict" or "stale");
        Assert.Equal(1, confirmed);
        Assert.Equal(1, conflicted);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(0m, await OutstandingAsync(invoice.Id));
    }

    private async Task AssertOutcomeAsync(
        OrganizationId organizationId,
        string outcome,
        Invoice invoice,
        BankTransaction bank)
    {
        await AssertOutcomeAsync(organizationId, Guid.NewGuid(), outcome, invoice, bank);
    }

    private async Task AssertOutcomeAsync(
        OrganizationId organizationId,
        Guid key,
        string outcome,
        Invoice invoice,
        BankTransaction bank)
    {
        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            Pair(invoice, bank));
        Assert.Equal(outcome, response.Items[0].Outcome);
        Assert.False(response.AllConfirmed);
    }

    private async Task<ExecuteBatchExactMatchesResponse> ExecuteSuccessAsync(
        OrganizationId organizationId,
        Guid key,
        params BatchExactMatchPair[] pairs)
    {
        return await ExecuteSuccessAsync(organizationId, key, time: null, pairs);
    }

    private async Task<ExecuteBatchExactMatchesResponse> ExecuteSuccessAsync(
        OrganizationId organizationId,
        Guid key,
        TimeProvider? time,
        params BatchExactMatchPair[] pairs)
    {
        await using ServiceProvider provider = CreateProvider(organizationId, time);
        return await ExecuteSuccessAsync(provider, organizationId, key, pairs);
    }

    private static async Task<ExecuteBatchExactMatchesResponse> ExecuteSuccessAsync(
        ServiceProvider provider,
        OrganizationId organizationId,
        Guid key,
        params BatchExactMatchPair[] pairs)
    {
        Result<ExecuteBatchExactMatchesResponse> result = await ExecuteAsync(
            provider,
            organizationId,
            key,
            pairs);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<Result<ExecuteBatchExactMatchesResponse>> ExecuteAsync(
        ServiceProvider provider,
        OrganizationId organizationId,
        Guid key,
        params BatchExactMatchPair[] pairs)
    {
        return await ExecuteAsync(provider, organizationId, key, CancellationToken.None, pairs);
    }

    private static async Task<Result<ExecuteBatchExactMatchesResponse>> ExecuteAsync(
        ServiceProvider provider,
        OrganizationId organizationId,
        Guid key,
        CancellationToken cancellationToken,
        params BatchExactMatchPair[] pairs)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ExecuteBatchExactMatchesHandler handler = scope.ServiceProvider
            .GetRequiredService<ExecuteBatchExactMatchesHandler>();
        return await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, pairs),
            cancellationToken);
    }

    private ServiceProvider CreateProvider(
        OrganizationId organizationId,
        TimeProvider? time = null,
        IBatchExactMatchItemSynchronization? synchronization = null,
        QueryCounters? counters = null,
        Action? afterCandidateQuery = null,
        bool forbidItemExecution = false,
        string? actorId = TestCurrentActorContext.DefaultActorId,
        Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<ICurrentOrganizationContext>(new TestCurrentOrganizationContext(organizationId));
        services.AddSingleton<ICurrentActorContext>(new TestCurrentActorContext(actorId));
        services.AddSingleton(time ?? TimeProvider.System);
        if (synchronization is not null)
        {
            services.AddSingleton(synchronization);
        }

        services.AddReconciliationModule(_fixture.ConnectionString);

        if (counters is not null)
        {
            services.AddScoped<ExactMatchCandidateQueries>();
            services.AddScoped<IExactMatchCandidateQueries>(serviceProvider =>
                new CountingCandidateQueries(
                    serviceProvider.GetRequiredService<ExactMatchCandidateQueries>(),
                    counters));
            services.AddScoped<OpenReviewBlockQueries>();
            services.AddScoped<IOpenReviewBlockQueries>(serviceProvider =>
                new CountingReviewQueries(
                    serviceProvider.GetRequiredService<OpenReviewBlockQueries>(),
                    counters));
        }

        if (afterCandidateQuery is not null)
        {
            services.AddScoped<ExactMatchCandidateQueries>();
            services.AddScoped<IExactMatchCandidateQueries>(serviceProvider =>
                new AdvancingCandidateQueries(
                    serviceProvider.GetRequiredService<ExactMatchCandidateQueries>(),
                    afterCandidateQuery));
        }

        if (forbidItemExecution)
        {
            services.AddScoped<IBatchExactMatchItemExecutor, ForbiddenItemExecutor>();
        }

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private async Task AssertClockAdvancedDuringCandidateReadAsync(TimeSpan pastDeadline)
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "CLOCK", 11m);
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
        await SaveRunningAsync(organizationId, key, createdAt, Pair(invoice, bank));
        DateTimeOffset deadline = createdAt.AddMinutes(15);
        AdvancingTimeProvider clock = new(deadline.AddSeconds(-1));
        int reads = 0;
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            clock,
            afterCandidateQuery: () =>
            {
                reads++;
                clock.AdvanceTo(deadline.Add(pastDeadline));
            },
            forbidItemExecution: true);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));
        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create([Pair(invoice, bank)]);
        string payload = await PayloadAsync(organizationId, key);
        BatchExecution.EnsureResultMatches(
            identity.Fingerprint,
            BatchExecutionResultDocument.Read(payload));

        Assert.Equal(1, reads);
        Assert.Equal("interrupted", Assert.Single(response.Items).Outcome);
        Assert.Null(response.Items[0].AllocationId);
        Assert.False(response.AllConfirmed);
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(11m, await OutstandingAsync(invoice.Id));
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(identity.Fingerprint.ToArray(), await FingerprintAsync(organizationId, key));
    }

    private async Task<(Invoice Invoice, BankTransaction Bank)> SeedPairAsync(
        OrganizationId organizationId,
        string reference,
        decimal amount,
        string? bankReference = null)
    {
        Invoice invoice = await SaveInvoiceAsync(organizationId, reference, amount, Pln);
        BankTransaction bank = await SaveBankAsync(
            organizationId,
            amount,
            Pln,
            bankReference ?? reference);
        return (invoice, bank);
    }

    private async Task<Invoice> SaveInvoiceAsync(
        OrganizationId organizationId,
        string number,
        decimal amount,
        CurrencyCode currency)
    {
        Invoice invoice = new(InvoiceId.New(), organizationId, number, new Money(amount, currency));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private async Task<BankTransaction> SaveBankAsync(
        OrganizationId organizationId,
        decimal amount,
        CurrencyCode currency,
        string reference)
    {
        BankTransaction bank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, currency),
            new DateOnly(2026, 10, 27),
            reference);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.BankTransactions.Add(bank);
        await context.SaveChangesAsync();
        return bank;
    }

    private async Task AllocateAsync(InvoiceId invoiceId, BankTransactionId bankTransactionId, decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        Assert.True(invoice.Allocate(new Money(amount, Pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(amount, Pln)).IsSuccess);
        await context.SaveChangesAsync();
    }

    private async Task DismissAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(candidate => candidate.Id == reviewCaseId);
        Assert.True(reviewCase.Dismiss(null, OpenedAt.AddMinutes(5)).IsSuccess);
        await context.SaveChangesAsync();
    }

    private async Task SaveReviewAsync(ReviewCase reviewCase)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase? existing = await context.ReviewCases.SingleOrDefaultAsync(
            candidate => candidate.Id == reviewCase.Id);

        if (existing is null)
        {
            context.ReviewCases.Add(reviewCase);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(reviewCase);
        }

        await context.SaveChangesAsync();
    }

    private async Task<Guid> SaveAllocationAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bank,
        decimal amount,
        Guid idempotencyKey,
        AllocationIdempotencyOperation operation)
    {
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(amount, Pln),
            OpenedAt,
            idempotencyKey,
            operation);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id.Value;
    }

    private async Task<BatchExecution> SaveRunningAsync(
        OrganizationId organizationId,
        Guid key,
        Invoice invoice,
        BankTransaction bank)
    {
        return await SaveRunningAsync(
            organizationId,
            key,
            DateTimeOffset.UtcNow,
            Pair(invoice, bank));
    }

    private async Task<BatchExecution> SaveRunningAsync(
        OrganizationId organizationId,
        Guid key,
        DateTimeOffset createdAt,
        params BatchExactMatchPair[] pairs)
    {
        BatchExecution execution = BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            key,
            BatchExactMatchIdentity.Create(pairs).Fingerprint,
            createdAt);
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>()
            .AddRunningAsync(execution, CancellationToken.None);
        return execution;
    }

    private async Task InsertOpenInvoicesAsync(
        OrganizationId organizationId,
        int count,
        string prefix,
        decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH numbers AS (
                SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects AS leftSide
                CROSS JOIN sys.all_objects AS rightSide
            )
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            SELECT NEWID(), {organizationId.Value}, CONCAT({prefix}, numbers.n),
                   {0m}, {"PLN"}, {amount}, {"PLN"}
            FROM numbers;
            """);
    }

    private async Task KillSessionAsync(int sessionId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"KILL {sessionId}", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> AllocationCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId);
    }

    private async Task<int> ReceiptCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BatchExecutions.CountAsync(
            execution => execution.OrganizationId == organizationId);
    }

    private async Task<BatchExecutionStatus> ReceiptStatusAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BatchExecutions
            .Where(execution => execution.OrganizationId == organizationId && execution.IdempotencyKey == key)
            .Select(execution => execution.Status)
            .SingleAsync();
    }

    private async Task<Guid> InsertInconsistentInvoiceAsync(OrganizationId organizationId)
    {
        Guid id = Guid.NewGuid();
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({id}, {organizationId.Value}, {"CORRUPT"}, {0m}, {"USD"}, {10m}, {"PLN"})
            """);
        return id;
    }

    private async Task<StoredMoneyRow> InvoiceMoneyAsync(Guid invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Database.SqlQuery<StoredMoneyRow>(
            $"""
            SELECT
                [AllocatedAmount],
                [AllocatedCurrency],
                [TotalAmount],
                [TotalCurrency]
            FROM [reconciliation].[Invoices]
            WHERE [Id] = {invoiceId}
            """).SingleAsync();
    }

    private async Task<string> PayloadAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        string? payload = await context.BatchExecutions
            .Where(execution => execution.OrganizationId == organizationId && execution.IdempotencyKey == key)
            .Select(execution => execution.ResultPayload)
            .SingleAsync();
        return payload ?? throw new InvalidOperationException("Batch execution payload is missing.");
    }

    private async Task<byte[]> FingerprintAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BatchExecution execution = await context.BatchExecutions.SingleAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.IdempotencyKey == key);
        return execution.Fingerprint.ToArray();
    }

    private async Task<DateTimeOffset?> CompletedAtAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BatchExecutions
            .Where(execution => execution.OrganizationId == organizationId && execution.IdempotencyKey == key)
            .Select(execution => execution.CompletedAt)
            .SingleAsync();
    }

    private async Task<decimal> OutstandingAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return invoice.OutstandingAmount.Amount;
    }

    private async Task<decimal> UnappliedAsync(BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        return bank.UnappliedAmount.Amount;
    }

    private async Task<byte[]> InvoiceVersionAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private async Task<Guid?> AllocationIdForAsync(OrganizationId organizationId, InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId
                && allocation.InvoiceId == invoiceId)
            .Select(allocation => (Guid?)allocation.Id.Value)
            .SingleAsync();
    }

    private async Task<ReviewCaseStatus> ReviewStatusAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases
            .Where(reviewCase => reviewCase.Id == reviewCaseId)
            .Select(reviewCase => reviewCase.Status)
            .SingleAsync();
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

    private static BatchExecutionItem Rejected(
        Invoice invoice,
        BankTransaction bank,
        BatchExecutionItemOutcome outcome)
    {
        return new BatchExecutionItem(
            invoice.Id,
            bank.Id,
            outcome,
            allocationId: null,
            amount: null);
    }

    private sealed class BlockingSynchronization : IBatchExactMatchItemSynchronization
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class ThrowOnCallSynchronization : IBatchExactMatchItemSynchronization
    {
        public int FailOn { get; set; } = int.MaxValue;

        private int _calls;

        public Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == FailOn)
            {
                throw new InvalidOperationException("Forced item failure before commit.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed record StoredMoneyRow(
        decimal AllocatedAmount,
        string AllocatedCurrency,
        decimal TotalAmount,
        string TotalCurrency);

    private sealed class QueryCounters
    {
        public int CandidateCalls { get; set; }

        public int ReviewCalls { get; set; }
    }

    private sealed class CountingCandidateQueries(
        ExactMatchCandidateQueries inner,
        QueryCounters counters) : IExactMatchCandidateQueries
    {
        public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            counters.CandidateCalls++;
            return inner.GetOpenCandidatesAsync(organizationId, limit, cancellationToken);
        }
    }

    private sealed class CountingReviewQueries(
        OpenReviewBlockQueries inner,
        QueryCounters counters) : IOpenReviewBlockQueries
    {
        public Task<IReadOnlyList<ReviewBlockSubject>> ListOpenAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken)
        {
            counters.ReviewCalls++;
            return inner.ListOpenAsync(organizationId, cancellationToken);
        }

        public Task<IReadOnlyList<ReviewBlockSubject>> ListOpenForPairAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            BankTransactionId bankTransactionId,
            CancellationToken cancellationToken)
        {
            counters.ReviewCalls++;
            return inner.ListOpenForPairAsync(
                organizationId,
                invoiceId,
                bankTransactionId,
                cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void AdvanceTo(DateTimeOffset value)
        {
            _now = value;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    private sealed class AdvancingCandidateQueries(
        ExactMatchCandidateQueries inner,
        Action afterRead) : IExactMatchCandidateQueries
    {
        public async Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            ExactMatchCandidateSet set = await inner.GetOpenCandidatesAsync(
                organizationId,
                limit,
                cancellationToken);
            afterRead();
            return set;
        }
    }

    private sealed class ForbiddenItemExecutor : IBatchExactMatchItemExecutor
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
            throw new InvalidOperationException("A batch allocation started after the deadline.");
        }
    }

    private sealed class SteppingTimeProvider(DateTimeOffset createdAt) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            int call = Interlocked.Increment(ref _calls);

            // The first reading decides whether the book is loaded. The second starts the first item.
            return call <= 2
                ? createdAt.AddMinutes(1)
                : createdAt.AddMinutes(15);
        }
    }

    private sealed class BarrierSynchronization(int participants) : IBatchExactMatchItemSynchronization
    {
        private readonly Barrier _barrier = new(participants);

        public Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            _barrier.SignalAndWait(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        }
    }
}