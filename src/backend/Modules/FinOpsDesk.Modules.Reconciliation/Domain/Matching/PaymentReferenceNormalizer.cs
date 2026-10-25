namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class PaymentReferenceNormalizer
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant();
    }

    internal static void EnsureCanonical(string? normalizedReference, string parameterName)
    {
        if (normalizedReference is null)
        {
            return;
        }

        if (!string.Equals(
                normalizedReference,
                Normalize(normalizedReference),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Payment reference must already use exact-1 normalization.",
                parameterName);
        }
    }
}