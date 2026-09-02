using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;

public sealed class BankTransaction
{
    public BankTransactionId Id { get; }

    public OrganizationId OrganizationId { get; }

    public Money Amount { get; }

    public Money AllocatedAmount { get; private set; }

    public Money UnappliedAmount =>
        Amount.Subtract(AllocatedAmount);

    public string? Reference { get; }

    public DateOnly BookingDate { get; }

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
        Amount = amount;
        BookingDate = bookingDate;
        Reference = NormalizeReference(reference);

        AllocatedAmount = Money.Zero(amount.Currency);
    }

    public Result Allocate(Money amount)
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

        AllocatedAmount = AllocatedAmount.Add(amount);
        return Result.Success();
    }

    public Result ReverseAllocation(Money amount)
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