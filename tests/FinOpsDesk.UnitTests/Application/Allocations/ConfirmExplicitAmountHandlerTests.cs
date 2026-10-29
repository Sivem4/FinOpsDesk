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
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Allocations;

public sealed class ConfirmExplicitAmountHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);
    private static readonly Guid IdempotencyKey =
        new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Handle_should_allocate_an_explicit_amount_without_a_reference_match()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(600m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Replayed);
        Assert.Equal(400m, result.Value.Amount.Amount);
        Assert.Equal("PLN", result.Value.Amount.Currency);
        Assert.Equal(cancellationToken, scenario.Lookup.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.Invoices.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.BankTransactions.LastCancellationToken);
        Assert.Equal(cancellationToken, scenario.UnitOfWork.LastCancellationToken);
        Assert.Equal(TestOrganizationId, scenario.Invoices.LastOrganizationId);
        Assert.Equal(TestOrganizationId, scenario.BankTransactions.LastOrganizationId);
        Allocation added = Assert.IsType<Allocation>(scenario.Allocations.Added);
        Assert.Equal(IdempotencyKey, added.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, added.IdempotencyOperation);
        Assert.Equal(new Money(400m, Pln), added.Amount);
        Assert.Equal(new Money(400m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(200m, Pln), bankTransaction.UnappliedAmount);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_allocate_the_entire_remaining_balance()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, null, Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 1000m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Money.Zero(Pln), invoice.OutstandingAmount);
        Assert.Equal(Money.Zero(Pln), bankTransaction.UnappliedAmount);
    }

    [Fact]
    public async Task Handle_should_accept_trailing_zeros_within_scale_4()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "INV-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        decimal amount = decimal.Parse("400.00000", System.Globalization.CultureInfo.InvariantCulture);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            new ConfirmExplicitAmountCommand(
                invoice.Id,
                bankTransaction.Id,
                new Money(amount, Pln),
                IdempotencyKey),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Money(400m, Pln), invoice.AllocatedAmount);
    }

    [Fact]
    public async Task Handle_should_allocate_the_smallest_stored_fraction()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1m, "INV-001", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 0.0001m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Money(0.0001m, Pln), invoice.AllocatedAmount);
    }

    [Fact]
    public async Task Handle_should_replay_the_same_amount_and_currency_without_loading_aggregates()
    {
        Scenario scenario = CreateScenario();
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        var allocationId = AllocationId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            allocationId,
            invoiceId,
            bankTransactionId,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoiceId, bankTransactionId, 400m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(400m, result.Value.Amount.Amount);
        Assert.Equal("PLN", result.Value.Amount.Currency);
        Assert.Equal(0, scenario.Invoices.GetByIdCallCount);
        Assert.Equal(0, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_the_same_key_for_a_different_amount()
    {
        Scenario scenario = Stored(400m, Pln);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(scenario.StoredInvoiceId, scenario.StoredBankTransactionId, 300m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_the_same_key_for_a_different_currency()
    {
        Scenario scenario = Stored(400m, Pln);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(scenario.StoredInvoiceId, scenario.StoredBankTransactionId, 400m, Eur),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.Invoices.GetByIdCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_the_same_key_for_a_different_pair()
    {
        Scenario scenario = Stored(400m, Pln);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(InvoiceId.New(), BankTransactionId.New(), 400m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
    }

    [Fact]
    public async Task Handle_should_reject_an_exact_match_key_for_the_same_pair_and_amount()
    {
        Scenario scenario = CreateScenario();
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoiceId,
            bankTransactionId,
            new Money(1000m, Pln),
            AllocationIdempotencyOperation.ExactMatch);
        scenario.StoredInvoiceId = invoiceId;
        scenario.StoredBankTransactionId = bankTransactionId;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoiceId, bankTransactionId, 1000m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.Invoices.GetByIdCallCount);
    }

    [Fact]
    public async Task Handle_should_recover_a_unique_conflict_as_a_replay()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.UnitOfWork.ExceptionToThrow = new UniqueConstraintConflictException(
            "A unique constraint rejected the commit.");
        var allocationId = AllocationId.New();
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            allocationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
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
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.UnitOfWork.ExceptionToThrow = new ConcurrencyConflictException(
            "The persisted data was modified by another operation.");

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ConcurrencyConflict.Code,
            result.Error?.Code);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_replay_when_the_same_key_commits_before_validation()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Pln);
        Assert.True(invoice.Allocate(new Money(1000m, Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(1000m, Pln)).IsSuccess);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        var allocationId = AllocationId.New();
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            allocationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Replayed);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal(400m, result.Value.Amount.Amount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
        Assert.Equal(new Money(1000m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(1000m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Handle_should_conflict_when_a_different_request_commits_before_validation()
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
            AllocationIdempotencyOperation.ExactMatch);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            result.Error?.Code);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Handle_should_reject_a_non_positive_amount_without_mutating(
        decimal amount)
    {
        Scenario scenario = OpenPair();

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(scenario.Invoices.InvoiceToReturn!.Id, scenario.BankTransactions.BankTransactionToReturn!.Id, amount, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ExplicitAmountInvalid.Code,
            result.Error?.Code);
        AssertUnchanged(scenario);
        Assert.Equal(1, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public async Task Handle_should_reject_more_than_four_fractional_digits_without_mutating()
    {
        Scenario scenario = OpenPair();

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(
                scenario.Invoices.InvoiceToReturn!.Id,
                scenario.BankTransactions.BankTransactionToReturn!.Id,
                0.00001m,
                Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ExplicitAmountInvalid.Code,
            result.Error?.Code);
        AssertUnchanged(scenario);
    }

    [Fact]
    public async Task Handle_should_reject_an_amount_outside_decimal_19_4_without_mutating()
    {
        Scenario scenario = OpenPair();

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(
                scenario.Invoices.InvoiceToReturn!.Id,
                scenario.BankTransactions.BankTransactionToReturn!.Id,
                1_000_000_000_000_000m,
                Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ExplicitAmountInvalid.Code,
            result.Error?.Code);
        AssertUnchanged(scenario);
    }

    [Fact]
    public async Task Handle_should_leave_both_aggregates_unchanged_when_the_invoice_rejects_the_amount()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(500m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 600m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code,
            result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(2, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public async Task Handle_should_leave_both_aggregates_unchanged_when_the_bank_rejects_the_amount()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(200m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
    }

    [Fact]
    public async Task Handle_should_leave_both_aggregates_unchanged_when_the_currency_differs()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, "OTHER", Eur);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.CurrencyMismatch.Code, result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Eur), bankTransaction.AllocatedAmount);
        Assert.Equal(1, scenario.Lookup.FindCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_return_not_found_for_a_missing_invoice_without_loading_the_bank_transaction()
    {
        Scenario scenario = CreateScenario();

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(InvoiceId.New(), BankTransactionId.New(), 400m, Pln),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.InvoiceNotFound.Code, result.Error?.Code);
        Assert.Equal(0, scenario.BankTransactions.GetByIdCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_append_one_confirmation_audit_for_the_confirmed_amount()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(600m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        (Allocation allocation, string actorId, BatchExecutionId? batchExecutionId) =
            Assert.Single(scenario.Audit.Calls);
        Assert.Same(scenario.Allocations.Added, allocation);
        Assert.Equal("operator-a", actorId);
        Assert.Null(batchExecutionId);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, allocation.IdempotencyOperation);
        Assert.Equal(IdempotencyKey, allocation.IdempotencyKey);
        Assert.Equal(new Money(400m, Pln), allocation.Amount);
        Assert.Equal(1, scenario.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Handle_should_not_append_audit_when_replaying()
    {
        Scenario scenario = CreateScenario();
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoiceId,
            bankTransactionId,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoiceId, bankTransactionId, 400m, Pln),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value?.Replayed);
        Assert.Empty(scenario.Audit.Calls);
    }

    [Fact]
    public async Task Handle_should_reject_a_missing_actor_before_idempotency_lookup()
    {
        Scenario scenario = CreateScenario();
        scenario.Actor.ActorId = null;
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoiceId,
            bankTransactionId,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoiceId, bankTransactionId, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ActorRequired.Code, result.Error?.Code);
        Assert.Equal(0, scenario.Lookup.FindCallCount);
        Assert.Empty(scenario.Audit.Calls);
    }

    [Fact]
    public async Task Handle_should_not_replay_an_audit_uniqueness_failure()
    {
        Scenario scenario = CreateScenario();
        Invoice invoice = CreateInvoice(1000m, "INV-001", Pln);
        BankTransaction bankTransaction = CreateBankTransaction(600m, "OTHER", Pln);
        scenario.Invoices.InvoiceToReturn = invoice;
        scenario.BankTransactions.BankTransactionToReturn = bankTransaction;
        scenario.Conflict.Violation = false;
        scenario.UnitOfWork.ExceptionToThrow = new UniqueConstraintConflictException(
            "A unique constraint rejected the commit.");
        scenario.Lookup.RecordAfterSave = new ConfirmedAllocationRecord(
            AllocationId.New(),
            invoice.Id,
            bankTransaction.Id,
            new Money(400m, Pln),
            AllocationIdempotencyOperation.ExplicitAmount);

        Result<ConfirmExplicitAmountResponse> result = await scenario.Handler.Handle(
            Command(invoice.Id, bankTransaction.Id, 400m, Pln),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ConcurrencyConflict.Code,
            result.Error?.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, scenario.Lookup.FindCallCount);
    }

    [Fact]
    public void Command_should_reject_an_empty_idempotency_key()
    {
        Assert.Throws<ArgumentException>(
            () => new ConfirmExplicitAmountCommand(
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(400m, Pln),
                Guid.Empty));
    }

    private static void AssertUnchanged(Scenario scenario)
    {
        Assert.Equal(new Money(0m, Pln), scenario.Invoices.InvoiceToReturn!.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), scenario.BankTransactions.BankTransactionToReturn!.AllocatedAmount);
        Assert.Equal(0, scenario.Allocations.AddCallCount);
        Assert.Equal(0, scenario.UnitOfWork.SaveChangesCallCount);
    }

    private static Scenario OpenPair()
    {
        Scenario scenario = CreateScenario();
        scenario.Invoices.InvoiceToReturn = CreateInvoice(1000m, "INV-001", Pln);
        scenario.BankTransactions.BankTransactionToReturn = CreateBankTransaction(1000m, "OTHER", Pln);
        return scenario;
    }

    private static Scenario Stored(decimal amount, CurrencyCode currency)
    {
        Scenario scenario = CreateScenario();
        scenario.StoredInvoiceId = InvoiceId.New();
        scenario.StoredBankTransactionId = BankTransactionId.New();
        scenario.Lookup.Record = new ConfirmedAllocationRecord(
            AllocationId.New(),
            scenario.StoredInvoiceId,
            scenario.StoredBankTransactionId,
            new Money(amount, currency),
            AllocationIdempotencyOperation.ExplicitAmount);
        return scenario;
    }

    private static ConfirmExplicitAmountCommand Command(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount,
        CurrencyCode currency)
    {
        return new ConfirmExplicitAmountCommand(
            invoiceId,
            bankTransactionId,
            new Money(amount, currency),
            IdempotencyKey);
    }

    private static Scenario CreateScenario()
    {
        var invoices = new FakeInvoiceRepository();
        var bankTransactions = new FakeBankTransactionRepository();
        var allocations = new FakeAllocationRepository();
        var lookup = new FakeLookup();
        var unitOfWork = new FakeUnitOfWork();
        var actor = new FakeActorContext();
        var audit = new FakeAuditWriter();
        var conflict = new FakeIdempotencyConflict();
        var handler = new ConfirmExplicitAmountHandler(
            new FakeCurrentOrganizationContext(TestOrganizationId),
            actor,
            invoices,
            bankTransactions,
            allocations,
            lookup,
            unitOfWork,
            audit,
            conflict,
            new FixedTimeProvider(ConfirmedAt));

        return new Scenario(
            handler,
            invoices,
            bankTransactions,
            allocations,
            lookup,
            unitOfWork,
            actor,
            audit,
            conflict);
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
        string? reference,
        CurrencyCode currency)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(amount, currency),
            BookingDate,
            reference);
    }

    private sealed class Scenario
    {
        public Scenario(
            ConfirmExplicitAmountHandler handler,
            FakeInvoiceRepository invoices,
            FakeBankTransactionRepository bankTransactions,
            FakeAllocationRepository allocations,
            FakeLookup lookup,
            FakeUnitOfWork unitOfWork,
            FakeActorContext actor,
            FakeAuditWriter audit,
            FakeIdempotencyConflict conflict)
        {
            Handler = handler;
            Invoices = invoices;
            BankTransactions = bankTransactions;
            Allocations = allocations;
            Lookup = lookup;
            UnitOfWork = unitOfWork;
            Actor = actor;
            Audit = audit;
            Conflict = conflict;
        }

        public ConfirmExplicitAmountHandler Handler { get; }

        public FakeInvoiceRepository Invoices { get; }

        public FakeBankTransactionRepository BankTransactions { get; }

        public FakeAllocationRepository Allocations { get; }

        public FakeLookup Lookup { get; }

        public FakeUnitOfWork UnitOfWork { get; }

        public FakeActorContext Actor { get; }

        public FakeAuditWriter Audit { get; }

        public FakeIdempotencyConflict Conflict { get; }

        public InvoiceId StoredInvoiceId { get; set; } = InvoiceId.New();

        public BankTransactionId StoredBankTransactionId { get; set; } = BankTransactionId.New();
    }

    private sealed class FakeActorContext : ICurrentActorContext
    {
        public string? ActorId { get; set; } = "operator-a";

        public bool TryGetActorId(out string actorId)
        {
            if (string.IsNullOrWhiteSpace(ActorId))
            {
                actorId = string.Empty;
                return false;
            }

            actorId = ActorId;
            return true;
        }
    }

    private sealed class FakeAuditWriter : IFinancialAuditWriter
    {
        public List<(Allocation Allocation, string ActorId, BatchExecutionId? BatchExecutionId)> Calls { get; } = [];

        public void AppendConfirmation(
            Allocation allocation,
            string actorId,
            BatchExecutionId? batchExecutionId)
        {
            Calls.Add((allocation, actorId, batchExecutionId));
        }

        public void AppendReversal(
            Allocation allocation,
            string actorId,
            Guid reversalIdempotencyKey)
        {
            throw new InvalidOperationException("This confirmation test double does not record reversals.");
        }
    }

    private sealed class FakeIdempotencyConflict : IAllocationIdempotencyConflict
    {
        public bool Violation { get; set; } = true;

        public bool IsIdempotencyIndexViolation(Exception exception)
        {
            return Violation;
        }
    }

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

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<Invoice?> GetByIdAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastOrganizationId = organizationId;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(InvoiceToReturn);
        }
    }

    private sealed class FakeBankTransactionRepository : IBankTransactionRepository
    {
        public BankTransaction? BankTransactionToReturn { get; set; }

        public int GetByIdCallCount { get; private set; }

        public OrganizationId? LastOrganizationId { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<BankTransaction?> GetByIdAsync(
            OrganizationId organizationId,
            BankTransactionId bankTransactionId,
            CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            LastOrganizationId = organizationId;
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