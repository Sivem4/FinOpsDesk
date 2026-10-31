using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record GetAllocationReversalContextQuery(AllocationId AllocationId);

public sealed record AllocationReversalContextResponse(
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    string InvoiceNumber,
    string? Reference,
    MoneyDto Amount,
    string Status,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset? ReversedAt,
    string? ReversalReason);