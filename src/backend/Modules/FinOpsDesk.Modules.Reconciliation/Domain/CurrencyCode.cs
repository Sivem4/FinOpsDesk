namespace FinOpsDesk.Modules.Reconciliation.Domain;

public sealed record CurrencyCode
{
    public string Value { get; }

    public CurrencyCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalizedValue = value.Trim().ToUpperInvariant();

        if (normalizedValue.Length != 3 ||
            normalizedValue.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException(
                "Currency code must consist of exactly three letters.",
                nameof(value));
        }

        Value = normalizedValue;
    }

    public override string ToString() => Value;
}