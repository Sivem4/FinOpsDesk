namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record FileHash
{
    public const int HexLength = 64;

    public string Value { get; }

    public FileHash(string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hex);

        string canonical = sha256Hex.Trim().ToLowerInvariant();

        if (canonical.Length != HexLength ||
            canonical.Any(character => !IsLowerHexadecimal(character)))
        {
            throw new ArgumentException(
                "File hash must be a 64-character SHA-256 hexadecimal digest.",
                nameof(sha256Hex));
        }

        Value = canonical;
    }

    public override string ToString()
    {
        return Value;
    }

    private static bool IsLowerHexadecimal(char character)
    {
        return character is (>= '0' and <= '9') or (>= 'a' and <= 'f');
    }
}