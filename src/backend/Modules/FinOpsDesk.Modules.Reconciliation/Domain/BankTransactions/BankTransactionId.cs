namespace FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;

public sealed record BankTransactionId
{
    public Guid Value { get; }

    public BankTransactionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Bank transaction id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static BankTransactionId New()
    {
        return new BankTransactionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}