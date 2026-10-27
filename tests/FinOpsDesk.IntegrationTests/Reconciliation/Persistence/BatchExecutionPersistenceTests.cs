using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class BatchExecutionPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public BatchExecutionPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_applies_and_phase_4f_tables_remain_usable()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        IEnumerable<string> migrations = await context.Database.GetAppliedMigrationsAsync();
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 20m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 20m);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            null,
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bankTransaction);
        context.ReviewCases.Add(reviewCase);
        await context.SaveChangesAsync();

        Assert.Contains(migrations, name => name.EndsWith("_AddBatchExecutions", StringComparison.Ordinal));
        Assert.Equal(1, await context.Invoices.CountAsync(stored => stored.Id == invoice.Id));
        Assert.Equal(1, await context.BankTransactions.CountAsync(stored => stored.Id == bankTransaction.Id));
        Assert.Equal(1, await context.ReviewCases.CountAsync(stored => stored.Id == reviewCase.Id));
        Assert.Equal(0, await context.Allocations.CountAsync(stored => stored.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Running_and_completed_receipts_round_trip()
    {
        OrganizationId organizationId = OrganizationId.New();
        BatchExecution running = CreateRunning(organizationId, Guid.NewGuid());
        await AddAsync(running);

        BatchExecutionSnapshot? stored = await FindAsync(organizationId, running.IdempotencyKey);

        Assert.NotNull(stored);
        Assert.Equal(BatchExecutionStatus.Running, stored.Status);
        Assert.Null(stored.ResultVersion);
        Assert.Null(stored.ResultPayload);
        Assert.Null(stored.CompletedAt);
        Assert.Equal(running.Fingerprint.ToArray(), stored.Fingerprint.ToArray());

        BatchExecutionResult result = BatchExecutionResult.FromItems([ConfirmedItem()]);
        DateTimeOffset completedAt = running.CreatedAt.AddMinutes(2);
        Assert.True(await CompleteAsync(running.Id, organizationId, result, completedAt));

        BatchExecutionSnapshot? completed = await FindAsync(organizationId, running.IdempotencyKey);

        Assert.NotNull(completed);
        Assert.Equal(BatchExecutionStatus.Completed, completed.Status);
        Assert.Equal(1, completed.ResultVersion);
        Assert.Equal(completedAt, completed.CompletedAt);
        Assert.Equal(BatchExecutionResultDocument.Write(result), completed.ResultPayload);
    }

    [Fact]
    public async Task Idempotency_key_is_unique_inside_one_organization_only()
    {
        OrganizationId firstOrganization = OrganizationId.New();
        OrganizationId secondOrganization = OrganizationId.New();
        Guid key = Guid.NewGuid();
        await AddAsync(CreateRunning(firstOrganization, key));

        UniqueConstraintConflictException conflict = await Assert.ThrowsAsync<UniqueConstraintConflictException>(
            () => AddAsync(CreateRunning(firstOrganization, key)));
        SqlException sqlException = SqlServerCatalog.RequireSqlException(conflict);
        await AddAsync(CreateRunning(secondOrganization, key));

        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.NotNull(await FindAsync(secondOrganization, key));
    }

    [Fact]
    public async Task Check_constraint_rejects_invalid_running_and_completed_shapes()
    {
        OrganizationId organizationId = OrganizationId.New();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        byte[] fingerprint = BatchExactMatchIdentity.Create([Pair(1, 2)]).Fingerprint.ToArray();
        await using ReconciliationDbContext context = _fixture.CreateContext();

        SqlException runningWithPayload = await Assert.ThrowsAsync<SqlException>(() =>
            InsertRawAsync(
                context,
                organizationId,
                createdAt,
                fingerprint,
                status: 1,
                resultVersion: null,
                resultPayload: "payload",
                completedAt: null));
        SqlException completedWithoutPayload = await Assert.ThrowsAsync<SqlException>(() =>
            InsertRawAsync(
                context,
                organizationId,
                createdAt,
                fingerprint,
                status: 2,
                resultVersion: 1,
                resultPayload: null,
                completedAt: createdAt));
        SqlException completedWithoutVersion = await Assert.ThrowsAsync<SqlException>(() =>
            InsertRawAsync(
                context,
                organizationId,
                createdAt,
                fingerprint,
                status: 2,
                resultVersion: null,
                resultPayload: "{}",
                completedAt: createdAt));
        SqlException completedBeforeCreation = await Assert.ThrowsAsync<SqlException>(() =>
            InsertRawAsync(
                context,
                organizationId,
                createdAt,
                fingerprint,
                status: 2,
                resultVersion: 1,
                resultPayload: "{}",
                completedAt: createdAt.AddMinutes(-1)));
        SqlException invalidStatus = await Assert.ThrowsAsync<SqlException>(() =>
            InsertRawAsync(
                context,
                organizationId,
                createdAt,
                fingerprint,
                status: 9,
                resultVersion: null,
                resultPayload: null,
                completedAt: null));

        Assert.Equal(547, runningWithPayload.Number);
        Assert.Equal(547, completedWithoutPayload.Number);
        Assert.Equal(547, completedWithoutVersion.Number);
        Assert.Equal(547, completedBeforeCreation.Number);
        Assert.Equal(547, invalidStatus.Number);
    }

    [Fact]
    public async Task Allocation_operation_values_follow_the_extended_check()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAllocationAsync(organizationId, AllocationIdempotencyOperation.ExactMatch);
        await SaveAllocationAsync(organizationId, AllocationIdempotencyOperation.ExplicitAmount);
        Allocation batch = await SaveAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.BatchExactMatch);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        int? storedOperation = await context.Allocations
            .Where(stored => stored.Id == batch.Id)
            .Select(stored => (int?)stored.IdempotencyOperation)
            .SingleAsync();
        SqlException unsupported = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyOperation] = {4}
                WHERE [Id] = {batch.Id.Value}
                """));
        SqlException keyWithoutOperation = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyOperation] = NULL
                WHERE [Id] = {batch.Id.Value}
                """));

        Assert.Equal(3, storedOperation);
        Assert.Equal(547, unsupported.Number);
        Assert.Equal(547, keyWithoutOperation.Number);
    }

    [Fact]
    public async Task Conditional_completion_succeeds_once_and_leaves_the_stored_result_unchanged()
    {
        OrganizationId organizationId = OrganizationId.New();
        BatchExecution execution = CreateRunning(organizationId, Guid.NewGuid());
        await AddAsync(execution);
        BatchExecutionResult first = BatchExecutionResult.FromItems([ConfirmedItem()]);
        BatchExecutionResult second = BatchExecutionResult.FromItems(
            [RejectedItem(BatchExecutionItemOutcome.SkippedReview)]);
        DateTimeOffset completedAt = execution.CreatedAt.AddMinutes(3);

        Assert.True(await CompleteAsync(execution.Id, organizationId, first, completedAt));
        BatchExecutionSnapshot? completed = await FindAsync(organizationId, execution.IdempotencyKey);
        Assert.False(await CompleteAsync(
            execution.Id,
            organizationId,
            second,
            completedAt.AddMinutes(10)));
        BatchExecutionSnapshot? replay = await FindAsync(organizationId, execution.IdempotencyKey);

        Assert.NotNull(completed);
        Assert.NotNull(replay);
        Assert.Equal(completed.ResultPayload, replay.ResultPayload);
        Assert.Equal(completed.CompletedAt, replay.CompletedAt);
        Assert.Equal(BatchExecutionResultDocument.Write(first), replay.ResultPayload);
        Assert.NotEqual(BatchExecutionResultDocument.Write(second), replay.ResultPayload);
    }

    [Fact]
    public async Task Completion_rejects_a_different_pair_set_and_keeps_the_running_receipt()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Guid idempotencyKey = Guid.NewGuid();
        BatchExecution execution = CreateRunning(organizationId, idempotencyKey, Pair(1, 2), Pair(3, 4));
        BatchExecution otherOrganization = CreateRunning(otherOrganizationId, idempotencyKey, Pair(1, 2));
        await AddAsync(execution);
        await AddAsync(otherOrganization);
        BatchExecutionResult substituted = BatchExecutionResult.FromItems(
        [
            RejectedItem(1, 2, BatchExecutionItemOutcome.SkippedReview),
            RejectedItem(5, 6, BatchExecutionItemOutcome.Conflict)
        ]);

        InvalidOperationException mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CompleteAsync(
                execution.Id,
                organizationId,
                substituted,
                execution.CreatedAt.AddMinutes(1)));

        await using (ReconciliationDbContext reload = _fixture.CreateContext())
        {
            BatchExecution stored = await reload.BatchExecutions.SingleAsync(
                candidate => candidate.Id == execution.Id
                    && candidate.OrganizationId == organizationId);

            Assert.Equal(BatchExecutionStatus.Running, stored.Status);
            Assert.Null(stored.ResultVersion);
            Assert.Null(stored.ResultPayload);
            Assert.Null(stored.CompletedAt);
            Assert.Equal(0, await reload.Allocations.CountAsync(
                allocation => allocation.OrganizationId == organizationId
                    || allocation.OrganizationId == otherOrganizationId));
        }

        BatchExecutionResult matching = BatchExecutionResult.FromItems(
        [
            RejectedItem(1, 2, BatchExecutionItemOutcome.SkippedCoverage),
            RejectedItem(3, 4, BatchExecutionItemOutcome.NotFound)
        ]);
        DateTimeOffset completedAt = execution.CreatedAt.AddMinutes(2);
        Assert.True(await CompleteAsync(execution.Id, organizationId, matching, completedAt));

        BatchExecutionResult differentOutcome = BatchExecutionResult.FromItems(
        [
            ConfirmedItem(1, 2),
            ConfirmedItem(3, 4)
        ]);
        Assert.False(await CompleteAsync(
            execution.Id,
            organizationId,
            differentOutcome,
            completedAt.AddMinutes(5)));

        await using ReconciliationDbContext completedReload = _fixture.CreateContext();
        BatchExecution completed = await completedReload.BatchExecutions.SingleAsync(
            candidate => candidate.Id == execution.Id);
        BatchExecution untouched = await completedReload.BatchExecutions.SingleAsync(
            candidate => candidate.Id == otherOrganization.Id
                && candidate.OrganizationId == otherOrganizationId);

        Assert.Contains("do not match", mismatch.Message, StringComparison.Ordinal);
        Assert.Equal(BatchExecutionStatus.Completed, completed.Status);
        Assert.Equal(completedAt, completed.CompletedAt);
        Assert.Equal(BatchExecutionResultDocument.Write(matching), completed.ResultPayload);
        Assert.NotEqual(BatchExecutionResultDocument.Write(differentOutcome), completed.ResultPayload);
        Assert.Equal(BatchExecutionStatus.Running, untouched.Status);
        Assert.Null(untouched.ResultPayload);
        Assert.Equal(idempotencyKey, untouched.IdempotencyKey);
        Assert.Equal(0, await completedReload.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Receipt_operations_do_not_change_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 80m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 80m);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(10m, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bankTransaction.Id,
            ReviewCaseSource.Exact1,
            null,
            new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero));
        await using (ReconciliationDbContext seed = _fixture.CreateContext())
        {
            seed.Invoices.Add(invoice);
            seed.BankTransactions.Add(bankTransaction);
            seed.Allocations.Add(allocation);
            seed.ReviewCases.Add(reviewCase);
            await seed.SaveChangesAsync();
        }

        BatchExecution execution = CreateRunning(organizationId, Guid.NewGuid());
        await AddAsync(execution);
        Assert.True(await CompleteAsync(
            execution.Id,
            organizationId,
            BatchExecutionResult.FromItems([ConfirmedItem()]),
            execution.CreatedAt.AddMinutes(1)));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(stored => stored.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(
            stored => stored.Id == bankTransaction.Id);
        Allocation storedAllocation = await context.Allocations.SingleAsync(stored => stored.Id == allocation.Id);
        ReviewCase storedReview = await context.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id);

        Assert.Equal(invoice.AllocatedAmount, storedInvoice.AllocatedAmount);
        Assert.Equal(bankTransaction.AllocatedAmount, storedBank.AllocatedAmount);
        Assert.Equal(allocation.Amount, storedAllocation.Amount);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, storedAllocation.IdempotencyOperation);
        Assert.Equal(ReviewCaseStatus.Open, storedReview.Status);
        Assert.Equal(1, await context.Allocations.CountAsync(stored => stored.OrganizationId == organizationId));
        Assert.Equal(1, await context.BatchExecutions.CountAsync(stored => stored.OrganizationId == organizationId));
    }

    private async Task AddAsync(BatchExecution execution)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionStore store = scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>();
        await store.AddRunningAsync(execution, CancellationToken.None);
    }

    private async Task<bool> CompleteAsync(
        BatchExecutionId id,
        OrganizationId organizationId,
        BatchExecutionResult result,
        DateTimeOffset completedAt)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionStore store = scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>();
        return await store.TryCompleteAsync(
            id,
            organizationId,
            result,
            completedAt,
            CancellationToken.None);
    }

    private async Task<BatchExecutionSnapshot?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBatchExecutionStore store = scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>();
        return await store.FindAsync(organizationId, idempotencyKey, CancellationToken.None);
    }

    private static BatchExecution CreateRunning(OrganizationId organizationId, Guid idempotencyKey)
    {
        return CreateRunning(organizationId, idempotencyKey, Pair(1, 2));
    }

    private static BatchExecution CreateRunning(
        OrganizationId organizationId,
        Guid idempotencyKey,
        params BatchExactMatchPair[] pairs)
    {
        return BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            idempotencyKey,
            BatchExactMatchIdentity.Create(pairs).Fingerprint,
            new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero));
    }

    private static async Task InsertRawAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        DateTimeOffset createdAt,
        byte[] fingerprint,
        int status,
        int? resultVersion,
        string? resultPayload,
        DateTimeOffset? completedAt)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [reconciliation].[BatchExecutions]
                ([Id], [OrganizationId], [IdempotencyKey], [Fingerprint], [Status], [ResultVersion], [ResultPayload], [CreatedAt], [CompletedAt])
            VALUES
                ({Guid.NewGuid()}, {organizationId.Value}, {Guid.NewGuid()}, {fingerprint}, {status}, {resultVersion}, {resultPayload}, {createdAt}, {completedAt})
            """);
    }

    private async Task<Allocation> SaveAllocationAsync(
        OrganizationId organizationId,
        AllocationIdempotencyOperation operation)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 30m, "INV-" + operation);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            30m,
            reference: "INV-" + operation);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(30m, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            operation);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bankTransaction);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation;
    }

    private static BatchExecutionItem ConfirmedItem()
    {
        return ConfirmedItem(1, 2);
    }

    private static BatchExecutionItem ConfirmedItem(int invoiceSuffix, int bankSuffix)
    {
        return new BatchExecutionItem(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)),
            BatchExecutionItemOutcome.Confirmed,
            new AllocationId(GuidOf(invoiceSuffix + 500)),
            new Money(40m, PersistenceTestData.Pln));
    }

    private static BatchExecutionItem RejectedItem(BatchExecutionItemOutcome outcome)
    {
        return RejectedItem(1, 2, outcome);
    }

    private static BatchExecutionItem RejectedItem(
        int invoiceSuffix,
        int bankSuffix,
        BatchExecutionItemOutcome outcome)
    {
        return new BatchExecutionItem(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)),
            outcome,
            allocationId: null,
            amount: null);
    }

    private static BatchExactMatchPair Pair(int invoiceSuffix, int bankSuffix)
    {
        return new BatchExactMatchPair(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)));
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }
}