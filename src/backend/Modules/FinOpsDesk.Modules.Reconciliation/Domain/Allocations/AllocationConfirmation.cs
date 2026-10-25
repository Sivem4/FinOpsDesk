using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public static class AllocationConfirmation
{
    public static Result Apply(
        Invoice invoice,
        BankTransaction bankTransaction,
        Money amount)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(bankTransaction);
        ArgumentNullException.ThrowIfNull(amount);

        Result invoiceCanAllocate = invoice.CanAllocate(amount);

        if (invoiceCanAllocate.IsFailure)
        {
            return invoiceCanAllocate;
        }

        Result bankTransactionCanAllocate = bankTransaction.CanAllocate(amount);

        if (bankTransactionCanAllocate.IsFailure)
        {
            return bankTransactionCanAllocate;
        }

        Result invoiceAllocated = invoice.Allocate(amount);

        if (invoiceAllocated.IsFailure)
        {
            throw new InvalidOperationException(
                "Invoice allocation failed after CanAllocate succeeded.");
        }

        Result bankTransactionAllocated = bankTransaction.Allocate(amount);

        if (bankTransactionAllocated.IsFailure)
        {
            throw new InvalidOperationException(
                "Bank transaction allocation failed after CanAllocate succeeded.");
        }

        return Result.Success();
    }
}