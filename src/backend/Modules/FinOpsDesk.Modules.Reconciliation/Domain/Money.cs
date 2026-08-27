namespace FinOpsDesk.Modules.Reconciliation.Domain;

public sealed record Money
{
    public decimal Amount { get; }

    public CurrencyCode Currency { get; }

    public Money(decimal amount, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        Amount = amount;
        Currency = currency;
    }

    public Money Add(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);

        EnsureSameCurrency(other);

        return new Money(
            Amount + other.Amount,
            Currency);
    }

    public Money Subtract(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);

        EnsureSameCurrency(other);

        return new Money(
            Amount - other.Amount,
            Currency);
    }

    public static Money Zero(CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return new Money(0m, currency);
    }

    public bool IsGreaterThan(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);

        EnsureSameCurrency(other);

        return Amount > other.Amount;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot perform a money operation between {Currency} and {other.Currency}.");
        }
    }
}