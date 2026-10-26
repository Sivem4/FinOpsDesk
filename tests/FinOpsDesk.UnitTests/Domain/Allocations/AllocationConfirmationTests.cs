using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Allocations;

public sealed class AllocationConfirmationTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateOnly BookingDate = new(2026, 8, 24);

    [Fact]
    public void Apply_should_update_both_balances_when_both_can_allocate()
    {
        Invoice invoice = CreateInvoice(1000m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, Pln);
        var amount = new Money(300m, Pln);

        Result result = AllocationConfirmation.Apply(invoice, bankTransaction, amount);

        Assert.True(result.IsSuccess);
        Assert.Equal(amount, invoice.AllocatedAmount);
        Assert.Equal(amount, bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_leave_both_aggregates_unchanged_when_the_bank_rejects_the_amount()
    {
        Invoice invoice = CreateInvoice(1000m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(200m, Pln);

        Result result = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            new Money(300m, Pln));

        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_leave_the_bank_transaction_unchanged_when_the_invoice_rejects_the_amount()
    {
        Invoice invoice = CreateInvoice(100m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, Pln);

        Result result = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            new Money(300m, Pln));

        Assert.Equal(
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code,
            result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Apply_should_leave_the_invoice_unchanged_when_the_bank_currency_differs()
    {
        Invoice invoice = CreateInvoice(1000m, Pln);
        BankTransaction bankTransaction = CreateBankTransaction(1000m, Eur);

        Result result = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            new Money(300m, Pln));

        Assert.Equal(ReconciliationErrors.CurrencyMismatch.Code, result.Error?.Code);
        Assert.Equal(new Money(0m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Eur), bankTransaction.AllocatedAmount);
    }

    private static Invoice CreateInvoice(decimal amount, CurrencyCode currency)
    {
        return new Invoice(
            InvoiceId.New(),
            OrganizationId,
            "INV-001",
            new Money(amount, currency));
    }

    private static BankTransaction CreateBankTransaction(decimal amount, CurrencyCode currency)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            OrganizationId,
            new Money(amount, currency),
            BookingDate,
            "INV-001");
    }
}