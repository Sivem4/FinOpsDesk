namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed record BatchExecutionId
{
    public Guid Value { get; }

    public BatchExecutionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Batch execution id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static BatchExecutionId New()
    {
        return new BatchExecutionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}