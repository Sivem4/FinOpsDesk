namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record ExternalId
{
    public const int MaxLength = 128;

    public string Value { get; }

    public ExternalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim();

        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException(
                $"External id cannot exceed {MaxLength} characters.",
                nameof(value));
        }

        Value = normalized;
    }

    public override string ToString()
    {
        return Value;
    }
}