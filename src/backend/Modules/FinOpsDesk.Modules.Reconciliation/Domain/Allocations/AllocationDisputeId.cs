namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public sealed record AllocationDisputeId
{
    public Guid Value { get; }

    public AllocationDisputeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Allocation dispute id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static AllocationDisputeId New()
    {
        return new AllocationDisputeId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}