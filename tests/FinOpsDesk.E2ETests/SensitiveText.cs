using System.Text.RegularExpressions;

namespace FinOpsDesk.E2ETests;

internal static partial class SensitiveText
{
    public static string Redact(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = text;
        foreach (string? secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        redacted = PasswordPattern().Replace(redacted, "$1=[redacted]");
        redacted = AuthorizationPattern().Replace(redacted, "Authorization: [redacted]");
        return JwtPattern().Replace(redacted, "[redacted-jwt]");
    }

    [GeneratedRegex("(?i)(Password|Pwd)\\s*=\\s*[^;\\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex("(?i)Authorization\\s*:\\s*(?:Bearer\\s+)?\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();
}