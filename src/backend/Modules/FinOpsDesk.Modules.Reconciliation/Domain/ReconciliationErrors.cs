using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Domain;

public static class ReconciliationErrors
{
    public static Error InvoiceAllocationExceedsOutstanding { get; } =
        new(
            "reconciliation.invoice.allocation_exceeds_outstanding",
            "Allocation amount cannot exceed invoice outstanding amount.");

    public static Error InvoiceReversalExceedsAllocated { get; } =
        new(
            "reconciliation.invoice.reversal_exceeds_allocated",
            "Reversal amount cannot exceed invoice allocated amount.");

    public static Error BankTransactionAllocationExceedsUnapplied { get; } =
        new(
            "reconciliation.bank_transaction.allocation_exceeds_unapplied",
            "Allocation amount cannot exceed unapplied transaction amount.");

    public static Error BankTransactionReversalExceedsAllocated { get; } =
        new(
            "reconciliation.bank_transaction.reversal_exceeds_allocated",
            "Reversal amount cannot exceed allocated transaction amount.");

    public static Error AllocationAlreadyReversed { get; } =
        new(
            "reconciliation.allocation.already_reversed",
            "A reversed allocation cannot be reversed again.");

    public static Error MatchSuggestionAlreadyResolved { get; } =
        new(
            "reconciliation.match_suggestion.already_resolved",
            "A match suggestion can only be accepted or rejected while pending.");

    public static Error CurrencyMismatch { get; } =
        new(
            "reconciliation.currency_mismatch",
            "The amount currency must match the aggregate currency.");
}