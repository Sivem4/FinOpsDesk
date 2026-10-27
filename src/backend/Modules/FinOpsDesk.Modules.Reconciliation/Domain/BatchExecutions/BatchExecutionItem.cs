using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed record BatchExecutionItem
{
    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public BatchExecutionItemOutcome Outcome { get; }

    public AllocationId? AllocationId { get; }

    public Money? Amount { get; }

    public BatchExecutionItem(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        BatchExecutionItemOutcome outcome,
        AllocationId? allocationId,
        Money? amount)
    {
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Batch execution item outcome is not defined.");
        }

        if (invoiceId.Value == Guid.Empty || bankTransactionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Batch execution item identifiers cannot be empty.");
        }

        if (outcome == BatchExecutionItemOutcome.Confirmed)
        {
            if (allocationId is null || amount is null)
            {
                throw new ArgumentException(
                    "A confirmed batch item requires an allocation id, amount, and currency.");
            }

            if (amount.Amount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    "A confirmed batch item amount must be greater than zero.");
            }

            if (amount.Amount != decimal.Round(amount.Amount, 4, MidpointRounding.AwayFromZero))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    "A confirmed batch item amount must fit decimal(19,4).");
            }
        }
        else if (allocationId is not null || amount is not null)
        {
            throw new ArgumentException(
                "A batch item that was not confirmed cannot carry an allocation id or amount.");
        }

        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Outcome = outcome;
        AllocationId = allocationId;
        Amount = amount;
    }
}