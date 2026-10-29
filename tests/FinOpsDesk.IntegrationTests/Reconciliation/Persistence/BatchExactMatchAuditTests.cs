using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

public sealed partial class BatchExactMatchExecutionTests
{
    private static readonly DateTimeOffset AuditConfirmedAt =
        new(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Successful_batch_item_persists_one_batch_confirmation_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-1", 40m);
        Guid key = Guid.NewGuid();
        FixedTimeProvider clock = new(AuditConfirmedAt);

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            clock,
            Pair(invoice, bank));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == organizationId);
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        BatchExecution execution = await context.BatchExecutions.SingleAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.IdempotencyKey == key);
        Guid itemKey = BatchExactMatchItemKey.Derive(organizationId, key, invoice.Id, bank.Id);

        Assert.Equal(allocation.Id.Value, response.Items[0].AllocationId);
        Assert.Equal(allocation.Id, audit.AllocationId);
        Assert.Equal(invoice.Id, audit.InvoiceId);
        Assert.Equal(bank.Id, audit.BankTransactionId);
        Assert.Equal(organizationId, audit.OrganizationId);
        Assert.Equal(new Money(40m, Pln), audit.Amount);
        Assert.Equal(FinancialAuditAction.Confirmation, audit.Action);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, audit.IdempotencyOperation);
        Assert.Equal(itemKey, audit.IdempotencyKey);
        Assert.Equal(allocation.IdempotencyKey, audit.IdempotencyKey);
        Assert.Equal(execution.Id, audit.BatchExecutionId);
        Assert.Equal(allocation.ConfirmedAt, audit.RecordedAt);
        Assert.Equal(AuditConfirmedAt, audit.RecordedAt);
        Assert.Equal(TestCurrentActorContext.DefaultActorId, audit.ActorId);
    }

    [Fact]
    public async Task Multiple_successful_items_create_one_audit_each_for_the_same_batch()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "AUD-A", 15m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "AUD-B", 16m);
        Guid key = Guid.NewGuid();

        await ExecuteSuccessAsync(
            organizationId,
            key,
            new FixedTimeProvider(AuditConfirmedAt),
            Pair(firstInvoice, firstBank),
            Pair(secondInvoice, secondBank));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        BatchExecution execution = await context.BatchExecutions.SingleAsync(
            candidate => candidate.OrganizationId == organizationId);
        Assert.Equal(2, audits.Count);
        Assert.Equal(2, audits.Select(entry => entry.AllocationId).Distinct().Count());
        Assert.All(audits, entry => Assert.Equal(execution.Id, entry.BatchExecutionId));
        Assert.All(
            audits,
            entry => Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, entry.IdempotencyOperation));
        Assert.Contains(
            audits,
            entry => entry.IdempotencyKey == BatchExactMatchItemKey.Derive(
                organizationId,
                key,
                firstInvoice.Id,
                firstBank.Id));
        Assert.Contains(
            audits,
            entry => entry.IdempotencyKey == BatchExactMatchItemKey.Derive(
                organizationId,
                key,
                secondInvoice.Id,
                secondBank.Id));
    }

    [Fact]
    public async Task Item_failure_before_commit_leaves_no_allocation_or_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-FAIL", 18m);
        ThrowOnCallSynchronization gate = new() { FailOn = 1 };
        Guid key = Guid.NewGuid();
        await using ServiceProvider provider = CreateProvider(organizationId, synchronization: gate);

        Result<ExecuteBatchExactMatchesResponse> failed = await ExecuteAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal(ReconciliationApplicationErrors.BatchInfrastructureFailure, failed.Error);
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(0, await AuditCountAsync(organizationId));
        Assert.Equal(18m, await OutstandingAsync(invoice.Id));
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
    }

    [Fact]
    public async Task Historical_allocation_recovered_from_a_running_batch_is_not_backfilled()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-OLD", 22m);
        Guid key = Guid.NewGuid();
        DateTimeOffset historicalConfirmedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        AllocationId historicalId = await SaveHistoricalBatchAllocationAsync(
            organizationId,
            key,
            invoice,
            bank,
            22m,
            historicalConfirmedAt);
        await SaveRunningAsync(
            organizationId,
            key,
            historicalConfirmedAt,
            Pair(invoice, bank));

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            organizationId,
            key,
            new FixedTimeProvider(AuditConfirmedAt),
            Pair(invoice, bank));

        Assert.Equal(historicalId.Value, Assert.Single(response.Items).AllocationId);
        Assert.Equal(0, await AuditCountAsync(organizationId));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Completed_batch_replay_does_not_create_another_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-REPLAY", 9m);
        Guid key = Guid.NewGuid();
        FixedTimeProvider clock = new(AuditConfirmedAt);
        ExecuteBatchExactMatchesResponse created = await ExecuteSuccessAsync(
            organizationId,
            key,
            clock,
            Pair(invoice, bank));
        ExecuteBatchExactMatchesResponse replayed = await ExecuteSuccessAsync(
            organizationId,
            key,
            clock,
            Pair(invoice, bank));

        Assert.Equal(created.Items[0].AllocationId, replayed.Items[0].AllocationId);
        Assert.Equal(1, await AuditCountAsync(organizationId));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_only_the_current_item_and_retry_keeps_the_first_actor()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(organizationId, "OP-A", 31m);
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(organizationId, "OP-B", 32m);
        BatchExactMatchPair[] pairs = [Pair(firstInvoice, firstBank), Pair(secondInvoice, secondBank)];
        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create(pairs);
        Invoice firstCommittedInvoice = identity.Pairs[0].InvoiceId == firstInvoice.Id
            ? firstInvoice
            : secondInvoice;
        Invoice rolledBackInvoice = identity.Pairs[0].InvoiceId == firstInvoice.Id
            ? secondInvoice
            : firstInvoice;
        Guid key = Guid.NewGuid();
        AuditFailurePlan plan = new() { FailOnCall = 2 };
        await using ServiceProvider failingProvider = CreateProvider(
            organizationId,
            new FixedTimeProvider(AuditConfirmedAt),
            actorId: "operator-a",
            configure: services =>
            {
                services.AddSingleton(plan);
                services.AddScoped<IFinancialAuditWriter, SelectiveDuplicatingFinancialAuditWriter>();
            });

        Result<ExecuteBatchExactMatchesResponse> failed = await ExecuteAsync(
            failingProvider,
            organizationId,
            key,
            pairs);

        Assert.Equal(ReconciliationApplicationErrors.BatchInfrastructureFailure, failed.Error);
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(1, await AuditCountAsync(organizationId));
        Assert.Equal(0m, await OutstandingAsync(firstCommittedInvoice.Id));
        Assert.Equal(
            firstCommittedInvoice.Id == firstInvoice.Id ? 32m : 31m,
            await OutstandingAsync(rolledBackInvoice.Id));

        await using ServiceProvider retryProvider = CreateProvider(
            organizationId,
            new FixedTimeProvider(AuditConfirmedAt.AddMinutes(5)),
            actorId: "operator-b");
        ExecuteBatchExactMatchesResponse recovered = await ExecuteSuccessAsync(
            retryProvider,
            organizationId,
            key,
            pairs);

        Assert.True(recovered.AllConfirmed);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        List<Allocation> allocations = await context.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        BatchExecution execution = await context.BatchExecutions.SingleAsync(
            candidate => candidate.OrganizationId == organizationId);
        Allocation firstAllocation = Assert.Single(
            allocations,
            allocation => allocation.InvoiceId == firstCommittedInvoice.Id);
        Allocation secondAllocation = Assert.Single(
            allocations,
            allocation => allocation.InvoiceId == rolledBackInvoice.Id);
        FinancialAuditEntry firstAudit = Assert.Single(
            audits,
            entry => entry.AllocationId == firstAllocation.Id);
        FinancialAuditEntry secondAudit = Assert.Single(
            audits,
            entry => entry.AllocationId == secondAllocation.Id);
        Assert.Equal("operator-a", firstAudit.ActorId);
        Assert.Equal("operator-b", secondAudit.ActorId);
        Assert.Equal(execution.Id, firstAudit.BatchExecutionId);
        Assert.Equal(execution.Id, secondAudit.BatchExecutionId);
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task Financial_concurrency_failure_does_not_commit_an_audit_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-ROW", 14m);
        Guid key = Guid.NewGuid();
        await using ServiceProvider provider = CreateProvider(
            organizationId,
            new FixedTimeProvider(AuditConfirmedAt),
            synchronization: new StaleInvoiceSynchronization(_fixture.ConnectionString, invoice.Id.Value));

        ExecuteBatchExactMatchesResponse response = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal("conflict", Assert.Single(response.Items).Outcome);
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(0, await AuditCountAsync(organizationId));
        Assert.Equal(14m, await OutstandingAsync(invoice.Id));
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));

        ExecuteBatchExactMatchesResponse replayed = await ExecuteSuccessAsync(
            provider,
            organizationId,
            key,
            Pair(invoice, bank));

        Assert.Equal("conflict", Assert.Single(replayed.Items).Outcome);
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
    }

    private async Task<AllocationId> SaveHistoricalBatchAllocationAsync(
        OrganizationId organizationId,
        Guid key,
        Invoice invoice,
        BankTransaction bank,
        decimal amount,
        DateTimeOffset confirmedAt)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bank.Id);
        Money confirmed = new(amount, Pln);
        Assert.True(AllocationConfirmation.Apply(storedInvoice, storedBank, confirmed).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            confirmed,
            confirmedAt,
            BatchExactMatchItemKey.Derive(organizationId, key, storedInvoice.Id, storedBank.Id),
            AllocationIdempotencyOperation.BatchExactMatch);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private sealed class AuditFailurePlan
    {
        public int FailOnCall { get; init; }

        public int Calls;
    }

    private sealed class SelectiveDuplicatingFinancialAuditWriter : IFinancialAuditWriter
    {
        private readonly ReconciliationDbContext _dbContext;
        private readonly AuditFailurePlan _plan;

        public SelectiveDuplicatingFinancialAuditWriter(
            ReconciliationDbContext dbContext,
            AuditFailurePlan plan)
        {
            _plan = plan;
            _dbContext = dbContext;
        }

        public void AppendConfirmation(
            Allocation allocation,
            string actorId,
            BatchExecutionId? batchExecutionId)
        {
            _dbContext.FinancialAuditEntries.Add(
                FinancialAuditEntry.ForConfirmation(
                    FinancialAuditEntryId.New(),
                    allocation,
                    actorId,
                    batchExecutionId));
            int call = Interlocked.Increment(ref _plan.Calls);
            if (call == _plan.FailOnCall)
            {
                _dbContext.FinancialAuditEntries.Add(
                    FinancialAuditEntry.ForConfirmation(
                        FinancialAuditEntryId.New(),
                        allocation,
                        actorId,
                        batchExecutionId));
            }
        }

        public void AppendReversal(
            Allocation allocation,
            string actorId,
            Guid reversalIdempotencyKey)
        {
            throw new InvalidOperationException("This confirmation test double does not record reversals.");
        }
    }

    private sealed class StaleInvoiceSynchronization : IBatchExactMatchItemSynchronization
    {
        private readonly string _connectionString;
        private readonly Guid _invoiceId;

        public StaleInvoiceSynchronization(string connectionString, Guid invoiceId)
        {
            _connectionString = connectionString;
            _invoiceId = invoiceId;
        }

        public async Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE [reconciliation].[Invoices]
                SET [InvoiceNumber] = [InvoiceNumber]
                WHERE [Id] = @id
                """;
            command.Parameters.Add(new SqlParameter("@id", _invoiceId));
            int updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException("Expected exactly one invoice rowversion bump.");
            }
        }
    }
}