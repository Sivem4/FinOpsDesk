using FinOpsDesk.Modules.Reconciliation.Application.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed record BankTransactionListItemDto(
    Guid Id,
    DateOnly BookingDate,
    string? Reference,
    MoneyDto Amount,
    MoneyDto AllocatedAmount,
    MoneyDto UnappliedAmount,
    string? SourceSystem,
    string? ExternalId);