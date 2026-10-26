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

    public static Error InvalidInvoicePagination { get; } =
        new(
            "reconciliation.invoices.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error ImportFileRequired { get; } =
        new(
            "reconciliation.import.file_required",
            "A CSV file is required.");

    public static Error ImportInvalidSourceSystem { get; } =
        new(
            "reconciliation.import.invalid_source_system",
            "Source system is missing or invalid.");

    public static Error ImportInvalidFile { get; } =
        new(
            "reconciliation.import.invalid_file",
            "The CSV file is empty, malformed, or does not match the expected header.");

    public static Error ImportTooManyRows { get; } =
        new(
            "reconciliation.import.too_many_rows",
            "The CSV file contains more than 2000 data rows.");

    public static Error ImportFileTooLarge { get; } =
        new(
            "reconciliation.import.file_too_large",
            "The CSV file is larger than 2 MiB.");

    public static Error ImportConflictUnresolved { get; } =
        new(
            "reconciliation.import.conflict_unresolved",
            "The import could not be completed because a concurrent import kept changing the same records.");

    public static Error MatchCandidateLimitExceeded { get; } =
        new(
            "reconciliation.matches.candidate_limit_exceeded",
            "Exact matching was not run because the open book exceeds 5000 invoices or 5000 bank transactions.");

    public static Error InconsistentMatchMoney { get; } =
        new(
            "reconciliation.matches.inconsistent_money",
            "Exact matching was not run because stored invoice or bank transaction amounts are inconsistent.");

    public static Error IdempotencyKeyRequired { get; } =
        new(
            "reconciliation.confirmation.idempotency_key_required",
            "An Idempotency-Key header with a UUID is required.");

    public static Error IdempotencyKeyInvalid { get; } =
        new(
            "reconciliation.confirmation.idempotency_key_invalid",
            "Idempotency-Key must be a non-empty UUID.");

    public static Error ConfirmationInvalidRequest { get; } =
        new(
            "reconciliation.confirmation.invalid_request",
            "The confirmation body must contain only non-empty invoice and bank transaction ids.");

    public static Error IdempotencyPayloadConflict { get; } =
        new(
            "reconciliation.confirmation.idempotency_payload_conflict",
            "This idempotency key was already used for a different invoice and bank transaction pair.");
}