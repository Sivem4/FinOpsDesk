namespace FinOpsDesk.Api.Authentication;

internal static class BrowserReturnUrl
{
    public static bool TryNormalize(string? value, out string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            path = "/";
            return true;
        }

        string candidate = value.Trim();
        if (!candidate.StartsWith('/')
            || candidate.StartsWith("//", StringComparison.Ordinal)
            || candidate.StartsWith("/\\", StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(candidate, UriKind.Relative, out _))
        {
            path = "/";
            return false;
        }

        path = candidate;
        return true;
    }
}