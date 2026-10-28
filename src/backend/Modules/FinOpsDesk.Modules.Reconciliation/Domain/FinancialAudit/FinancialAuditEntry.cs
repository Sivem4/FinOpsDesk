using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

public sealed class FinancialAuditEntry
{
    public const int MaxActorIdLength = 256;

    public FinancialAuditEntryId Id { get; }

    public OrganizationId OrganizationId { get; }

    public AllocationId AllocationId { get; }

    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public BatchExecutionId? BatchExecutionId { get; }

    public FinancialAuditAction Action { get; }

    public string ActorId { get; }

    public Money Amount { get; }

    public DateTimeOffset RecordedAt { get; }

    public AllocationIdempotencyOperation IdempotencyOperation { get; }

    public Guid IdempotencyKey { get; }

#pragma warning disable CS8618
    private FinancialAuditEntry()
    {
    }
#pragma warning restore CS8618

    private FinancialAuditEntry(
        FinancialAuditEntryId id,
        OrganizationId organizationId,
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        BatchExecutionId? batchExecutionId,
        FinancialAuditAction action,
        string actorId,
        Money amount,
        DateTimeOffset recordedAt,
        AllocationIdempotencyOperation idempotencyOperation,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);

        if (action != FinancialAuditAction.Confirmation || !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                "Only a confirmation audit entry can be recorded.");
        }

        if (actorId.Length == 0 || actorId.Length > MaxActorIdLength || actorId != actorId.Trim())
        {
            throw new ArgumentException(
                "Actor id must be a trimmed non-blank value of at most 256 characters.",
                nameof(actorId));
        }

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Audit amount must be greater than zero.");
        }

        if (recordedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Audit time cannot be the default value.");
        }

        if (idempotencyKey == Guid.Empty || !Enum.IsDefined(idempotencyOperation))
        {
            throw new ArgumentException(
                "A confirmation audit requires the allocation idempotency key and operation.",
                nameof(idempotencyKey));
        }

        if (idempotencyOperation == AllocationIdempotencyOperation.BatchExactMatch)
        {
            ArgumentNullException.ThrowIfNull(batchExecutionId);
        }
        else if (batchExecutionId is not null)
        {
            throw new ArgumentException(
                "A batch execution id is only valid for a batch exact-match confirmation.",
                nameof(batchExecutionId));
        }

        Id = id;
        OrganizationId = organizationId;
        AllocationId = allocationId;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        BatchExecutionId = batchExecutionId;
        Action = action;
        ActorId = actorId;
        Amount = amount;
        RecordedAt = recordedAt;
        IdempotencyOperation = idempotencyOperation;
        IdempotencyKey = idempotencyKey;
    }

    public static FinancialAuditEntry ForConfirmation(
        FinancialAuditEntryId id,
        Allocation allocation,
        string actorId,
        BatchExecutionId? batchExecutionId)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(actorId);

        if (allocation.Status != AllocationStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "A confirmation audit requires an allocation that is currently confirmed.");
        }

        if (allocation.IdempotencyKey is not Guid idempotencyKey
            || allocation.IdempotencyOperation is not AllocationIdempotencyOperation idempotencyOperation)
        {
            throw new ArgumentException(
                "A confirmation audit requires the allocation idempotency key and operation.",
                nameof(allocation));
        }

        return new FinancialAuditEntry(
            id,
            allocation.OrganizationId,
            allocation.Id,
            allocation.InvoiceId,
            allocation.BankTransactionId,
            batchExecutionId,
            FinancialAuditAction.Confirmation,
            actorId.Trim(),
            allocation.Amount,
            allocation.ConfirmedAt,
            idempotencyOperation,
            idempotencyKey);
    }
}