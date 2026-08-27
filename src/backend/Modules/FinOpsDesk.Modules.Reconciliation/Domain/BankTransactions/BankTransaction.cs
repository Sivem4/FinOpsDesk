using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;

public sealed class BankTransaction
{
    public BankTransactionId Id { get; }

    public Money Amount { get; }

    public Money AllocatedAmount { get; private set; }

    public Money UnappliedAmount =>
        Amount.Subtract(AllocatedAmount);

    public string? Reference { get; }

    public DateOnly BookingDate { get; }

    public BankTransaction(
        BankTransactionId id,
        Money amount,
        DateOnly bookingDate,
        string? reference)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Incoming bank transaction amount must be greater than zero.");
        }

        Id = id;
        Amount = amount;
        BookingDate = bookingDate;
        Reference = NormalizeReference(reference);

        AllocatedAmount = Money.Zero(amount.Currency);
    }

    public void Allocate(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        EnsureSameCurrency(amount);

        if (amount.Amount > UnappliedAmount.Amount)
        {
            throw new InvalidOperationException(
                "Allocation amount cannot exceed unapplied transaction amount.");
        }

        AllocatedAmount = AllocatedAmount.Add(amount);
    }

    public void ReverseAllocation(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Reversal amount must be greater than zero.");
        }

        EnsureSameCurrency(amount);

        if (amount.Amount > AllocatedAmount.Amount)
        {
            throw new InvalidOperationException(
                "Reversal amount cannot exceed allocated transaction amount.");
        }

        AllocatedAmount = AllocatedAmount.Subtract(amount);
    }

    private void EnsureSameCurrency(Money amount)
    {
        if (Amount.Currency != amount.Currency)
        {
            throw new InvalidOperationException(
                $"Bank transaction currency is {Amount.Currency}, but operation currency is {amount.Currency}.");
        }
    }

    private static string? NormalizeReference(string? reference)
    {
        return string.IsNullOrWhiteSpace(reference)
            ? null
            : reference.Trim();
    }
}