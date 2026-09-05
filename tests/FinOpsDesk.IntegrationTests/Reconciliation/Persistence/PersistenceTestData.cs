using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal static class PersistenceTestData
{
    public static readonly CurrencyCode Pln = new("PLN");

    public static Invoice CreateInvoice(
        OrganizationId organizationId,
        decimal totalAmount,
        string invoiceNumber = "INV-001")
    {
        return new Invoice(
            InvoiceId.New(),
            organizationId,
            invoiceNumber,
            new Money(totalAmount, Pln));
    }

    public static BankTransaction CreateBankTransaction(
        OrganizationId organizationId,
        decimal amount,
        DateOnly? bookingDate = null,
        string? reference = "INV-001")
    {
        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, Pln),
            bookingDate ?? new DateOnly(2026, 8, 24),
            reference);
    }
}