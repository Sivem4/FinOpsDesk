namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed record BankTransactionListResponse(
    IReadOnlyList<BankTransactionListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);