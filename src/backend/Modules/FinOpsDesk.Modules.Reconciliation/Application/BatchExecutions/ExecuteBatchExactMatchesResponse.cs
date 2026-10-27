namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed record ExecuteBatchExactMatchesResponse(
    int ResultVersion,
    int RequestedCount,
    int ConfirmedCount,
    int RejectedCount,
    int InterruptedCount,
    bool AllConfirmed,
    bool HasRejections,
    bool Partial,
    IReadOnlyList<ExecuteBatchExactMatchItemResponse> Items);

public sealed record ExecuteBatchExactMatchItemResponse(
    Guid InvoiceId,
    Guid BankTransactionId,
    string Outcome,
    Guid? AllocationId,
    decimal? Amount,
    string? Currency);