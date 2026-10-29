using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Allocations;

public sealed class AllocationReversalTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);

    [Fact]
    public void Apply_should_release_the_full_original_amount_and_keep_confirmation_metadata()
    {
        Guid confirmationKey = Guid.NewGuid();
        Settled settled = CreateSettled(
            100m,
            100m,
            40m,
            confirmationKey,
            AllocationIdempotencyOperation.ExplicitAmount);
        DateTimeOffset reversedAt = ConfirmedAt.AddHours(2);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            "  Customer requested reversal  ",
            reversedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Reversed, settled.Allocation.Status);
        Assert.Equal(reversedAt, settled.Allocation.ReversedAt);
        Assert.Equal("Customer requested reversal", settled.Allocation.ReversalReason);
        Assert.Equal(new Money(40m, Pln), settled.Allocation.Amount);
        Assert.Equal(ConfirmedAt, settled.Allocation.ConfirmedAt);
        Assert.Equal(confirmationKey, settled.Allocation.IdempotencyKey);
        Assert.Equal(
            AllocationIdempotencyOperation.ExplicitAmount,
            settled.Allocation.IdempotencyOperation);
        Assert.Equal(new Money(0m, Pln), settled.Invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), settled.BankTransaction.AllocatedAmount);
        Assert.Equal(new Money(100m, Pln), settled.Invoice.OutstandingAmount);
        Assert.Equal(new Money(100m, Pln), settled.BankTransaction.UnappliedAmount);
    }

    [Fact]
    public void Apply_should_release_the_allocated_balance_down_to_zero()
    {
        Guid confirmationKey = Guid.NewGuid();
        Settled settled = CreateSettled(
            80m,
            80m,
            80m,
            confirmationKey,
            AllocationIdempotencyOperation.ExactMatch);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            "Duplicate payment",
            ConfirmedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConfirmedAt, settled.Allocation.ReversedAt);
        Assert.Equal(confirmationKey, settled.Allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, settled.Allocation.IdempotencyOperation);
        Assert.Equal(new Money(0m, Pln), settled.Invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), settled.BankTransaction.AllocatedAmount);
        Assert.Equal(new Money(80m, Pln), settled.Invoice.OutstandingAmount);
        Assert.Equal(new Money(80m, Pln), settled.BankTransaction.UnappliedAmount);
    }

    [Fact]
    public void Apply_should_release_only_the_selected_allocation_on_a_shared_invoice()
    {
        InvoiceId invoiceId = InvoiceId.New();
        Invoice invoice = CreateInvoice(invoiceId, OrganizationId, 100m, Pln);
        BankTransaction selectedBank = CreateBankTransaction(
            BankTransactionId.New(),
            OrganizationId,
            40m,
            Pln);
        BankTransaction otherBank = CreateBankTransaction(
            BankTransactionId.New(),
            OrganizationId,
            60m,
            Pln);
        Allocation selected = CreateAllocation(
            invoiceId,
            selectedBank.Id,
            40m,
            Pln);
        Allocation other = CreateAllocation(invoiceId, otherBank.Id, 60m, Pln);
        Assert.True(invoice.Allocate(selected.Amount).IsSuccess);
        Assert.True(invoice.Allocate(other.Amount).IsSuccess);
        Assert.True(selectedBank.Allocate(selected.Amount).IsSuccess);
        Assert.True(otherBank.Allocate(other.Amount).IsSuccess);

        Result result = AllocationReversal.Apply(
            selected,
            invoice,
            selectedBank,
            "Release one payment",
            ConfirmedAt.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(new Money(60m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), selectedBank.AllocatedAmount);
        Assert.Equal(new Money(60m, Pln), otherBank.AllocatedAmount);
        Assert.Equal(AllocationStatus.Confirmed, other.Status);
        Assert.Equal(new Money(60m, Pln), other.Amount);
        Assert.Null(other.ReversedAt);
        Assert.Null(other.ReversalReason);
    }

    [Fact]
    public void Apply_should_release_only_the_selected_allocation_on_a_shared_bank_transaction()
    {
        BankTransactionId bankTransactionId = BankTransactionId.New();
        BankTransaction bankTransaction = CreateBankTransaction(
            bankTransactionId,
            OrganizationId,
            100m,
            Pln);
        Invoice selectedInvoice = CreateInvoice(InvoiceId.New(), OrganizationId, 40m, Pln);
        Invoice otherInvoice = CreateInvoice(InvoiceId.New(), OrganizationId, 60m, Pln);
        Allocation selected = CreateAllocation(
            selectedInvoice.Id,
            bankTransactionId,
            40m,
            Pln);
        Allocation other = CreateAllocation(otherInvoice.Id, bankTransactionId, 60m, Pln);
        Assert.True(selectedInvoice.Allocate(selected.Amount).IsSuccess);
        Assert.True(otherInvoice.Allocate(other.Amount).IsSuccess);
        Assert.True(bankTransaction.Allocate(selected.Amount).IsSuccess);
        Assert.True(bankTransaction.Allocate(other.Amount).IsSuccess);

        Result result = AllocationReversal.Apply(
            selected,
            selectedInvoice,
            bankTransaction,
            "Release one invoice",
            ConfirmedAt.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(new Money(0m, Pln), selectedInvoice.AllocatedAmount);
        Assert.Equal(new Money(60m, Pln), otherInvoice.AllocatedAmount);
        Assert.Equal(new Money(60m, Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(AllocationStatus.Confirmed, other.Status);
        Assert.Equal(new Money(60m, Pln), other.Amount);
        Assert.Null(other.ReversalReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_should_reject_a_blank_reason_without_changing_balances(string? reason)
    {
        Settled settled = CreateSettled(100m, 100m, 40m);

        Assert.ThrowsAny<ArgumentException>(
            () => AllocationReversal.Apply(
                settled.Allocation,
                settled.Invoice,
                settled.BankTransaction,
                reason!,
                ConfirmedAt.AddHours(1)));

        AssertUnchanged(settled);
    }

    [Fact]
    public void Apply_should_accept_a_trimmed_reason_of_exactly_500_characters()
    {
        Settled settled = CreateSettled(100m, 100m, 25m);
        string reason = new('b', Allocation.MaxReversalReasonLength);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            "  " + reason + "  ",
            ConfirmedAt.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(reason, settled.Allocation.ReversalReason);
        Assert.Equal(Allocation.MaxReversalReasonLength, settled.Allocation.ReversalReason?.Length);
    }

    [Fact]
    public void Apply_should_reject_501_characters_without_changing_any_aggregate()
    {
        Settled settled = CreateSettled(100m, 100m, 25m);
        string reason = new('c', Allocation.MaxReversalReasonLength + 1);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            reason,
            ConfirmedAt.AddHours(1));

        Assert.Equal(
            ReconciliationErrors.AllocationReversalReasonTooLong.Code,
            result.Error?.Code);
        AssertUnchanged(settled);
    }

    [Fact]
    public void Apply_should_reject_a_reversal_time_before_confirmation_without_changing_balances()
    {
        Settled settled = CreateSettled(100m, 100m, 25m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AllocationReversal.Apply(
                settled.Allocation,
                settled.Invoice,
                settled.BankTransaction,
                "Duplicate payment",
                ConfirmedAt.AddMinutes(-1)));

        AssertUnchanged(settled);
    }

    [Fact]
    public void Apply_should_reject_a_second_reversal_and_keep_the_original_reason()
    {
        Settled settled = CreateSettled(100m, 100m, 25m);
        DateTimeOffset reversedAt = ConfirmedAt.AddHours(1);
        Assert.True(
            AllocationReversal.Apply(
                settled.Allocation,
                settled.Invoice,
                settled.BankTransaction,
                "Original reason",
                reversedAt).IsSuccess);
        Money invoiceAllocated = settled.Invoice.AllocatedAmount;
        Money bankAllocated = settled.BankTransaction.AllocatedAmount;

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            "Different reason",
            reversedAt.AddHours(1));

        Assert.Equal(
            ReconciliationErrors.AllocationAlreadyReversed.Code,
            result.Error?.Code);
        Assert.Equal(AllocationStatus.Reversed, settled.Allocation.Status);
        Assert.Equal(reversedAt, settled.Allocation.ReversedAt);
        Assert.Equal("Original reason", settled.Allocation.ReversalReason);
        Assert.Equal(invoiceAllocated, settled.Invoice.AllocatedAmount);
        Assert.Equal(bankAllocated, settled.BankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reject_an_invoice_balance_that_cannot_release_the_allocation()
    {
        Settled settled = CreateSettled(100m, 100m, 40m, allocateInvoice: 10m, allocateBank: 40m);

        Result result = ApplyDefault(settled);

        Assert.Equal(
            ReconciliationErrors.InvoiceReversalExceedsAllocated.Code,
            result.Error?.Code);
        AssertUnchanged(settled, allocatedOnInvoice: 10m, allocatedOnBank: 40m);
    }

    [Fact]
    public void Apply_should_reject_a_bank_balance_that_cannot_release_the_allocation()
    {
        Settled settled = CreateSettled(100m, 100m, 40m, allocateInvoice: 40m, allocateBank: 15m);

        Result result = ApplyDefault(settled);

        Assert.Equal(
            ReconciliationErrors.BankTransactionReversalExceedsAllocated.Code,
            result.Error?.Code);
        AssertUnchanged(settled, allocatedOnInvoice: 40m, allocatedOnBank: 15m);
    }

    [Fact]
    public void Apply_should_reject_an_invoice_currency_mismatch_without_changing_balances()
    {
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        Invoice invoice = CreateInvoice(invoiceId, OrganizationId, 100m, Eur);
        BankTransaction bankTransaction = CreateBankTransaction(
            bankTransactionId,
            OrganizationId,
            100m,
            Pln);
        Allocation allocation = CreateAllocation(invoiceId, bankTransactionId, 40m, Pln);
        Assert.True(invoice.Allocate(new Money(40m, Eur)).IsSuccess);
        Assert.True(bankTransaction.Allocate(allocation.Amount).IsSuccess);

        Result result = AllocationReversal.Apply(
            allocation,
            invoice,
            bankTransaction,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));

        Assert.Equal(ReconciliationErrors.CurrencyMismatch.Code, result.Error?.Code);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Assert.Equal(new Money(40m, Eur), invoice.AllocatedAmount);
        Assert.Equal(new Money(40m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reject_a_bank_currency_mismatch_without_changing_balances()
    {
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        Invoice invoice = CreateInvoice(invoiceId, OrganizationId, 100m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(
            bankTransactionId,
            OrganizationId,
            100m,
            Eur);
        Allocation allocation = CreateAllocation(invoiceId, bankTransactionId, 40m, Pln);
        Assert.True(invoice.Allocate(allocation.Amount).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(40m, Eur)).IsSuccess);

        Result result = AllocationReversal.Apply(
            allocation,
            invoice,
            bankTransaction,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));

        Assert.Equal(ReconciliationErrors.CurrencyMismatch.Code, result.Error?.Code);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Assert.Equal(new Money(40m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(40m, Eur), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reject_a_different_invoice_without_changing_balances()
    {
        Settled settled = CreateSettled(100m, 100m, 40m);
        Invoice otherInvoice = CreateInvoice(InvoiceId.New(), OrganizationId, 100m, Pln);
        Assert.True(otherInvoice.Allocate(new Money(40m, Pln)).IsSuccess);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            otherInvoice,
            settled.BankTransaction,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));

        Assert.Equal(
            ReconciliationErrors.AllocationReversalTargetMismatch.Code,
            result.Error?.Code);
        AssertUnchanged(settled);
        Assert.Equal(new Money(40m, Pln), otherInvoice.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reject_a_different_bank_transaction_without_changing_balances()
    {
        Settled settled = CreateSettled(100m, 100m, 40m);
        BankTransaction otherBank = CreateBankTransaction(
            BankTransactionId.New(),
            OrganizationId,
            100m,
            Pln);
        Assert.True(otherBank.Allocate(new Money(40m, Pln)).IsSuccess);

        Result result = AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            otherBank,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));

        Assert.Equal(
            ReconciliationErrors.AllocationReversalTargetMismatch.Code,
            result.Error?.Code);
        AssertUnchanged(settled);
        Assert.Equal(new Money(40m, Pln), otherBank.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reject_a_different_organization_without_revealing_it()
    {
        OrganizationId otherOrganization = OrganizationId.New();
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        Invoice invoice = CreateInvoice(invoiceId, otherOrganization, 100m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(
            bankTransactionId,
            OrganizationId,
            100m,
            Pln);
        Allocation allocation = CreateAllocation(invoiceId, bankTransactionId, 40m, Pln);
        Assert.True(invoice.Allocate(allocation.Amount).IsSuccess);
        Assert.True(bankTransaction.Allocate(allocation.Amount).IsSuccess);

        Result result = AllocationReversal.Apply(
            allocation,
            invoice,
            bankTransaction,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));

        Assert.Equal(
            ReconciliationErrors.AllocationReversalTargetMismatch.Code,
            result.Error?.Code);
        Assert.DoesNotContain(otherOrganization.Value.ToString(), result.Error?.Message);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
        Assert.Equal(new Money(40m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(40m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_reverse_a_historical_allocation_without_confirmation_metadata()
    {
        Settled settled = CreateSettled(50m, 50m, 50m);

        Result result = ApplyDefault(settled);

        Assert.True(result.IsSuccess);
        Assert.Null(settled.Allocation.IdempotencyKey);
        Assert.Null(settled.Allocation.IdempotencyOperation);
        Assert.Equal(AllocationStatus.Reversed, settled.Allocation.Status);
        Assert.Equal(new Money(0m, Pln), settled.Invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), settled.BankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_keep_the_batch_confirmation_identity()
    {
        Guid itemKey = Guid.NewGuid();
        Settled settled = CreateSettled(
            90m,
            90m,
            90m,
            itemKey,
            AllocationIdempotencyOperation.BatchExactMatch);

        Result result = ApplyDefault(settled);

        Assert.True(result.IsSuccess);
        Assert.Equal(itemKey, settled.Allocation.IdempotencyKey);
        Assert.Equal(
            AllocationIdempotencyOperation.BatchExactMatch,
            settled.Allocation.IdempotencyOperation);
        Assert.Equal(ConfirmedAt, settled.Allocation.ConfirmedAt);
        Assert.Equal(new Money(90m, Pln), settled.Allocation.Amount);
        Assert.Equal(new Money(0m, Pln), settled.Invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), settled.BankTransaction.AllocatedAmount);
    }

    private static Result ApplyDefault(Settled settled)
    {
        return AllocationReversal.Apply(
            settled.Allocation,
            settled.Invoice,
            settled.BankTransaction,
            "Duplicate payment",
            ConfirmedAt.AddHours(1));
    }

    private static void AssertUnchanged(
        Settled settled,
        decimal? allocatedOnInvoice = null,
        decimal? allocatedOnBank = null)
    {
        decimal invoiceAllocated = allocatedOnInvoice ?? settled.Allocation.Amount.Amount;
        decimal bankAllocated = allocatedOnBank ?? settled.Allocation.Amount.Amount;

        Assert.Equal(AllocationStatus.Confirmed, settled.Allocation.Status);
        Assert.Null(settled.Allocation.ReversedAt);
        Assert.Null(settled.Allocation.ReversalReason);
        Assert.Equal(new Money(invoiceAllocated, Pln), settled.Invoice.AllocatedAmount);
        Assert.Equal(new Money(bankAllocated, Pln), settled.BankTransaction.AllocatedAmount);
    }

    private static Settled CreateSettled(
        decimal invoiceTotal,
        decimal bankTotal,
        decimal allocationAmount,
        Guid? idempotencyKey = null,
        AllocationIdempotencyOperation? idempotencyOperation = null,
        decimal? allocateInvoice = null,
        decimal? allocateBank = null)
    {
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        Invoice invoice = CreateInvoice(invoiceId, OrganizationId, invoiceTotal, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(
            bankTransactionId,
            OrganizationId,
            bankTotal,
            Pln);
        Allocation allocation = new(
            AllocationId.New(),
            OrganizationId,
            invoiceId,
            bankTransactionId,
            new Money(allocationAmount, Pln),
            ConfirmedAt,
            idempotencyKey,
            idempotencyOperation);
        Assert.True(invoice.Allocate(new Money(allocateInvoice ?? allocationAmount, Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(allocateBank ?? allocationAmount, Pln)).IsSuccess);
        return new Settled(allocation, invoice, bankTransaction);
    }

    private static Allocation CreateAllocation(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount,
        CurrencyCode currency)
    {
        return new Allocation(
            AllocationId.New(),
            OrganizationId,
            invoiceId,
            bankTransactionId,
            new Money(amount, currency),
            ConfirmedAt);
    }

    private static Invoice CreateInvoice(
        InvoiceId invoiceId,
        OrganizationId organizationId,
        decimal amount,
        CurrencyCode currency)
    {
        return new Invoice(
            invoiceId,
            organizationId,
            "INV-001",
            new Money(amount, currency));
    }

    private static BankTransaction CreateBankTransaction(
        BankTransactionId bankTransactionId,
        OrganizationId organizationId,
        decimal amount,
        CurrencyCode currency)
    {
        return new BankTransaction(
            bankTransactionId,
            organizationId,
            new Money(amount, currency),
            BookingDate,
            "INV-001");
    }

    private sealed record Settled(
        Allocation Allocation,
        Invoice Invoice,
        BankTransaction BankTransaction);
}