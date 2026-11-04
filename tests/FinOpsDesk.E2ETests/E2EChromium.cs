namespace FinOpsDesk.E2ETests;

internal static class E2EChromium
{
    public static InvalidOperationException Unavailable(Exception exception)
    {
        return new InvalidOperationException(
            "Chromium is not available. Build the E2E project, then run playwright.ps1 install chromium from its output directory. "
            + SensitiveText.Redact(exception.Message),
            exception);
    }
}