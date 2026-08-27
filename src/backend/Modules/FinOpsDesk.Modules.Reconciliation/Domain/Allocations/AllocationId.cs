namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public sealed record AllocationId
{
    public Guid Value { get; }

    public AllocationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Allocation id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static AllocationId New()
    {
        return new AllocationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}