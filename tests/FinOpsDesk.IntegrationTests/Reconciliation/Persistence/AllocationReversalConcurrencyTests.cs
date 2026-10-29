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
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalConcurrencyTests
{
    private const decimal ExistingAmount = 40m;

    private const decimal NewAmount = 60m;

    private const string ConfirmationActor = "confirm-operator";

    private const string ConfirmationRacer = "confirm-racer";

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset FirstReversedAt = ConfirmedAt.AddHours(2);

    private static readonly DateTimeOffset SecondReversedAt = ConfirmedAt.AddHours(3);

    private static readonly DateTimeOffset CompetingConfirmedAt = ConfirmedAt.AddHours(4);

    private readonly SqlServerFixture _fixture;

    public AllocationReversalConcurrencyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Same_allocation_key_and_reason_commits_once_and_replays_the_loser()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid confirmationKey = Guid.NewGuid();
        AllocationId allocationId = await SeedFullAllocationAsync(organizationId, confirmationKey, 40m);
        Guid reversalKey = Guid.NewGuid();
        ReversalAttempt[] attempts =
        [
            new("actor-alpha", "customer refund", reversalKey, FirstReversedAt),
            new("actor-beta", "customer refund", reversalKey, SecondReversedAt),
        ];

        ReversalAttemptResult[] outcomes = await RaceReversalsAsync(organizationId, allocationId, attempts);

        ReversalAttemptResult winner = Assert.Single(outcomes, IsFreshSuccess);
        ReversalAttemptResult replay = Assert.Single(outcomes, IsReplay);
        Assert.Equal(winner.Attempt.ReversedAt, replay.Result.Value!.ReversedAt);
        Assert.Equal(winner.Attempt.Reason, replay.Result.Value.Reason);
        Assert.Equal(allocationId.Value, replay.Result.Value.AllocationId);
        await AssertSingleReversalAsync(organizationId, allocationId, confirmationKey, winner.Attempt, allocated: 0m);
    }

    [Fact]
    public async Task Same_allocation_and_key_with_different_reasons_keeps_the_winner()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid confirmationKey = Guid.NewGuid();
        AllocationId allocationId = await SeedFullAllocationAsync(organizationId, confirmationKey, 40m);
        Guid reversalKey = Guid.NewGuid();
        ReversalAttempt[] attempts =
        [
            new("actor-alpha", "alpha reason", reversalKey, FirstReversedAt),
            new("actor-beta", "beta reason", reversalKey, SecondReversedAt),
        ];

        ReversalAttemptResult[] outcomes = await RaceReversalsAsync(organizationId, allocationId, attempts);

        ReversalAttemptResult winner = Assert.Single(outcomes, IsFreshSuccess);
        ReversalAttemptResult loser = Assert.Single(outcomes, outcome => outcome.Result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code,
            loser.Result.Error?.Code);
        await AssertSingleReversalAsync(organizationId, allocationId, confirmationKey, winner.Attempt, allocated: 0m);
    }

    [Fact]
    public async Task Same_allocation_with_different_keys_keeps_one_reversal()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid confirmationKey = Guid.NewGuid();
        AllocationId allocationId = await SeedFullAllocationAsync(organizationId, confirmationKey, 40m);
        ReversalAttempt[] attempts =
        [
            new("actor-alpha", "alpha reason", Guid.NewGuid(), FirstReversedAt),
            new("actor-beta", "beta reason", Guid.NewGuid(), SecondReversedAt),
        ];

        ReversalAttemptResult[] outcomes = await RaceReversalsAsync(organizationId, allocationId, attempts);

        ReversalAttemptResult winner = Assert.Single(outcomes, IsFreshSuccess);
        ReversalAttemptResult loser = Assert.Single(outcomes, outcome => outcome.Result.IsFailure);
        Assert.Equal(ReconciliationErrors.AllocationAlreadyReversed.Code, loser.Result.Error?.Code);
        await AssertSingleReversalAsync(organizationId, allocationId, confirmationKey, winner.Attempt, allocated: 0m);
    }

    [Theory]
    [InlineData("invoice", true)]
    [InlineData("invoice", false)]
    [InlineData("bank", true)]
    [InlineData("bank", false)]
    [InlineData("both", true)]
    [InlineData("both", false)]
    public async Task Stale_shared_rowversion_commits_only_the_designated_writer(
        string sharedSide,
        bool reversalWins)
    {
        CompetingBooks books = await SeedCompetitionAsync(sharedSide);
        Guid confirmationKey = Guid.NewGuid();
        ReversalAttempt reversal = new("reverse-racer", "shared row reversal", Guid.NewGuid(), FirstReversedAt);

        (Result<ReverseAllocationResponse> reversalResult, Result<ConfirmExplicitAmountResponse> confirmationResult) =
            await RaceAgainstConfirmationAsync(books, reversal, confirmationKey, reversalWins);

        if (reversalWins)
        {
            AssertFreshReversal(reversalResult, books.ExistingAllocationId, reversal);
            Assert.Equal(
                ReconciliationApplicationErrors.ConcurrencyConflict.Code,
                confirmationResult.Error?.Code);
            await AssertReversalWonAsync(books, reversal, confirmationKey);
            return;
        }

        Assert.Equal(
            ReconciliationApplicationErrors.ConcurrencyConflict.Code,
            reversalResult.Error?.Code);
        AssertFreshConfirmation(confirmationResult, books);
        await AssertConfirmationWonAsync(books, confirmationKey);
    }

    [Fact]
    public async Task Independent_reversal_and_confirmation_both_commit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid existingKey = Guid.NewGuid();
        Guid confirmationKey = Guid.NewGuid();
        (AllocationId allocationId, InvoiceId invoiceA, BankTransactionId bankA, InvoiceId invoiceB, BankTransactionId bankB) =
            await SeedIndependentPairsAsync(organizationId, existingKey);
        ReversalAttempt reversal = new("reverse-racer", "independent reversal", Guid.NewGuid(), FirstReversedAt);
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using ServiceProvider provider = _fixture.CreateServiceProvider();

        Task<Result<ReverseAllocationResponse>> reversalTask = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ReverseAllocationHandler handler = CreateReversalHandler(
                    scope.ServiceProvider,
                    organizationId,
                    reversal.ActorId,
                    reversal.ReversedAt,
                    new SimultaneousUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), gate));
                return await handler.Handle(
                    new ReverseAllocationCommand(allocationId, reversal.Reason, reversal.Key),
                    timeout.Token);
            },
            timeout.Token);
        Task<Result<ConfirmExplicitAmountResponse>> confirmationTask = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ConfirmExplicitAmountHandler handler = CreateConfirmationHandler(
                    scope.ServiceProvider,
                    organizationId,
                    new SimultaneousUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), gate));
                return await handler.Handle(
                    new ConfirmExplicitAmountCommand(
                        invoiceB,
                        bankB,
                        new Money(25m, PersistenceTestData.Pln),
                        confirmationKey),
                    timeout.Token);
            },
            timeout.Token);

        await Task.WhenAll(reversalTask, confirmationTask);

        AssertFreshReversal(await reversalTask, allocationId, reversal);
        Result<ConfirmExplicitAmountResponse> confirmation = await confirmationTask;
        Assert.True(confirmation.IsSuccess);
        Assert.False(confirmation.Value.Replayed);
        Assert.Equal(invoiceB.Value, confirmation.Value.InvoiceId);
        Assert.Equal(bankB.Value, confirmation.Value.BankTransactionId);
        await AssertIndependentCommitAsync(
            organizationId,
            allocationId,
            existingKey,
            invoiceA,
            bankA,
            invoiceB,
            bankB,
            confirmation.Value.AllocationId,
            confirmationKey,
            reversal);
    }

    private async Task AssertSingleReversalAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid confirmationKey,
        ReversalAttempt winner,
        decimal allocated)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == allocation.BankTransactionId);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        FinancialAuditEntry reversal = Assert.Single(
            audits,
            entry => entry.Action == FinancialAuditAction.Reversal);
        FinancialAuditEntry confirmation = Assert.Single(
            audits,
            entry => entry.Action == FinancialAuditAction.Confirmation);

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(winner.Key, ReversalKey(context, allocation));
        Assert.Equal(confirmationKey, allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, allocation.IdempotencyOperation);
        Assert.Equal(ConfirmedAt, allocation.ConfirmedAt);
        Assert.Equal(winner.ReversedAt, allocation.ReversedAt);
        Assert.Equal(winner.Reason, allocation.ReversalReason);
        Assert.Equal(40m, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(allocated, invoice.AllocatedAmount.Amount);
        Assert.Equal("PLN", invoice.AllocatedAmount.Currency.Value);
        Assert.Equal(allocated, bank.AllocatedAmount.Amount);
        Assert.Equal("PLN", bank.AllocatedAmount.Currency.Value);
        Assert.Equal(2, audits.Count);
        Assert.Equal(winner.ActorId, reversal.ActorId);
        Assert.Equal(winner.ReversedAt, reversal.RecordedAt);
        Assert.Equal(winner.Reason, reversal.Reason);
        Assert.Equal(winner.Key, reversal.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.Reversal, reversal.IdempotencyOperation);
        Assert.Equal(allocation.Amount, reversal.Amount);
        Assert.Equal(allocation.InvoiceId, reversal.InvoiceId);
        Assert.Equal(allocation.BankTransactionId, reversal.BankTransactionId);
        Assert.Equal(ConfirmationActor, confirmation.ActorId);
        Assert.Equal(ConfirmedAt, confirmation.RecordedAt);
        Assert.Null(confirmation.Reason);
        Assert.Equal(confirmationKey, confirmation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, confirmation.IdempotencyOperation);
        Assert.Equal(1, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == organizationId));
    }

    private async Task AssertReversalWonAsync(
        CompetingBooks books,
        ReversalAttempt winner,
        Guid losingConfirmationKey)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(
            candidate => candidate.Id == books.ExistingAllocationId);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == books.OrganizationId)
            .ToListAsync();

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(winner.Key, ReversalKey(context, allocation));
        Assert.Equal(books.ExistingConfirmationKey, allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, allocation.IdempotencyOperation);
        Assert.Equal(ConfirmedAt, allocation.ConfirmedAt);
        Assert.Equal(winner.ReversedAt, allocation.ReversedAt);
        Assert.Equal(winner.Reason, allocation.ReversalReason);
        Assert.Equal(0, await context.Allocations.CountAsync(
            candidate => candidate.OrganizationId == books.OrganizationId && candidate.IdempotencyKey == losingConfirmationKey));
        Assert.Equal(1, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == books.OrganizationId));
        FinancialAuditEntry reversal = Assert.Single(audits, entry => entry.Action == FinancialAuditAction.Reversal);
        FinancialAuditEntry confirmation = Assert.Single(audits, entry => entry.Action == FinancialAuditAction.Confirmation);
        Assert.Equal(winner.ActorId, reversal.ActorId);
        Assert.Equal(winner.ReversedAt, reversal.RecordedAt);
        Assert.Equal(winner.Reason, reversal.Reason);
        Assert.Equal(winner.Key, reversal.IdempotencyKey);
        Assert.Equal(ConfirmationActor, confirmation.ActorId);
        Assert.Equal(books.ExistingConfirmationKey, confirmation.IdempotencyKey);
        Assert.Null(confirmation.Reason);
        await AssertBalancesAsync(context, books, reversalWins: true);
    }

    private async Task AssertConfirmationWonAsync(CompetingBooks books, Guid confirmationKey)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation original = await context.Allocations.SingleAsync(candidate => candidate.Id == books.ExistingAllocationId);
        Allocation created = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == books.OrganizationId && candidate.IdempotencyKey == confirmationKey);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == books.OrganizationId)
            .ToListAsync();

        Assert.Equal(AllocationStatus.Confirmed, original.Status);
        Assert.Null(original.ReversalReason);
        Assert.Null(original.ReversedAt);
        Assert.Null(ReversalKey(context, original));
        Assert.Equal(books.ExistingConfirmationKey, original.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, original.IdempotencyOperation);
        Assert.Equal(ConfirmedAt, original.ConfirmedAt);
        Assert.Equal(AllocationStatus.Confirmed, created.Status);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, created.IdempotencyOperation);
        Assert.Equal(NewAmount, created.Amount.Amount);
        Assert.Equal("PLN", created.Amount.Currency.Value);
        Assert.Equal(books.SecondInvoiceId, created.InvoiceId);
        Assert.Equal(books.SecondBankId, created.BankTransactionId);
        Assert.Equal(2, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == books.OrganizationId));
        Assert.Equal(2, audits.Count(entry => entry.Action == FinancialAuditAction.Confirmation));
        Assert.DoesNotContain(audits, entry => entry.Action == FinancialAuditAction.Reversal);
        FinancialAuditEntry originalAudit = Assert.Single(
            audits,
            entry => entry.AllocationId == original.Id);
        FinancialAuditEntry createdAudit = Assert.Single(
            audits,
            entry => entry.AllocationId == created.Id);
        Assert.Equal(ConfirmationActor, originalAudit.ActorId);
        Assert.Equal(books.ExistingConfirmationKey, originalAudit.IdempotencyKey);
        Assert.Null(originalAudit.Reason);
        Assert.Equal(ConfirmedAt, originalAudit.RecordedAt);
        Assert.Equal(ConfirmationRacer, createdAudit.ActorId);
        Assert.Equal(confirmationKey, createdAudit.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, createdAudit.IdempotencyOperation);
        Assert.Equal(CompetingConfirmedAt, createdAudit.RecordedAt);
        Assert.Null(createdAudit.Reason);
        Assert.Equal(created.Amount, createdAudit.Amount);
        await AssertBalancesAsync(context, books, reversalWins: false);
    }

    private static async Task AssertBalancesAsync(
        ReconciliationDbContext context,
        CompetingBooks books,
        bool reversalWins)
    {
        decimal invoiceA = reversalWins ? 0m : books.SecondInvoiceId == books.FirstInvoiceId ? ExistingAmount + NewAmount : ExistingAmount;
        decimal invoiceB = reversalWins ? 0m : NewAmount;
        decimal bankA = reversalWins ? 0m : books.SecondBankId == books.FirstBankId ? ExistingAmount + NewAmount : ExistingAmount;
        decimal bankB = reversalWins ? 0m : NewAmount;
        Assert.Equal(invoiceA, (await context.Invoices.SingleAsync(candidate => candidate.Id == books.FirstInvoiceId)).AllocatedAmount.Amount);
        Assert.Equal(bankA, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == books.FirstBankId)).AllocatedAmount.Amount);
        if (books.SecondInvoiceId != books.FirstInvoiceId)
        {
            Assert.Equal(invoiceB, (await context.Invoices.SingleAsync(candidate => candidate.Id == books.SecondInvoiceId)).AllocatedAmount.Amount);
        }

        if (books.SecondBankId != books.FirstBankId)
        {
            Assert.Equal(bankB, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == books.SecondBankId)).AllocatedAmount.Amount);
        }
    }

    private async Task AssertIndependentCommitAsync(
        OrganizationId organizationId,
        AllocationId reversedId,
        Guid existingKey,
        InvoiceId invoiceA,
        BankTransactionId bankA,
        InvoiceId invoiceB,
        BankTransactionId bankB,
        Guid createdAllocationId,
        Guid confirmationKey,
        ReversalAttempt reversal)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation reversed = await context.Allocations.SingleAsync(candidate => candidate.Id == reversedId);
        Allocation created = await context.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.IdempotencyKey == confirmationKey);
        Assert.Equal(createdAllocationId, created.Id.Value);
        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        FinancialAuditEntry reversalAudit = Assert.Single(
            audits,
            entry => entry.Action == FinancialAuditAction.Reversal);

        Assert.Equal(AllocationStatus.Reversed, reversed.Status);
        Assert.Equal(reversal.Key, ReversalKey(context, reversed));
        Assert.Equal(existingKey, reversed.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, reversed.IdempotencyOperation);
        Assert.Equal(reversal.ReversedAt, reversed.ReversedAt);
        Assert.Equal(reversal.Reason, reversed.ReversalReason);
        Assert.Equal(AllocationStatus.Confirmed, created.Status);
        Assert.Equal(confirmationKey, created.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, created.IdempotencyOperation);
        Assert.Equal(25m, created.Amount.Amount);
        Assert.Equal(0m, (await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceA)).AllocatedAmount.Amount);
        Assert.Equal(0m, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankA)).AllocatedAmount.Amount);
        Assert.Equal(25m, (await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceB)).AllocatedAmount.Amount);
        Assert.Equal(25m, (await context.BankTransactions.SingleAsync(candidate => candidate.Id == bankB)).AllocatedAmount.Amount);
        Assert.Equal(2, audits.Count(entry => entry.Action == FinancialAuditAction.Confirmation));
        Assert.Equal(reversal.ActorId, reversalAudit.ActorId);
        Assert.Equal(reversal.Key, reversalAudit.IdempotencyKey);
        Assert.Equal(reversal.ReversedAt, reversalAudit.RecordedAt);
        Assert.Equal(reversal.Reason, reversalAudit.Reason);
        Assert.Equal(2, await context.Allocations.CountAsync(candidate => candidate.OrganizationId == organizationId));
    }

    private async Task<AllocationId> SeedFullAllocationAsync(
        OrganizationId organizationId,
        Guid confirmationKey,
        decimal amount)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, "INV-" + suffix);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            amount,
            reference: "INV-" + suffix);
        return await SaveConfirmedAllocationAsync(invoice, bank, amount, confirmationKey);
    }

    private async Task<CompetingBooks> SeedCompetitionAsync(string sharedSide)
    {
        OrganizationId organizationId = OrganizationId.New();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        bool shareInvoice = sharedSide is "invoice" or "both";
        bool shareBank = sharedSide is "bank" or "both";
        Invoice invoiceA = PersistenceTestData.CreateInvoice(
            organizationId,
            shareInvoice ? ExistingAmount + NewAmount : ExistingAmount,
            "IA-" + suffix);
        Invoice? invoiceB = shareInvoice
            ? null
            : PersistenceTestData.CreateInvoice(organizationId, NewAmount, "IB-" + suffix);
        BankTransaction bankA = PersistenceTestData.CreateBankTransaction(
            organizationId,
            shareBank ? ExistingAmount + NewAmount : ExistingAmount,
            reference: "BA-" + suffix);
        BankTransaction? bankB = shareBank
            ? null
            : PersistenceTestData.CreateBankTransaction(organizationId, NewAmount, reference: "BB-" + suffix);
        Guid confirmationKey = Guid.NewGuid();
        AllocationId allocationId = await SaveConfirmedAllocationAsync(
            invoiceA,
            bankA,
            ExistingAmount,
            confirmationKey,
            invoiceB,
            bankB);

        return new CompetingBooks(
            organizationId,
            allocationId,
            confirmationKey,
            invoiceA.Id,
            invoiceB?.Id ?? invoiceA.Id,
            bankA.Id,
            bankB?.Id ?? bankA.Id);
    }

    private async Task<(AllocationId AllocationId, InvoiceId InvoiceA, BankTransactionId BankA, InvoiceId InvoiceB, BankTransactionId BankB)> SeedIndependentPairsAsync(
        OrganizationId organizationId,
        Guid confirmationKey)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationId, 30m, "INA-" + suffix);
        BankTransaction bankA = PersistenceTestData.CreateBankTransaction(organizationId, 30m, reference: "BNA-" + suffix);
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationId, 25m, "INB-" + suffix);
        BankTransaction bankB = PersistenceTestData.CreateBankTransaction(organizationId, 25m, reference: "BNB-" + suffix);
        AllocationId allocationId = await SaveConfirmedAllocationAsync(
            invoiceA,
            bankA,
            30m,
            confirmationKey,
            invoiceB,
            bankB);
        return (allocationId, invoiceA.Id, bankA.Id, invoiceB.Id, bankB.Id);
    }

    private async Task<AllocationId> SaveConfirmedAllocationAsync(
        Invoice invoice,
        BankTransaction bank,
        decimal amount,
        Guid confirmationKey,
        Invoice? extraInvoice = null,
        BankTransaction? extraBank = null)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Add(invoice);
        context.Add(bank);
        if (extraInvoice is not null)
        {
            context.Add(extraInvoice);
        }

        if (extraBank is not null)
        {
            context.Add(extraBank);
        }

        var money = new Money(amount, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(invoice, bank, money).IsSuccess);
        var allocation = new Allocation(
            AllocationId.New(),
            invoice.OrganizationId,
            invoice.Id,
            bank.Id,
            money,
            ConfirmedAt,
            confirmationKey,
            AllocationIdempotencyOperation.ExactMatch);
        context.Allocations.Add(allocation);
        new FinancialAuditWriter(context).AppendConfirmation(allocation, ConfirmationActor, batchExecutionId: null);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task<ReversalAttemptResult[]> RaceReversalsAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        IReadOnlyList<ReversalAttempt> attempts)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(attempts.Count);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        return await Task.WhenAll(attempts.Select(attempt => Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ReverseAllocationHandler handler = CreateReversalHandler(
                    scope.ServiceProvider,
                    organizationId,
                    attempt.ActorId,
                    attempt.ReversedAt,
                    new SimultaneousUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), gate));
                Result<ReverseAllocationResponse> result = await handler.Handle(
                    new ReverseAllocationCommand(allocationId, attempt.Reason, attempt.Key),
                    timeout.Token);
                return new ReversalAttemptResult(attempt, result);
            },
            timeout.Token)));
    }

    private async Task<(Result<ReverseAllocationResponse> Reversal, Result<ConfirmExplicitAmountResponse> Confirmation)> RaceAgainstConfirmationAsync(
        CompetingBooks books,
        ReversalAttempt reversal,
        Guid confirmationKey,
        bool reversalWins)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        OrderedSaveGate gate = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<Result<ReverseAllocationResponse>> reversalTask = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ReverseAllocationHandler handler = CreateReversalHandler(
                    scope.ServiceProvider,
                    books.OrganizationId,
                    reversal.ActorId,
                    reversal.ReversedAt,
                    new OrderedUnitOfWork(
                        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                        gate,
                        reversalWins));
                return await handler.Handle(
                    new ReverseAllocationCommand(books.ExistingAllocationId, reversal.Reason, reversal.Key),
                    timeout.Token);
            },
            timeout.Token);
        Task<Result<ConfirmExplicitAmountResponse>> confirmationTask = Task.Run(
            async () =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                ConfirmExplicitAmountHandler handler = CreateConfirmationHandler(
                    scope.ServiceProvider,
                    books.OrganizationId,
                    new OrderedUnitOfWork(
                        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                        gate,
                        wins: !reversalWins));
                return await handler.Handle(
                    new ConfirmExplicitAmountCommand(
                        books.SecondInvoiceId,
                        books.SecondBankId,
                        new Money(NewAmount, PersistenceTestData.Pln),
                        confirmationKey),
                    timeout.Token);
            },
            timeout.Token);

        await Task.WhenAll(reversalTask, confirmationTask);
        return (await reversalTask, await confirmationTask);
    }

    private static ReverseAllocationHandler CreateReversalHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string actorId,
        DateTimeOffset reversedAt,
        IUnitOfWork unitOfWork)
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
            unitOfWork,
            new FixedTimeProvider(reversedAt));
    }

    private static ConfirmExplicitAmountHandler CreateConfirmationHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        IUnitOfWork unitOfWork)
    {
        return new ConfirmExplicitAmountHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(ConfirmationRacer),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IAllocationRepository>(),
            services.GetRequiredService<IAllocationIdempotencyLookup>(),
            unitOfWork,
            services.GetRequiredService<IFinancialAuditWriter>(),
            services.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(CompetingConfirmedAt));
    }

    private static void AssertFreshReversal(
        Result<ReverseAllocationResponse> result,
        AllocationId allocationId,
        ReversalAttempt attempt)
    {
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(attempt.Reason, result.Value.Reason);
        Assert.Equal(attempt.ReversedAt, result.Value.ReversedAt);
    }

    private static void AssertFreshConfirmation(
        Result<ConfirmExplicitAmountResponse> result,
        CompetingBooks books)
    {
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Replayed);
        Assert.Equal(books.SecondInvoiceId.Value, result.Value.InvoiceId);
        Assert.Equal(books.SecondBankId.Value, result.Value.BankTransactionId);
        Assert.Equal(NewAmount, result.Value.Amount.Amount);
        Assert.Equal("PLN", result.Value.Amount.Currency);
    }

    private static bool IsFreshSuccess(ReversalAttemptResult outcome)
    {
        return outcome.Result.IsSuccess && outcome.Result.Value.Replayed == false;
    }

    private static bool IsReplay(ReversalAttemptResult outcome)
    {
        return outcome.Result.IsSuccess && outcome.Result.Value.Replayed;
    }

    private static Guid? ReversalKey(ReconciliationDbContext context, Allocation allocation)
    {
        return context.Entry(allocation)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue;
    }

    private sealed record ReversalAttempt(
        string ActorId,
        string Reason,
        Guid Key,
        DateTimeOffset ReversedAt);

    private sealed record ReversalAttemptResult(
        ReversalAttempt Attempt,
        Result<ReverseAllocationResponse> Result);

    private sealed record CompetingBooks(
        OrganizationId OrganizationId,
        AllocationId ExistingAllocationId,
        Guid ExistingConfirmationKey,
        InvoiceId FirstInvoiceId,
        InvoiceId SecondInvoiceId,
        BankTransactionId FirstBankId,
        BankTransactionId SecondBankId);

    /// <summary>
    /// Both writers stage their changes, then the designated winner commits before the loser saves its stale rowversion.
    /// </summary>
    private sealed class OrderedSaveGate
    {
        private readonly TaskCompletionSource<bool> _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _winnerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
            {
                _bothArrived.TrySetResult(true);
            }

            await _bothArrived.Task.WaitAsync(cancellationToken);
        }

        public Task WaitForWinnerAsync(CancellationToken cancellationToken)
        {
            return _winnerFinished.Task.WaitAsync(cancellationToken);
        }

        public void SignalWinnerFinished()
        {
            _winnerFinished.TrySetResult(true);
        }
    }

    private sealed class OrderedUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;

        private readonly OrderedSaveGate _gate;

        private readonly bool _wins;

        public OrderedUnitOfWork(IUnitOfWork inner, OrderedSaveGate gate, bool wins)
        {
            _inner = inner;
            _gate = gate;
            _wins = wins;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            await _gate.ArriveAsync(cancellationToken);
            if (!_wins)
            {
                await _gate.WaitForWinnerAsync(cancellationToken);
            }

            try
            {
                await _inner.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                if (_wins)
                {
                    _gate.SignalWinnerFinished();
                }
            }
        }
    }

    private sealed class SimultaneousUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;

        private readonly Barrier _gate;

        public SimultaneousUnitOfWork(IUnitOfWork inner, Barrier gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _gate.SignalAndWait(cancellationToken);
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