namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed record ReviewCaseAssignmentEntryId
{
    public Guid Value { get; }

    public ReviewCaseAssignmentEntryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Review case assignment entry id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static ReviewCaseAssignmentEntryId New()
    {
        return new ReviewCaseAssignmentEntryId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}