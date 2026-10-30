namespace FinOpsDesk.Modules.Reconciliation.Application;

internal static class ExactLookupTerm
{
    public const int MaximumLength = 256;

    public static bool TryNormalize(string? supplied, out string? term)
    {
        term = null;

        if (supplied is null)
        {
            return true;
        }

        string trimmed = supplied.Trim();

        if (trimmed.Length == 0 || trimmed.Length > MaximumLength)
        {
            return false;
        }

        term = trimmed;
        return true;
    }
}