namespace FinOpsDesk.Api.Authentication;

// Absent or any value other than Enabled keeps ProductionCookie financial
// routes fail-closed. Enabled does not grant access by itself: the financial
// policy still requires an explicit read classification and the caller's
// current permission. DevelopmentJwt ignores this setting.
internal static class FinancialReadAuthorization
{
    public const string ConfigurationKey = "Authentication:FinancialReadAuthorization";

    public const string Enabled = "Enabled";

    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.Equals(
            configuration[ConfigurationKey],
            Enabled,
            StringComparison.Ordinal);
    }
}