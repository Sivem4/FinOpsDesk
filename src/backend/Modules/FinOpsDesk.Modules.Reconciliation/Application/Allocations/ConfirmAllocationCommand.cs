using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmAllocationCommand
{
    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money Amount { get; }

    public ConfirmAllocationCommand(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount)
    {
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);

        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Amount = amount;
    }
}