using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application;

public static class ReconciliationApplicationErrors
{
    public static Error InvoiceNotFound { get; } =
        new(
            "reconciliation.invoice.not_found",
            "Invoice was not found.");

    public static Error BankTransactionNotFound { get; } =
        new(
            "reconciliation.bank_transaction.not_found",
            "Bank transaction was not found.");

    public static Error ConcurrencyConflict { get; } =
        new(
            "reconciliation.concurrency_conflict",
            "The invoice or bank transaction was modified by another operation.");
}