namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed record BankTransactionListRead(
    int TotalCount,
    IReadOnlyList<BankTransactionListColumns> Items);