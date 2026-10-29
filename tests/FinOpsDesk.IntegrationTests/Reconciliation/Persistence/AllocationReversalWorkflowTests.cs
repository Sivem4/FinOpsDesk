using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalWorkflowTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ReversedAt = ConfirmedAt.AddHours(2);

    private readonly SqlServerFixture _fixture;

    public AllocationReversalWorkflowTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Full_reversal_restores_both_balances_and_writes_one_reversal_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid confirmationKey = Guid.NewGuid();
        AllocationId allocationId = await AddAllocationAsync(
            organizationId,
            invoiceTotal: 100m,
            bankTotal: 100m,
            amount: 40m,
            confirmationKey,
            AllocationIdempotencyOperation.ExplicitAmount,
            confirmationAudit: true,
            confirmationActor: "confirm-operator");
        Guid reversalKey = Guid.NewGuid();

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            organizationId,
            allocationId,
            "  duplicate payment  ",
            reversalKey,
            actorId: "reversal-operator");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Replayed);
        Assert.Equal("reversed", result.Value.Status);
        Assert.Equal(40m, result.Value.Amount.Amount);
        Assert.Equal("PLN", result.Value.Amount.Currency);
        Assert.Equal("duplicate payment", result.Value.Reason);
        Assert.Equal(ReversedAt, result.Value.ReversedAt);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == allocation.BankTransactionId);
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(confirmationKey, allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, allocation.IdempotencyOperation);
        Assert.Equal(ConfirmedAt, allocation.ConfirmedAt);
        Assert.Equal(40m, allocation.Amount.Amount);
        Assert.Equal(reversalKey, ReversalKey(context, allocation));
        Assert.Equal(0m, invoice.AllocatedAmount.Amount);
        Assert.Equal(100m, invoice.OutstandingAmount.Amount);
        Assert.Equal(0m, bank.AllocatedAmount.Amount);
        Assert.Equal(100m, bank.UnappliedAmount.Amount);

        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.AllocationId == allocationId)
            .OrderBy(entry => entry.Action)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(FinancialAuditAction.Confirmation, audits[0].Action);
        Assert.Equal("confirm-operator", audits[0].ActorId);
        Assert.Equal(confirmationKey, audits[0].IdempotencyKey);
        Assert.Equal(ConfirmedAt, audits[0].RecordedAt);
        Assert.Null(audits[0].Reason);
        Assert.Equal(FinancialAuditAction.Reversal, audits[1].Action);
        Assert.Equal(AllocationIdempotencyOperation.Reversal, audits[1].IdempotencyOperation);
        Assert.Equal(reversalKey, audits[1].IdempotencyKey);
        Assert.Equal(40m, audits[1].Amount.Amount);
        Assert.Equal("reversal-operator", audits[1].ActorId);
        Assert.Equal(ReversedAt, audits[1].RecordedAt);
        Assert.Equal("duplicate payment", audits[1].Reason);
        Assert.Null(audits[1].BatchExecutionId);
    }

    [Fact]
    public async Task Reversal_restores_only_the_selected_allocation_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        (InvoiceId invoiceId, BankTransactionId bankId) = await SeedPairAsync(organizationId, 100m, 100m);
        AllocationId selected = await AddAllocationOnPairAsync(
            organizationId,
            invoiceId,
            bankId,
            40m,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount,
            confirmationAudit: false);
        AllocationId other = await AddAllocationOnPairAsync(
            organizationId,
            invoiceId,
            bankId,
            25m,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount,
            confirmationAudit: false);

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            organizationId,
            selected,
            "partial book",
            Guid.NewGuid());

        Assert.True(result.IsSuccess);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankId);
        Allocation remaining = await context.Allocations.SingleAsync(candidate => candidate.Id == other);
        Assert.Equal(25m, invoice.AllocatedAmount.Amount);
        Assert.Equal(75m, invoice.OutstandingAmount.Amount);
        Assert.Equal(25m, bank.AllocatedAmount.Amount);
        Assert.Equal(75m, bank.UnappliedAmount.Amount);
        Assert.Equal(AllocationStatus.Confirmed, remaining.Status);
        Assert.Equal(25m, remaining.Amount.Amount);
    }

    [Fact]
    public async Task Historical_allocation_without_confirmation_audit_can_be_reversed()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(
            organizationId,
            80m,
            80m,
            80m,
            confirmationKey: null,
            operation: null,
            confirmationAudit: false);

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            organizationId,
            allocationId,
            "opening correction",
            Guid.NewGuid());

        Assert.True(result.IsSuccess);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Null(allocation.IdempotencyKey);
        Assert.Null(allocation.IdempotencyOperation);
        Assert.Equal(FinancialAuditAction.Reversal, audit.Action);
        Assert.Equal(0m, (await context.Invoices.SingleAsync(invoice => invoice.Id == allocation.InvoiceId))
            .AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Same_key_replay_preserves_time_actor_and_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(organizationId, 50m, 50m, 50m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, true);
        Guid reversalKey = Guid.NewGuid();
        Result<ReverseAllocationResponse> first = await ReverseAsync(
            organizationId,
            allocationId,
            "customer refund",
            reversalKey,
            actorId: "operator-a");
        Assert.True(first.IsSuccess);

        await using ReconciliationDbContext before = _fixture.CreateContext();
        Allocation stored = await before.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        decimal allocated = (await before.Invoices.SingleAsync(invoice => invoice.Id == stored.InvoiceId)).AllocatedAmount.Amount;
        int audits = await before.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId);

        Result<ReverseAllocationResponse> replay = await ReverseAsync(
            organizationId,
            allocationId,
            "customer refund",
            reversalKey,
            actorId: "operator-b");

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(first.Value.ReversedAt, replay.Value.ReversedAt);
        Assert.Equal("customer refund", replay.Value.Reason);
        await using ReconciliationDbContext after = _fixture.CreateContext();
        Allocation allocation = await after.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        FinancialAuditEntry reversal = await after.FinancialAuditEntries.SingleAsync(
            entry => entry.AllocationId == allocationId && entry.Action == FinancialAuditAction.Reversal);
        Assert.Equal(allocated, (await after.Invoices.SingleAsync(invoice => invoice.Id == allocation.InvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(audits, await after.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Assert.Equal(ReversedAt, allocation.ReversedAt);
        Assert.Equal("operator-a", reversal.ActorId);
        Assert.Equal("customer refund", allocation.ReversalReason);
    }

    [Fact]
    public async Task Idempotency_conflicts_do_not_mutate_the_original_reversal()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId first = await AddAllocationAsync(organizationId, 100m, 100m, 30m, Guid.NewGuid(), AllocationIdempotencyOperation.ExplicitAmount, false);
        AllocationId second = await AddAllocationAsync(organizationId, 90m, 90m, 20m, Guid.NewGuid(), AllocationIdempotencyOperation.ExplicitAmount, false);
        Guid reversalKey = Guid.NewGuid();
        Assert.True((await ReverseAsync(organizationId, first, "original reason", reversalKey)).IsSuccess);

        Result<ReverseAllocationResponse> differentReason = await ReverseAsync(
            organizationId,
            first,
            "other reason",
            reversalKey);
        Result<ReverseAllocationResponse> differentAllocation = await ReverseAsync(
            organizationId,
            second,
            "original reason",
            reversalKey);
        Result<ReverseAllocationResponse> differentKey = await ReverseAsync(
            organizationId,
            first,
            "original reason",
            Guid.NewGuid());

        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code,
            differentReason.Error?.Code);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code,
            differentAllocation.Error?.Code);
        Assert.Equal(ReconciliationErrors.AllocationAlreadyReversed.Code, differentKey.Error?.Code);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation original = await context.Allocations.SingleAsync(candidate => candidate.Id == first);
        Allocation untouched = await context.Allocations.SingleAsync(candidate => candidate.Id == second);
        Assert.Equal("original reason", original.ReversalReason);
        Assert.Equal(ReversedAt, original.ReversedAt);
        Assert.Equal(reversalKey, ReversalKey(context, original));
        Assert.Equal(AllocationStatus.Confirmed, untouched.Status);
        Assert.Null(ReversalKey(context, untouched));
        Assert.Equal(1, await context.FinancialAuditEntries.CountAsync(
            entry => entry.AllocationId == first && entry.Action == FinancialAuditAction.Reversal));
        Assert.Equal(20m, (await context.Invoices.SingleAsync(invoice => invoice.Id == untouched.InvoiceId)).AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Confirmation_key_can_be_reused_as_the_reversal_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid sharedKey = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        AllocationId allocationId = await AddAllocationAsync(
            organizationId,
            40m,
            40m,
            40m,
            sharedKey,
            AllocationIdempotencyOperation.ExactMatch,
            confirmationAudit: true);

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            organizationId,
            allocationId,
            "same namespace key",
            sharedKey);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Replayed);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(sharedKey, allocation.IdempotencyKey);
        Assert.Equal(sharedKey, ReversalKey(context, allocation));
        Assert.Equal(2, await context.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId));
    }

    [Fact]
    public async Task Another_organization_cannot_reverse_the_allocation()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(owner, 15m, 15m, 15m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            other,
            allocationId,
            "foreign attempt",
            Guid.NewGuid());

        Assert.Equal(ReconciliationApplicationErrors.AllocationNotFound.Code, result.Error?.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(15m, (await context.Invoices.SingleAsync(invoice => invoice.Id == allocation.InvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(0, await context.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == owner));
    }

    [Fact]
    public async Task Stale_financial_rowversions_reject_the_reversal_and_roll_back()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(organizationId, 22m, 22m, 22m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);
        await using ReconciliationDbContext before = _fixture.CreateContext();
        Allocation allocation = await before.Allocations.SingleAsync(candidate => candidate.Id == allocationId);

        Result<ReverseAllocationResponse> staleAllocation = await ReverseAsync(
            organizationId,
            allocationId,
            "stale allocation",
            Guid.NewGuid(),
            unitOfWork: services => new RowVersionBumpUnitOfWork(
                services.GetRequiredService<IUnitOfWork>(),
                _fixture.ConnectionString,
                "Allocations",
                allocation.Id.Value));
        Result<ReverseAllocationResponse> staleInvoice = await ReverseAsync(
            organizationId,
            allocationId,
            "stale invoice",
            Guid.NewGuid(),
            unitOfWork: services => new RowVersionBumpUnitOfWork(
                services.GetRequiredService<IUnitOfWork>(),
                _fixture.ConnectionString,
                "Invoices",
                allocation.InvoiceId.Value));
        Result<ReverseAllocationResponse> staleBank = await ReverseAsync(
            organizationId,
            allocationId,
            "stale bank",
            Guid.NewGuid(),
            unitOfWork: services => new RowVersionBumpUnitOfWork(
                services.GetRequiredService<IUnitOfWork>(),
                _fixture.ConnectionString,
                "BankTransactions",
                allocation.BankTransactionId.Value));

        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, staleAllocation.Error?.Code);
        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, staleInvoice.Error?.Code);
        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, staleBank.Error?.Code);
        await AssertUnreversedAsync(organizationId, allocationId, 22m);
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_the_entire_reversal()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(organizationId, 33m, 33m, 33m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);

        await using ServiceProvider provider = CreateProvider(duplicateAudit: true);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Result<ReverseAllocationResponse> result = await CreateHandler(scope.ServiceProvider, organizationId)
            .Handle(
                new ReverseAllocationCommand(allocationId, "audit failure", Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, result.Error?.Code);
        await AssertUnreversedAsync(organizationId, allocationId, 33m);
    }

    [Fact]
    public async Task Failed_reversal_is_not_later_treated_as_a_replay()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(organizationId, 18m, 18m, 18m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);
        Guid reversalKey = Guid.NewGuid();
        await using ReconciliationDbContext before = _fixture.CreateContext();
        Guid invoiceId = (await before.Allocations.SingleAsync(candidate => candidate.Id == allocationId)).InvoiceId.Value;

        Result<ReverseAllocationResponse> failed = await ReverseAsync(
            organizationId,
            allocationId,
            "retry me",
            reversalKey,
            unitOfWork: services => new RowVersionBumpUnitOfWork(
                services.GetRequiredService<IUnitOfWork>(),
                _fixture.ConnectionString,
                "Invoices",
                invoiceId));
        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, failed.Error?.Code);
        await AssertUnreversedAsync(organizationId, allocationId, 18m);

        Result<ReverseAllocationResponse> retried = await ReverseAsync(
            organizationId,
            allocationId,
            "retry me",
            reversalKey);

        Assert.True(retried.IsSuccess);
        Assert.False(retried.Value.Replayed);
        await using ReconciliationDbContext after = _fixture.CreateContext();
        Assert.Equal(1, await after.FinancialAuditEntries.CountAsync(
            entry => entry.AllocationId == allocationId && entry.Action == FinancialAuditAction.Reversal));
    }

    [Fact]
    public async Task Inconsistent_reversal_records_are_not_replayed_as_success()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId missingAudit = await AddAllocationAsync(organizationId, 12m, 12m, 12m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);
        Guid missingKey = Guid.NewGuid();
        await MarkReversedWithoutAuditAsync(missingAudit, missingKey, "stored reason");

        Result<ReverseAllocationResponse> missing = await ReverseAsync(
            organizationId,
            missingAudit,
            "stored reason",
            missingKey);

        Assert.Equal(ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code, missing.Error?.Code);
        await using ReconciliationDbContext afterMissing = _fixture.CreateContext();
        Assert.Equal(0, await afterMissing.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == missingAudit));
        Assert.Equal(12m, (await afterMissing.Invoices.SingleAsync(
            invoice => invoice.Id == afterMissing.Allocations.Single(allocation => allocation.Id == missingAudit).InvoiceId))
            .AllocatedAmount.Amount);

        AllocationId wrongAmount = await AddAllocationAsync(organizationId, 16m, 16m, 16m, Guid.NewGuid(), AllocationIdempotencyOperation.ExactMatch, false);
        Guid wrongKey = Guid.NewGuid();
        await MarkReversedWithoutAuditAsync(wrongAmount, wrongKey, "amount mismatch");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == wrongAmount);
            await InsertReversalAuditAsync(
                allocation,
                wrongKey,
                reason: "amount mismatch",
                recordedAt: ReversedAt,
                invoiceId: allocation.InvoiceId.Value,
                bankTransactionId: allocation.BankTransactionId.Value,
                amount: 1m);
        }

        Result<ReverseAllocationResponse> mismatched = await ReverseAsync(
            organizationId,
            wrongAmount,
            "amount mismatch",
            wrongKey);

        Assert.Equal(ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code, mismatched.Error?.Code);
        await using ReconciliationDbContext afterMismatch = _fixture.CreateContext();
        Assert.Equal(1, await afterMismatch.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == wrongAmount));
        Assert.Equal(16m, (await afterMismatch.Invoices.SingleAsync(
            invoice => invoice.Id == afterMismatch.Allocations.Single(allocation => allocation.Id == wrongAmount).InvoiceId))
            .AllocatedAmount.Amount);
    }

    [Fact]
    public async Task Reversal_audit_metadata_that_disagrees_with_the_allocation_is_not_a_replay()
    {
        await AssertAuditDisagreementAsync(
            (allocation, key) => InsertReversalAuditAsync(
                allocation,
                key,
                reason: "different audit reason",
                recordedAt: ReversedAt,
                invoiceId: allocation.InvoiceId.Value,
                bankTransactionId: allocation.BankTransactionId.Value,
                amount: allocation.Amount.Amount));

        OrganizationId timedOrganization = OrganizationId.New();
        await AssertAuditDisagreementAsync(
            (allocation, key) => InsertReversalAuditAsync(
                allocation,
                key,
                reason: "stored reason",
                recordedAt: ReversedAt.AddMinutes(5),
                invoiceId: allocation.InvoiceId.Value,
                bankTransactionId: allocation.BankTransactionId.Value,
                amount: allocation.Amount.Amount),
            timedOrganization);

        OrganizationId pairedOrganization = OrganizationId.New();
        (InvoiceId otherInvoiceId, BankTransactionId otherBankId) = await SeedPairAsync(pairedOrganization, 9m, 9m);
        await AssertAuditDisagreementAsync(
            (allocation, key) => InsertReversalAuditAsync(
                allocation,
                key,
                reason: "stored reason",
                recordedAt: ReversedAt,
                invoiceId: otherInvoiceId.Value,
                bankTransactionId: otherBankId.Value,
                amount: allocation.Amount.Amount),
            pairedOrganization);
    }

    [Fact]
    public async Task Confirmed_allocation_with_only_a_reversal_key_is_an_integrity_conflict()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(
            organizationId,
            11m,
            11m,
            11m,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch,
            confirmationAudit: false);
        Guid reversalKey = Guid.NewGuid();
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
            await context.SaveChangesAsync();
        }

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            organizationId,
            allocationId,
            "customer refund",
            reversalKey);

        Assert.Equal(ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code, result.Error?.Code);
        await using ReconciliationDbContext verify = _fixture.CreateContext();
        Allocation stored = await verify.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(AllocationStatus.Confirmed, stored.Status);
        Assert.Null(stored.ReversalReason);
        Assert.Null(stored.ReversedAt);
        Assert.Equal(reversalKey, ReversalKey(verify, stored));
        Assert.Equal(11m, (await verify.Invoices.SingleAsync(invoice => invoice.Id == stored.InvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(0, await verify.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId));
    }

    private async Task<Result<ReverseAllocationResponse>> ReverseAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        string reason,
        Guid reversalKey,
        string actorId = TestCurrentActorContext.DefaultActorId,
        Func<IServiceProvider, IUnitOfWork>? unitOfWork = null)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ReverseAllocationHandler handler = CreateHandler(
            scope.ServiceProvider,
            organizationId,
            actorId,
            unitOfWork?.Invoke(scope.ServiceProvider));
        return await handler.Handle(
            new ReverseAllocationCommand(allocationId, reason, reversalKey),
            CancellationToken.None);
    }

    private static ReverseAllocationHandler CreateHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string actorId = TestCurrentActorContext.DefaultActorId,
        IUnitOfWork? unitOfWork = null)
    {
        return new ReverseAllocationHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(actorId),
            services.GetRequiredService<IAllocationRepository>(),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IAllocationReversalLookup>(),
            services.GetRequiredService<IAllocationReversalIdempotencyConflict>(),
            services.GetRequiredService<IFinancialAuditWriter>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(ReversedAt));
    }

    private ServiceProvider CreateProvider(bool duplicateAudit)
    {
        ServiceCollection services = [];
        services.AddReconciliationModule(_fixture.ConnectionString);
        if (duplicateAudit)
        {
            services.AddScoped<IFinancialAuditWriter, DuplicatingReversalAuditWriter>();
        }

        return services.BuildServiceProvider();
    }

    private async Task<AllocationId> AddAllocationAsync(
        OrganizationId organizationId,
        decimal invoiceTotal,
        decimal bankTotal,
        decimal amount,
        Guid? confirmationKey,
        AllocationIdempotencyOperation? operation,
        bool confirmationAudit,
        string confirmationActor = "confirm-operator")
    {
        (InvoiceId invoiceId, BankTransactionId bankId) = await SeedPairAsync(organizationId, invoiceTotal, bankTotal);
        return await AddAllocationOnPairAsync(
            organizationId,
            invoiceId,
            bankId,
            amount,
            confirmationKey,
            operation,
            confirmationAudit,
            confirmationActor);
    }

    private async Task<(InvoiceId InvoiceId, BankTransactionId BankTransactionId)> SeedPairAsync(
        OrganizationId organizationId,
        decimal invoiceTotal,
        decimal bankTotal)
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, invoiceTotal, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            bankTotal,
            reference: number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        return (invoice.Id, bank.Id);
    }

    private async Task<AllocationId> AddAllocationOnPairAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankId,
        decimal amount,
        Guid? confirmationKey,
        AllocationIdempotencyOperation? operation,
        bool confirmationAudit,
        string confirmationActor = "confirm-operator")
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankId);
        var money = new Money(amount, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(invoice, bank, money).IsSuccess);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            money,
            ConfirmedAt,
            confirmationKey,
            operation);
        context.Allocations.Add(allocation);
        if (confirmationAudit)
        {
            new FinancialAuditWriter(context).AppendConfirmation(allocation, confirmationActor, batchExecutionId: null);
        }

        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task MarkReversedWithoutAuditAsync(AllocationId allocationId, Guid reversalKey, string reason)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.True(allocation.Reverse(reason, ReversedAt).IsSuccess);
        AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
        await context.SaveChangesAsync();
    }

    private async Task AssertAuditDisagreementAsync(
        Func<Allocation, Guid, Task> insertAudit,
        OrganizationId? organizationId = null)
    {
        OrganizationId owner = organizationId ?? OrganizationId.New();
        AllocationId allocationId = await AddAllocationAsync(
            owner,
            14m,
            14m,
            14m,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch,
            confirmationAudit: false);
        Guid reversalKey = Guid.NewGuid();
        await MarkReversedWithoutAuditAsync(allocationId, reversalKey, "stored reason");
        FinancialAuditEntry before;
        decimal allocatedBefore;
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            await insertAudit(allocation, reversalKey);
        }

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            before = await context.FinancialAuditEntries.SingleAsync(entry => entry.AllocationId == allocationId);
            allocatedBefore = (await context.Invoices.SingleAsync(
                invoice => invoice.Id == context.Allocations.Single(candidate => candidate.Id == allocationId).InvoiceId))
                .AllocatedAmount.Amount;
        }

        Result<ReverseAllocationResponse> result = await ReverseAsync(
            owner,
            allocationId,
            "stored reason",
            reversalKey);

        Assert.Equal(ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code, result.Error?.Code);
        await using ReconciliationDbContext after = _fixture.CreateContext();
        Allocation stored = await after.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        FinancialAuditEntry audit = await after.FinancialAuditEntries.SingleAsync(
            entry => entry.AllocationId == allocationId);
        Assert.Equal(AllocationStatus.Reversed, stored.Status);
        Assert.Equal("stored reason", stored.ReversalReason);
        Assert.Equal(ReversedAt, stored.ReversedAt);
        Assert.Equal(reversalKey, ReversalKey(after, stored));
        Assert.Equal(allocatedBefore, (await after.Invoices.SingleAsync(invoice => invoice.Id == stored.InvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(before.Reason, audit.Reason);
        Assert.Equal(before.RecordedAt, audit.RecordedAt);
        Assert.Equal(before.InvoiceId, audit.InvoiceId);
        Assert.Equal(before.BankTransactionId, audit.BankTransactionId);
        Assert.Equal(before.Amount, audit.Amount);
        Assert.Equal(1, await after.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId));
    }

    private async Task InsertReversalAuditAsync(
        Allocation allocation,
        Guid reversalKey,
        string reason,
        DateTimeOffset recordedAt,
        Guid invoiceId,
        Guid bankTransactionId,
        decimal amount)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency], [Reason])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 NULL, 2, N'raw-operator', @recordedAt, 4, @idempotencyKey, @amount, N'PLN', @reason)
            """;
        command.Parameters.Add(new SqlParameter("@id", Guid.NewGuid()));
        command.Parameters.Add(new SqlParameter("@organizationId", allocation.OrganizationId.Value));
        command.Parameters.Add(new SqlParameter("@allocationId", allocation.Id.Value));
        command.Parameters.Add(new SqlParameter("@invoiceId", invoiceId));
        command.Parameters.Add(new SqlParameter("@bankTransactionId", bankTransactionId));
        command.Parameters.Add(new SqlParameter("@recordedAt", recordedAt));
        command.Parameters.Add(new SqlParameter("@idempotencyKey", reversalKey));
        SqlParameter amountParameter = command.Parameters.Add("@amount", System.Data.SqlDbType.Decimal);
        amountParameter.Precision = 19;
        amountParameter.Scale = 4;
        amountParameter.Value = amount;
        command.Parameters.Add(new SqlParameter("@reason", reason));
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertUnreversedAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        decimal allocated)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == allocation.BankTransactionId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Assert.Null(allocation.ReversedAt);
        Assert.Null(ReversalKey(context, allocation));
        Assert.Equal(allocated, invoice.AllocatedAmount.Amount);
        Assert.Equal(allocated, bank.AllocatedAmount.Amount);
        Assert.Equal(0, await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId && entry.Action == FinancialAuditAction.Reversal));
    }

    private static Guid? ReversalKey(ReconciliationDbContext context, Allocation allocation)
    {
        return context.Entry(allocation)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue;
    }

    private sealed class DuplicatingReversalAuditWriter : IFinancialAuditWriter
    {
        private readonly ReconciliationDbContext _dbContext;

        public DuplicatingReversalAuditWriter(ReconciliationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public void AppendConfirmation(
            Allocation allocation,
            string actorId,
            BatchExecutionId? batchExecutionId)
        {
            throw new NotSupportedException();
        }

        public void AppendReversal(Allocation allocation, string actorId, Guid reversalIdempotencyKey)
        {
            _dbContext.FinancialAuditEntries.Add(FinancialAuditEntry.ForReversal(
                FinancialAuditEntryId.New(),
                allocation,
                actorId,
                reversalIdempotencyKey));
            _dbContext.FinancialAuditEntries.Add(FinancialAuditEntry.ForReversal(
                FinancialAuditEntryId.New(),
                allocation,
                actorId,
                reversalIdempotencyKey));
        }
    }

    private sealed class RowVersionBumpUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly string _connectionString;
        private readonly string _table;
        private readonly Guid _id;

        public RowVersionBumpUnitOfWork(IUnitOfWork inner, string connectionString, string table, Guid id)
        {
            _inner = inner;
            _connectionString = connectionString;
            _table = table;
            _id = id;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = _table switch
            {
                "Allocations" => "UPDATE [reconciliation].[Allocations] SET [Status] = [Status] WHERE [Id] = @id",
                "Invoices" => "UPDATE [reconciliation].[Invoices] SET [InvoiceNumber] = [InvoiceNumber] WHERE [Id] = @id",
                "BankTransactions" => "UPDATE [reconciliation].[BankTransactions] SET [Reference] = [Reference] WHERE [Id] = @id",
                _ => throw new InvalidOperationException("Unexpected table."),
            };
            command.Parameters.Add(new SqlParameter("@id", _id));
            int updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException("Expected exactly one rowversion bump.");
            }

            await _inner.SaveChangesAsync(cancellationToken);
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
}