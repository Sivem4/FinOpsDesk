using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Allocations;

public sealed class ConfirmExactMatchHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);
    private static readonly Guid IdempotencyKey =
        new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Handle_should_allocate_the_full_open_amount_and_store_the_key()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "inv-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Replayed);
        Assert.Equal(invoice.Id.Value, result.Value.InvoiceId);
        Assert.Equal(bankTransaction.Id.Value, result.Value.BankTransactionId);
        Assert.Equal(1000m, result.Value.Amount.Amount);
        Assert.Equal("PLN", result.Value.Amount.Currency);
        Assert.Equal(cancellationToken, scenario.Lookup.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.Invoices.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.BankTransactions.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.UnitOfWork.LastCancellationToken);
        Allocation added = Assert.IsType<Allocation>(scenario.Allocations.Added);
        Assert.Equal(IdempotencyKey, added.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, added.IdempotencyOperation);
        Assert.Equal(new Money(1000m, Pln), added.Amount);
        Assert.Equal(new Money(1000m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(1000m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_replay_a_committed_key_without_loading_aggregates()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        var allocationId = AllocationId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            allocationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExactMatch);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(0, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(0, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_the_same_key_for_a_different_pair()
    {
        Scenario scenario = CreateScenario();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExactMatch);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(InvoiceId.New(), BankTransactionId.New(), IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_recover_a_unique_conflict_as_a_replay()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.UnitOfWork.ExceptionToThrow = new UniqueConstraintConflictException(
            "A unique constraint rejected the commit.");
        var allocationId = AllocationId.New();
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            allocationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExactMatch);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public async Task Handle_should_return_concurrency_conflict_when_the_key_was_not_stored()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.UnitOfWork.ExceptionToThrow = new ConcurrencyConflictException(
            "The persisted data was modified by another operation.");

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ConcurrencyConflict.Code,
            result.Error?.Code);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_not_found_for_a_missing_invoice_without_loading_the_bank_transaction()
    {
        Scenario scenario = CreateScenario();

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(InvoiceId.New(), BankTransactionId.New(), IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.InvoiceNotFound.Code, result.Error?.Code);
        Assert.Equal(0, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_leave_the_invoice_unchanged_when_the_pair_is_stale()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(200m, "INV-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.MatchStale.Code, result.Error?.Code);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_leave_both_aggregates_unchanged_when_references_differ()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.MatchReferenceMismatch.Code, result.Error?.Code);
        Assert.Equal(1, scenario.Lookup.FindCallCount);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_leave_both_aggregates_unchanged_when_currencies_differ()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Eur);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.MatchCurrencyMismatch.Code, result.Error?.Code);
        Assert.Equal(1, scenario.Lookup.FindCallCount);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Eur), bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_replay_when_the_same_key_commits_before_validation()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        Assert.True(invoice.Allocate(new Money(1000m, Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(1000m, Pln)).IsSuccess);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        var allocationId = AllocationId.New();
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            allocationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExactMatch);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(1000m, result.Value.Amount.Amount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public async Task Handle_should_conflict_when_a_different_operation_commits_before_validation()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        Assert.True(invoice.Allocate(new Money(1000m, Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(1000m, Pln)).IsSuccess);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoice.Id,
            bankTransaction.Id,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_an_explicit_amount_key_for_the_same_pair()
    {
        Scenario scenario = CreateScenario();
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoiceId,
            bankTransactionId,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExactMatchResponse> result = await scenario.Handler.Handle(
            new ConfirmExactMatchCommand(invoiceId, bankTransactionId, IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public void Command_should_reject_an_empty_idempotency_key()
    {
        Assert.Throws<ArgumentException>(
            () => new ConfirmExactMatchCommand(
                InvoiceId.New(),
                BankTransactionId.New(),
                Guid.Empty));
    }

    private static Scenario CreateScenario()
    {
        var invoices = new FakeInvoiceRepository();
        var bankTransactions = new FakeBankTransactionRepository();
        var allocations = new FakeAllocationRepository();
        var lookup = new FakeLookup();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new ConfirmExactMatchHandler(
            new FakeCurrentOrganizationContext(TestOrganizationId),
            invoices,
            bankTransactions,
            allocations,
            lookup,
            unitOfWork,
            new FixedTimeProvider(ConfirmedAt));

        return new Scenario(
            handler,
            invoices,
            bankTransactions,
            allocations,
            lookup,
            unitOfWork);
    }

    private static Invoice CreateInvoice(
        decimal amount,
        string invoiceNumber,
        CurrencyCode currency)
    {
        return new Invoice(
            InvoiceId.New(),
            TestOrganizationId,
            invoiceNumber,
            new Money(amount, currency));
    }

    private static BankTransaction CreateBankTransaction(
        decimal amount,
        string reference,
        CurrencyCode currency)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(amount, currency),
            BookingDate,
            reference);
    }

    private sealed record Scenario(
        ConfirmExactMatchHandler Handler,
        FakeInvoiceRepository Invoices,
        FakeBankTransactionRepository BankTransactions,
        FakeAllocationRepository Allocations,
        FakeLookup Lookup,
        FakeUnitOfWork UnitOfWork);

    private sealed class FakeCurrentOrganizationContext : ICurrentOrganizationContext
    {
        public FakeCurrentOrganizationContext(OrganizationId organizationId)
        {
            OrganizationId = organizationId;
        }

        public OrganizationId OrganizationId { get; }
    }

    private sealed class FakeInvoiceRepository : IInvoiceRepository
    {
        public Invoice? InvoiceToReturn { get; set; }

        public int GetByIdCallCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<Invoice?> GetByIdAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(InvoiceToReturn);
        }
    }

    private sealed class FakeBankTransactionRepository : IBankTransactionRepository
    {
        public BankTransaction? BankTransactionToReturn { get; set; }

        public int GetByIdCallCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<BankTransaction?> GetByIdAsync(
            OrganizationId organizationId,
            BankTransactionId bankTransactionId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(BankTransactionToReturn);
        }
    }

    private sealed class FakeAllocationRepository : IAllocationRepository
    {
        public Allocation? Added { get; private set; }

        public int AddCallCount { get; private set; }

        public void Add(Allocation allocation)
        {
            AddCallCount++;
            Added = allocation;
        }
    }

    private sealed class FakeLookup : IAllocationIdempotencyLookup
    {
        public ConfirmedAllocationRecord? Record { get; set; }

        public ConfirmedAllocationRecord? RecordAfterSave { get; set; }

        public int FindCallCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<ConfirmedAllocationRecord?> FindAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            FindCallCount++;
            LastCancellationToken = cancellationToken;
            ConfirmedAllocationRecord? record = FindCallCount == 1
                ? Record
                : RecordAfterSave ?? Record;
            return Task.FromResult(record);
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveChangesCallCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCallCount++;
            LastCancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.CompletedTask;
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