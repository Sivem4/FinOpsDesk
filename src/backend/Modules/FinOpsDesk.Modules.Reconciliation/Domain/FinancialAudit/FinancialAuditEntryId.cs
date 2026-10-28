namespace FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

public sealed record FinancialAuditEntryId
{
    public Guid Value { get; }

    public FinancialAuditEntryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Financial audit entry id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static FinancialAuditEntryId New()
    {
        return new FinancialAuditEntryId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}