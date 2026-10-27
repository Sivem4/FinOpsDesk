namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed record ReviewCaseId
{
    public Guid Value { get; }

    public ReviewCaseId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Review case id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static ReviewCaseId New()
    {
        return new ReviewCaseId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}