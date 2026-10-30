namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed record BankTransactionListColumns(
    Guid Id,
    string? Reference,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    decimal AllocatedAmount,
    string AllocatedCurrency,
    string? SourceSystem,
    string? ExternalId);