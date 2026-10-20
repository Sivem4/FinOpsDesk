namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record ImportBatchId
{
    public Guid Value { get; }

    public ImportBatchId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Import batch id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static ImportBatchId New()
    {
        return new ImportBatchId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}