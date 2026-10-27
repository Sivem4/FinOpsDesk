namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed record ReviewCaseSource
{
    public const int MaxLength = 16;

    public static ReviewCaseSource Manual { get; } = new("manual");

    public static ReviewCaseSource Exact1 { get; } = new("exact-1");

    public static ReviewCaseSource Smart1 { get; } = new("smart-1");

    public string Value { get; }

    private ReviewCaseSource(string value)
    {
        Value = value;
    }

    public static ReviewCaseSource Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim();
        if (normalized.Equals(Manual.Value, StringComparison.Ordinal))
        {
            return Manual;
        }

        if (normalized.Equals(Exact1.Value, StringComparison.Ordinal))
        {
            return Exact1;
        }

        if (normalized.Equals(Smart1.Value, StringComparison.Ordinal))
        {
            return Smart1;
        }

        throw new ArgumentException(
            "Review source is not recognized.",
            nameof(value));
    }

    public override string ToString()
    {
        return Value;
    }
}