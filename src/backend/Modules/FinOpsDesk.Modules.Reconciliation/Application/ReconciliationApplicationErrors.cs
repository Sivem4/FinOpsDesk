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

    public static Error InvalidInvoiceNumberLookup { get; } =
        new(
            "reconciliation.invoices.invalid_invoice_number",
            "Invoice number must be 1 to 256 characters after trimming.");

    public static Error InvalidBankTransactionPagination { get; } =
        new(
            "reconciliation.bank_transactions.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error InvalidBankTransactionReferenceLookup { get; } =
        new(
            "reconciliation.bank_transactions.invalid_reference",
            "Bank transaction reference must be 1 to 256 characters after trimming.");

    public static Error BankTransactionListInconsistentMoney { get; } =
        new(
            "reconciliation.bank_transactions.inconsistent_money",
            "Stored amounts for a bank transaction on this page are inconsistent.");

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

    public static Error SmartMatchUnsupportedQuery { get; } =
        new(
            "reconciliation.matches.smart.unsupported_query",
            "Smart matching does not accept query parameters.");

    public static Error ActorRequired { get; } =
        new(
            "reconciliation.actor.required",
            "A valid authenticated actor is required.");

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
            "This idempotency key was already used for a different allocation request.");

    public static Error ExplicitAmountInvalid { get; } =
        new(
            "reconciliation.allocation.invalid_amount",
            "The allocation amount must be greater than zero and representable as decimal(19,4) without rounding.");

    public static Error AllocationInvalidRequest { get; } =
        new(
            "reconciliation.allocation.invalid_request",
            "The allocation body must contain non-empty invoice and bank transaction ids and an amount object with a three-letter currency.");

    public static Error AllocationUnsupportedMediaType { get; } =
        new(
            "reconciliation.allocation.unsupported_media_type",
            "The allocation request must use Content-Type application/json.");

    public static Error AllocationNotFound { get; } =
        new(
            "reconciliation.allocation.not_found",
            "Allocation was not found.");

    public static Error AllocationAuditInvalidRequest { get; } =
        new(
            "reconciliation.allocation_audit.invalid_request",
            "The allocation audit request has a malformed allocation id or an unsupported query parameter.");

    public static Error AllocationAuditInvalidPagination { get; } =
        new(
            "reconciliation.allocation_audit.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error AllocationHistoryInvalidRequest { get; } =
        new(
            "reconciliation.allocation_history.invalid_request",
            "The allocation history request has a malformed filter or an unsupported query parameter.");

    public static Error AllocationHistoryInvalidPagination { get; } =
        new(
            "reconciliation.allocation_history.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error AllocationReversalContextInvalidRequest { get; } =
        new(
            "reconciliation.allocation_reversal_context.invalid_request",
            "The reversal context request has a malformed allocation id or an unsupported query parameter.");

    public static Error AllocationRequestTooLarge { get; } =
        new(
            "reconciliation.allocation.request_too_large",
            "The allocation request body must be at most 4096 bytes.");

    public static Error AllocationContextInvalidRequest { get; } =
        new(
            "reconciliation.allocation_context.invalid_request",
            "The allocation context request must contain exactly one invoice id and one bank transaction id, and both must be non-empty GUIDs.");

    public static Error AllocationContextInconsistentMoney { get; } =
        new(
            "reconciliation.allocation_context.inconsistent_money",
            "Stored amounts for the invoice or bank transaction are inconsistent.");

    public static Error ReviewCaseNotFound { get; } =
        new(
            "reconciliation.review_case.not_found",
            "Review case was not found.");

    public static Error ReviewCaseAuditInvalidRequest { get; } =
        new(
            "reconciliation.review_case_audit.invalid_request",
            "The review case audit request has a malformed review case id or an unsupported query parameter.");

    public static Error ReviewCaseAuditInvalidPagination { get; } =
        new(
            "reconciliation.review_case_audit.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error ReviewCaseInvalidRequest { get; } =
        new(
            "reconciliation.review_case.invalid_request",
            "The review case request has an invalid reason, source, note, or financial identifiers.");

    public static Error ReviewCaseInvalidConcurrencyToken { get; } =
        new(
            "reconciliation.review_case.invalid_concurrency_token",
            "The concurrency token must be the Base64 form of the review case row version.");

    public static Error ReviewCaseInvalidPagination { get; } =
        new(
            "reconciliation.review_cases.invalid_pagination",
            "Page must be at least 1, page size must be between 1 and 100, and the page offset must fit in a 32-bit integer.");

    public static Error ReviewCaseConcurrencyConflict { get; } =
        new(
            "reconciliation.review_case.concurrency_conflict",
            "The review case was modified by another operation.");

    public static Error ReviewCaseAllocationMismatch { get; } =
        new(
            "reconciliation.review_case.allocation_mismatch",
            "The allocation was not found in this organization, is not confirmed, or does not cover the review case.");

    public static Error ReviewCaseSubjectConflict { get; } =
        new(
            "reconciliation.review_case.subject_conflict",
            "An open review case for this subject could not be recovered after a concurrent create.");

    public static Error ReviewCaseInconsistentLinkedMoney { get; } =
        new(
            "reconciliation.review_case.inconsistent_linked_money",
            "Stored amounts for a linked invoice or bank transaction are inconsistent.");

    public static Error ReviewCaseUnsupportedMediaType { get; } =
        new(
            "reconciliation.review_case.unsupported_media_type",
            "The review case request must use Content-Type application/json.");

    public static Error ReviewCaseRequestTooLarge { get; } =
        new(
            "reconciliation.review_case.request_too_large",
            "The review case request body must be at most 4096 bytes.");

    public static Error BatchInvalidRequest { get; } =
        new(
            "reconciliation.batch.invalid_request",
            "The batch request must contain a non-empty idempotency key and 1 to 25 distinct pairs.");

    public static Error BatchIdempotencyPayloadConflict { get; } =
        new(
            "reconciliation.batch.idempotency_payload_conflict",
            "This idempotency key was already used for a different batch pair set.");

    public static Error BatchExecutionInProgress { get; } =
        new(
            "reconciliation.batch.execution_in_progress",
            "This batch execution is already being processed.");

    public static Error BatchCandidateLimitExceeded { get; } =
        new(
            "reconciliation.batch.candidate_limit_exceeded",
            "Batch execution was not started because the open book exceeds 5000 invoices or 5000 bank transactions.");

    public static Error BatchAllocationIntegrityConflict { get; } =
        new(
            "reconciliation.batch.allocation_integrity_conflict",
            "An allocation with this batch item key does not match the organization, operation, invoice, and bank transaction.");

    public static Error BatchInfrastructureFailure { get; } =
        new(
            "reconciliation.batch.infrastructure_failure",
            "Batch execution stopped because of an unexpected infrastructure failure. Committed allocations remain committed.");

    public static Error BatchInvalidQuery { get; } =
        new(
            "reconciliation.batch.invalid_query",
            "Page must be at least 1, page size must be from 1 through 100, and the page offset must fit in a 32-bit integer.");

    public static Error BatchUnsupportedMediaType { get; } =
        new(
            "reconciliation.batch.unsupported_media_type",
            "The batch execution request must use Content-Type application/json.");

    public static Error BatchRequestTooLarge { get; } =
        new(
            "reconciliation.batch.request_too_large",
            "The batch execution request body must be at most 8192 bytes.");

    public static Error AllocationReversalInvalidRequest { get; } =
        new(
            "reconciliation.allocation.reversal_invalid_request",
            "The reversal body must contain only a non-blank reason.");

    public static Error AllocationReversalIdempotencyPayloadConflict { get; } =
        new(
            "reconciliation.allocation.reversal_idempotency_payload_conflict",
            "This idempotency key was already used for a different reversal request.");

    public static Error AllocationReversalIntegrityConflict { get; } =
        new(
            "reconciliation.allocation.reversal_integrity_conflict",
            "The stored reversal is not a complete reversal for this request.");
}