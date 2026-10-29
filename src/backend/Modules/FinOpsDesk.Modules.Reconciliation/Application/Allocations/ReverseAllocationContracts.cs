using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ReverseAllocationCommand(
    AllocationId AllocationId,
    string Reason,
    Guid IdempotencyKey);

public sealed record ReverseAllocationResponse(
    Guid AllocationId,
    string Status,
    MoneyDto Amount,
    DateTimeOffset ReversedAt,
    string Reason,
    bool Replayed);

public sealed record AllocationReversalKeyRecord(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    AllocationStatus Status,
    string? ReversalReason,
    DateTimeOffset? ReversedAt,
    Money Amount,
    Guid ReversalIdempotencyKey,
    int ReversalAuditCount,
    AllocationReversalAuditRecord? Audit);

public sealed record AllocationReversalAuditRecord(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    FinancialAuditAction Action,
    AllocationIdempotencyOperation Operation,
    Guid IdempotencyKey,
    Money Amount,
    DateTimeOffset RecordedAt,
    string? Reason);

public sealed record AllocationReversalState(AllocationStatus Status);