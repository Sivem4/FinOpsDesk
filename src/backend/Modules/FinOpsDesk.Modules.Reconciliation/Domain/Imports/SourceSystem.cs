namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record SourceSystem
{
    public const int MaxLength = 64;

    public string Value { get; }

    public SourceSystem(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Source system cannot exceed {MaxLength} characters.",
                nameof(value));
        }

        Value = normalized;
    }

    public override string ToString()
    {
        return Value;
    }
}