using FinOpsDesk.Modules.Reconciliation.Application.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ConfirmExplicitAmountResponse(
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    MoneyDto Amount,
    bool Replayed);