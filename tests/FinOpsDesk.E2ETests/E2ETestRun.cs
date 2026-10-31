namespace FinOpsDesk.E2ETests;

internal static class E2ETestRun
{
    public static async Task RunAsync(TimeSpan limit, Func<E2EHost, Task> journey)
    {
        E2EHost host = await E2EHost.StartAsync(CancellationToken.None);
        Exception? journeyFailure = null;
        try
        {
            await host.RunWithinLimitAsync(() => journey(host), limit);
        }
        catch (Exception exception)
        {
            journeyFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (journeyFailure is not null || cleanupFailure is not null)
        {
            throw new Xunit.Sdk.XunitException(host.Describe(string.Join(
                Environment.NewLine,
                journeyFailure?.ToString() ?? string.Empty,
                cleanupFailure?.ToString() ?? string.Empty)));
        }

        Assert.Contains("database dropped " + host.DatabaseName, host.CleanupReport, StringComparison.Ordinal);
        Assert.Contains("api stopped", host.CleanupReport, StringComparison.Ordinal);
        Assert.Contains("vite stopped", host.CleanupReport, StringComparison.Ordinal);
        Assert.False(ProcessTermination.IsAlive(host.ApiProcessId));
        Assert.False(ProcessTermination.IsAlive(host.ViteProcessId));
    }
}