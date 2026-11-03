namespace FinOpsDesk.Api.Authentication;

// Absent or any value other than Enabled keeps ProductionCookie financial
// writes fail-closed. Enabled does not grant a write by itself: the read
// activation must also be Enabled, and the commit still requires the exact
// permission inside the financial transaction. DevelopmentJwt ignores this
// setting. Deployed configuration leaves the key unset.
internal static class FinancialWriteAuthorization
{
    public const string ConfigurationKey = "Authentication:FinancialWriteAuthorization";

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