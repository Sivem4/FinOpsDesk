namespace FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

public sealed record InvoiceId
{
    public Guid Value { get; }

    public InvoiceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Invoice id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static InvoiceId New()
    {
        return new InvoiceId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}