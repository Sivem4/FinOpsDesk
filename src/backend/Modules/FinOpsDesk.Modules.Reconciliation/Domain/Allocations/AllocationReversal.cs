using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public static class AllocationReversal
{
    public static Result Apply(
        Allocation allocation,
        Invoice invoice,
        BankTransaction bankTransaction,
        string reason,
        DateTimeOffset reversedAt)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(bankTransaction);

        Result allocationValidation = allocation.ValidateReversal(reason, reversedAt);

        if (allocationValidation.IsFailure)
        {
            return allocationValidation;
        }

        if (invoice.Id != allocation.InvoiceId
            || bankTransaction.Id != allocation.BankTransactionId
            || invoice.OrganizationId != allocation.OrganizationId
            || bankTransaction.OrganizationId != allocation.OrganizationId)
        {
            return Result.Failure(ReconciliationErrors.AllocationReversalTargetMismatch);
        }

        Result invoiceCanReverse = invoice.CanReverseAllocation(allocation.Amount);

        if (invoiceCanReverse.IsFailure)
        {
            return invoiceCanReverse;
        }

        Result bankTransactionCanReverse = bankTransaction.CanReverseAllocation(allocation.Amount);

        if (bankTransactionCanReverse.IsFailure)
        {
            return bankTransactionCanReverse;
        }

        Result reversed = allocation.Reverse(reason, reversedAt);

        if (reversed.IsFailure)
        {
            throw new InvalidOperationException(
                "Allocation reversal failed after validation succeeded.");
        }

        Result invoiceReversed = invoice.ReverseAllocation(allocation.Amount);

        if (invoiceReversed.IsFailure)
        {
            throw new InvalidOperationException(
                "Invoice reversal failed after CanReverseAllocation succeeded.");
        }

        Result bankTransactionReversed = bankTransaction.ReverseAllocation(allocation.Amount);

        if (bankTransactionReversed.IsFailure)
        {
            throw new InvalidOperationException(
                "Bank transaction reversal failed after CanReverseAllocation succeeded.");
        }

        return Result.Success();
    }
}