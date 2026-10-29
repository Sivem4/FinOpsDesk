using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;

public sealed class BankTransaction
{
    public BankTransactionId Id { get; }

    public OrganizationId OrganizationId { get; }

    public SourceIdentity? SourceIdentity { get; }

    public Money Amount { get; }

    public Money AllocatedAmount { get; private set; }

    public Money UnappliedAmount =>
        Amount.Subtract(AllocatedAmount);

    public string? Reference { get; }

    public DateOnly BookingDate { get; }

#pragma warning disable CS8618
    // Required for persistence materialization.
    private BankTransaction()
    {
    }
#pragma warning restore CS8618

    public BankTransaction(
        BankTransactionId id,
        OrganizationId organizationId,
        Money amount,
        DateOnly bookingDate,
        string? reference)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Incoming bank transaction amount must be greater than zero.");
        }

        Id = id;
        OrganizationId = organizationId;
        SourceIdentity = null;
        Amount = amount;
        BookingDate = bookingDate;
        Reference = NormalizeReference(reference);

        AllocatedAmount = Money.Zero(amount.Currency);
    }

    public BankTransaction(
        BankTransactionId id,
        OrganizationId organizationId,
        Money amount,
        DateOnly bookingDate,
        string? reference,
        SourceIdentity sourceIdentity)
        : this(
            id,
            organizationId,
            amount,
            bookingDate,
            reference)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);

        SourceIdentity = sourceIdentity;
    }

    public bool HasEquivalentImportedBusinessData(
        Money amount,
        DateOnly bookingDate,
        string? reference)
    {
        ArgumentNullException.ThrowIfNull(amount);

        return Amount == amount
            && BookingDate == bookingDate
            && string.Equals(
                Reference,
                NormalizeReference(reference),
                StringComparison.Ordinal);
    }

    public Result CanAllocate(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        if (!amount.HasSameCurrency(Amount))
        {
            return Result.Failure(ReconciliationErrors.CurrencyMismatch);
        }

        if (amount.IsGreaterThan(UnappliedAmount))
        {
            return Result.Failure(
                ReconciliationErrors.BankTransactionAllocationExceedsUnapplied);
        }

        return Result.Success();
    }

    public Result Allocate(Money amount)
    {
        Result canAllocate = CanAllocate(amount);

        if (canAllocate.IsFailure)
        {
            return canAllocate;
        }

        AllocatedAmount = AllocatedAmount.Add(amount);
        return Result.Success();
    }

    public Result CanReverseAllocation(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Reversal amount must be greater than zero.");
        }

        if (!amount.HasSameCurrency(Amount))
        {
            return Result.Failure(ReconciliationErrors.CurrencyMismatch);
        }

        if (amount.IsGreaterThan(AllocatedAmount))
        {
            return Result.Failure(
                ReconciliationErrors.BankTransactionReversalExceedsAllocated);
        }

        return Result.Success();
    }

    public Result ReverseAllocation(Money amount)
    {
        Result canReverse = CanReverseAllocation(amount);

        if (canReverse.IsFailure)
        {
            return canReverse;
        }

        AllocatedAmount = AllocatedAmount.Subtract(amount);
        return Result.Success();
    }

    private static string? NormalizeReference(string? reference)
    {
        return string.IsNullOrWhiteSpace(reference)
            ? null
            : reference.Trim();
    }
}