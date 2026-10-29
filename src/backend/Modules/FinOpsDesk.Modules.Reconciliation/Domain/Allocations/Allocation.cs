using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public sealed class Allocation
{
    public const int MaxReversalReasonLength = 500;

    public AllocationId Id { get; }

    public OrganizationId OrganizationId { get; }

    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money Amount { get; }

    public AllocationStatus Status { get; private set; }

    public DateTimeOffset ConfirmedAt { get; }

    public Guid? IdempotencyKey { get; }

    public AllocationIdempotencyOperation? IdempotencyOperation { get; }

    public DateTimeOffset? ReversedAt { get; private set; }

    public string? ReversalReason { get; private set; }

#pragma warning disable CS8618
    // Required for persistence materialization.
    private Allocation()
    {
    }
#pragma warning restore CS8618

    public Allocation(
        AllocationId id,
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        DateTimeOffset confirmedAt,
        Guid? idempotencyKey = null,
        AllocationIdempotencyOperation? idempotencyOperation = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        if (confirmedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmedAt),
                "Confirmation time cannot be the default value.");
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        if (idempotencyKey is null)
        {
            if (idempotencyOperation is not null)
            {
                throw new ArgumentException(
                    "An idempotency operation requires an idempotency key.",
                    nameof(idempotencyOperation));
            }
        }
        else if (idempotencyOperation is null
            || idempotencyOperation == AllocationIdempotencyOperation.Reversal
            || !Enum.IsDefined(idempotencyOperation.Value))
        {
            throw new ArgumentException(
                "An idempotency key requires a valid idempotency operation.",
                nameof(idempotencyOperation));
        }

        Id = id;
        OrganizationId = organizationId;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Amount = amount;
        ConfirmedAt = confirmedAt;
        IdempotencyKey = idempotencyKey;
        IdempotencyOperation = idempotencyOperation;
        Status = AllocationStatus.Confirmed;
    }

    public Result Reverse(
        string reason,
        DateTimeOffset reversedAt)
    {
        Result validation = ValidateReversal(reason, reversedAt);

        if (validation.IsFailure)
        {
            return validation;
        }

        Status = AllocationStatus.Reversed;
        ReversedAt = reversedAt;
        ReversalReason = reason.Trim();
        return Result.Success();
    }

    internal Result ValidateReversal(
        string reason,
        DateTimeOffset reversedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (reversedAt < ConfirmedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reversedAt),
                "Reversal time cannot precede confirmation time.");
        }

        if (Status == AllocationStatus.Reversed)
        {
            return Result.Failure(ReconciliationErrors.AllocationAlreadyReversed);
        }

        if (reason.Trim().Length > MaxReversalReasonLength)
        {
            return Result.Failure(ReconciliationErrors.AllocationReversalReasonTooLong);
        }

        return Result.Success();
    }
}