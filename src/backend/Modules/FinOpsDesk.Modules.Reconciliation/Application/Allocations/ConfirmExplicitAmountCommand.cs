using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmExplicitAmountCommand
{
    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money Amount { get; }

    public Guid IdempotencyKey { get; }

    public ConfirmExplicitAmountCommand(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Amount = amount;
        IdempotencyKey = idempotencyKey;
    }
}