using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ListAllocationHistoryQuery(
    int Page,
    int PageSize,
    InvoiceId? InvoiceId,
    BankTransactionId? BankTransactionId);

public sealed record AllocationHistoryItemResponse(
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    string InvoiceNumber,
    string? Reference,
    MoneyDto Amount,
    string Status,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset? ReversedAt,
    string? ReversalReason,
    string? IdempotencyOperation);

public sealed record AllocationHistoryListResponse(
    IReadOnlyList<AllocationHistoryItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);