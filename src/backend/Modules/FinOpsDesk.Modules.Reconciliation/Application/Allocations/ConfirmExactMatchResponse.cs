using FinOpsDesk.Modules.Reconciliation.Application.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ConfirmExactMatchResponse(
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    MoneyDto Amount,
    bool Replayed);