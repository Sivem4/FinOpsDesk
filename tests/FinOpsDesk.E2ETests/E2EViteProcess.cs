using System.Diagnostics;

namespace FinOpsDesk.E2ETests;

internal sealed class E2EViteLaunch
{
    public string Host { get; init; } = "127.0.0.1";

    public int? Port { get; init; }

    public bool CookieMode { get; init; }

    public string? NodeExtraCaCerts { get; init; }
}

internal sealed class E2EViteProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly E2EProcessLog _log;
    private bool _disposed;

    private E2EViteProcess(Process process, E2EProcessLog log, string origin)
    {
        _process = process;
        _log = log;
        Origin = origin;
        ProcessId = process.Id;
    }

    public string Origin { get; }

    public int ProcessId { get; }

    public bool TerminationEstablished { get; private set; }

    public static Task<E2EViteProcess> StartAsync(string apiOrigin, CancellationToken cancellationToken)
    {
        return StartAsync(apiOrigin, new E2EViteLaunch(), cancellationToken);
    }

    public static async Task<E2EViteProcess> StartAsync(
        string apiOrigin,
        E2EViteLaunch launch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiOrigin);
        ArgumentNullException.ThrowIfNull(launch);
        string viteBinary = Path.Combine(RepoPaths.FrontendDirectory, "node_modules", "vite", "package.json");
        string viteEntry = Path.Combine(RepoPaths.FrontendDirectory, "node_modules", "vite", "bin", "vite.js");
        if (!File.Exists(viteBinary) || !File.Exists(viteEntry))
        {
            throw new InvalidOperationException(
                "Vite is not installed. From src/frontend run: npm ci");
        }

        int port = launch.Port ?? LoopbackPort.Reserve();
        string origin = $"http://{launch.Host}:{port}";
        E2EProcessLog log = new();
        ProcessStartInfo start = CreateStartInfo(apiOrigin, launch, port, viteEntry);
        Process process = StartOwnedProcess(start);

        E2EViteProcess vite;
        try
        {
            log.Attach(process);
            vite = new E2EViteProcess(process, log, origin);
        }
        catch (Exception exception)
        {
            throw await StartupCompletion.AbandonAsync(process, exception);
        }

        await StartupCompletion.CompleteAsync(
            () => WaitUntilReadyAsync(process, log, origin, cancellationToken),
            vite.DisposeAsync,
            () => vite.TerminationEstablished);
        return vite;
    }

    public string Diagnostics()
    {
        return "Vite " + Origin + " exit=" + ProcessExit.Describe(_process) + Environment.NewLine + _log.Tail();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            ProcessStopResult result = await ProcessTermination.ConfirmStoppedAsync(_process, TimeSpan.FromSeconds(15));
            TerminationEstablished = result.Stopped;
            if (!TerminationEstablished)
            {
                throw new InvalidOperationException(
                    "E2E could not confirm the Vite process stopped (" + result.Detail + ").");
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string apiOrigin,
        E2EViteLaunch launch,
        int port,
        string viteEntry)
    {
        // The retained handle is the Node process that runs Vite. npm is not the
        // parent, so disposal does not leave a wrapper behind. A port or a
        // command-line fragment is not used to find or stop another process.
        ProcessStartInfo start = new()
        {
            FileName = "node",
            WorkingDirectory = RepoPaths.FrontendDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(viteEntry);
        if (launch.CookieMode)
        {
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add("cookie");
        }

        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(launch.Host);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString());
        start.ArgumentList.Add("--strictPort");
        start.Environment["FINOPSDESK_E2E_API_ORIGIN"] = apiOrigin;
        start.Environment["BROWSER"] = "none";
        start.Environment.Remove("NODE_TLS_REJECT_UNAUTHORIZED");
        if (launch.CookieMode)
        {
            start.Environment["VITE_AUTH_MODE"] = "cookie";
        }

        if (!string.IsNullOrWhiteSpace(launch.NodeExtraCaCerts))
        {
            start.Environment["NODE_EXTRA_CA_CERTS"] = launch.NodeExtraCaCerts;
        }

        return start;
    }

    internal static Process StartOwnedProcess(ProcessStartInfo start)
    {
        Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("E2E could not start the Vite process.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static async Task WaitUntilReadyAsync(
        Process process,
        E2EProcessLog log,
        string origin,
        CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Vite process exited early with code {process.ExitCode}.{Environment.NewLine}{log.Tail()}");
            }

            try
            {
                using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using HttpResponseMessage page = await client.GetAsync(origin, requestTimeout.Token);
                using HttpResponseMessage health = await client.GetAsync(origin + "/health", requestTimeout.Token);
                string pageBody = await page.Content.ReadAsStringAsync(requestTimeout.Token);
                string healthBody = await health.Content.ReadAsStringAsync(requestTimeout.Token);
                if (page.IsSuccessStatusCode
                    && pageBody.Contains("FinOpsDesk", StringComparison.Ordinal)
                    && health.IsSuccessStatusCode
                    && healthBody.Contains("Healthy", StringComparison.Ordinal))
                {
                    return;
                }

                lastException = new InvalidOperationException(
                    $"Vite readiness returned page {(int)page.StatusCode} and health {(int)health.StatusCode}.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Vite at {origin} was not ready, or /health did not reach the E2E API.{Environment.NewLine}{log.Tail()}",
            lastException);
    }
}