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

    public static Error MatchReferenceMismatch { get; } =
        new(
            "reconciliation.match.reference_mismatch",
            "The invoice number and bank transaction reference are not an exact match.");

    public static Error MatchCurrencyMismatch { get; } =
        new(
            "reconciliation.match.currency_mismatch",
            "The invoice and bank transaction currencies do not match.");

    public static Error MatchStale { get; } =
        new(
            "reconciliation.match.stale",
            "The invoice and bank transaction are no longer an exact open-amount match.");

    public static Error ImportSourceConflict { get; } =
        new(
            "reconciliation.import.source_conflict",
            "An existing record with this source identity has different business data and was not updated.");

    public static Error ImportBatchNotProcessing { get; } =
        new(
            "reconciliation.import.batch_not_processing",
            "The import batch is not accepting row outcomes.");

    public static Error ImportDataRowAlreadyClassified { get; } =
        new(
            "reconciliation.import.data_row_already_classified",
            "The logical data row has already been classified.");

    public static Error ImportIncompleteClassification { get; } =
        new(
            "reconciliation.import.incomplete_classification",
            "Every logical data row must be classified before the import batch can be completed.");

    public static Error ImportCompletionPrecedesCreation { get; } =
        new(
            "reconciliation.import.completion_precedes_creation",
            "Completion time cannot precede creation time.");
}