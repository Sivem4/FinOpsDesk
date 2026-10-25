using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmExactMatchCommand
{
    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Guid IdempotencyKey { get; }

    public ConfirmExactMatchCommand(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        IdempotencyKey = idempotencyKey;
    }
}