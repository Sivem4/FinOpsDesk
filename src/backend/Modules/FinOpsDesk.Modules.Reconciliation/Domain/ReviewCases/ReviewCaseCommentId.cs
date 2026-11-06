namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed record ReviewCaseCommentId
{
    public Guid Value { get; }

    public ReviewCaseCommentId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Review case comment id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static ReviewCaseCommentId New()
    {
        return new ReviewCaseCommentId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}