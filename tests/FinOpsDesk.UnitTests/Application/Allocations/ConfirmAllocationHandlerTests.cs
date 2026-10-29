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

public sealed class ConfirmAllocationHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 9, 2, 18, 56, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);

    [Fact]
    public async Task Handle_should_confirm_allocation_and_commit_once()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var amount = new Money(300m, Pln);
        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            amount);
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(TestOrganizationId, scenario.Invoices.LastOrganizationId);
        Assert.Equal(invoice.Id, scenario.Invoices.LastInvoiceId);
        Assert.Equal(cancellationToken, scenario.Invoices.LastCancellationToken);

        Assert.Equal(1, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(TestOrganizationId, scenario.BankTransactions.LastOrganizationId);
        Assert.Equal(bankTransaction.Id, scenario.BankTransactions.LastBankTransactionId);
        Assert.Equal(
            cancellationToken,
            scenario.BankTransactions.LastCancellationToken);

        Assert.Equal(1, scenario.Allocations.AddCallCount);
        Allocation added = Assert.IsType<Allocation>(scenario.Allocations.Added);
        Assert.Equal(TestOrganizationId, added.OrganizationId);
        Assert.Equal(invoice.Id, added.InvoiceId);
        Assert.Equal(bankTransaction.Id, added.BankTransactionId);
        Assert.Equal(amount, added.Amount);
        Assert.Equal(ConfirmedAt, added.ConfirmedAt);
        Assert.Null(added.IdempotencyKey);
        Assert.Equal(AllocationStatus.Confirmed, added.Status);

        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(cancellationToken, scenario.UnitOfWork.LastCancellationToken);

        Assert.Equal(added.Id, result.Value.AllocationId);
        Assert.Equal(
            new Money(300m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(300m, Pln),
            bankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Handle_should_return_invoice_not_found_without_loading_bank_transaction()
    {
        Scenario scenario = CreateScenario();
        scenario.BankTransactions.BankTransactionToReturn = CreateBankTransaction(1000m);

        var command = new ConfirmAllocationCommand(
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(300m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InvoiceNotFound.Code,
            result.Error?.Code);
        Assert.Equal(1, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(0, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_bank_transaction_not_found_without_allocating()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            BankTransactionId.New(),
            new Money(300m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.BankTransactionNotFound.Code,
            result.Error?.Code);
        Assert.Equal(1, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(1, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_forward_organization_id_from_context()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(300m, Pln));

        await scenario.Handler.Handle(command, CancellationToken.None);

        Assert.Equal(TestOrganizationId, scenario.Invoices.LastOrganizationId);
        Assert.Equal(TestOrganizationId, scenario.BankTransactions.LastOrganizationId);
    }

    [Fact]
    public async Task Handle_should_propagate_invoice_allocation_failure()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(2000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(1001m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code,
            result.Error?.Code);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(0m, Pln),
            bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_propagate_invoice_currency_mismatch()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(100m, Eur));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(0m, Pln),
            bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_bank_transaction_failure_without_mutating_the_invoice()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(200m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(300m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(0m, Pln),
            bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_bank_currency_mismatch_without_mutating_the_invoice()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        var bankTransaction = new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(1000m, Eur),
            BookingDate,
            "INV-001");
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(300m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(0m, Eur),
            bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_propagate_non_positive_amount_guard_exception()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(0m, Pln));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => scenario.Handler.Handle(command, CancellationToken.None));

        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(0m, Pln),
            bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_concurrency_conflict_when_unit_of_work_reports_conflict()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m);
        BankTransaction bankTransaction = CreateBankTransaction(1000m);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.UnitOfWork.ExceptionToThrow = new ConcurrencyConflictException(
            "The persisted data was modified by another operation.");

        var command = new ConfirmAllocationCommand(
            invoice.Id,
            bankTransaction.Id,
            new Money(300m, Pln));

        Result<ConfirmAllocationResult> result = await scenario.Handler.Handle(
            command,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.ConcurrencyConflict.Code,
            result.Error?.Code);
        Assert.Equal("reconciliation.concurrency_conflict", result.Error?.Code);
        Assert.Equal(1, scenario.Allocations.AddCallCount);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new Money(300m, Pln),
            invoice.AllocatedAmount);
        Assert.Equal(
            new Money(300m, Pln),
            bankTransaction.AllocatedAmount);
    }

    private static Scenario CreateScenario()
    {
        var invoices = new FakeInvoiceRepository();
        var bankTransactions = new FakeBankTransactionRepository();
        var allocations = new FakeAllocationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new ConfirmAllocationHandler(
            new FakeCurrentOrganizationContext(TestOrganizationId),
            invoices,
            bankTransactions,
            allocations,
            unitOfWork,
            new FixedTimeProvider(ConfirmedAt));

        return new Scenario(
            handler,
            invoices,
            bankTransactions,
            allocations,
            unitOfWork);
    }

    private static Invoice CreateInvoice(decimal amount)
    {
        return new Invoice(
            InvoiceId.New(),
            TestOrganizationId,
            "INV-001",
            new Money(amount, Pln));
    }

    private static BankTransaction CreateBankTransaction(decimal amount)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(amount, Pln),
            BookingDate,
            "INV-001");
    }

    private sealed record Scenario(
        ConfirmAllocationHandler Handler,
        FakeInvoiceRepository Invoices,
        FakeBankTransactionRepository BankTransactions,
        FakeAllocationRepository Allocations,
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

        public OrganizationId? LastOrganizationId { get; private set; }

        public InvoiceId? LastInvoiceId { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<Invoice?> GetByIdAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastOrganizationId = organizationId;
            LastInvoiceId = invoiceId;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(InvoiceToReturn);
        }
    }

    private sealed class FakeBankTransactionRepository : IBankTransactionRepository
    {
        public BankTransaction? BankTransactionToReturn { get; set; }

        public int GetByIdCallCount { get; private set; }

        public OrganizationId? LastOrganizationId { get; private set; }

        public BankTransactionId? LastBankTransactionId { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<BankTransaction?> GetByIdAsync(
            OrganizationId organizationId,
            BankTransactionId bankTransactionId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastOrganizationId = organizationId;
            LastBankTransactionId = bankTransactionId;
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

        public Task<Allocation?> GetByIdAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void AssignReversalIdempotencyKey(Allocation allocation, Guid reversalIdempotencyKey)
        {
            throw new NotSupportedException();
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