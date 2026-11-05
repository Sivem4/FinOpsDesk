namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed record ReviewCaseAuditEntryId
{
    public Guid Value { get; }

    public ReviewCaseAuditEntryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Review case audit entry id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static ReviewCaseAuditEntryId New()
    {
        return new ReviewCaseAuditEntryId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}