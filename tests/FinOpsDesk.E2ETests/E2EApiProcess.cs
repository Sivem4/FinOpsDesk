using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Microsoft.Data.SqlClient;

namespace FinOpsDesk.E2ETests;

internal static class LoopbackPort
{
    public static int Reserve()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal sealed class E2EApiProcess : IAsyncDisposable
{
    public const string Issuer = "https://finopsdesk.local";

    public const string Audience = "finopsdesk-e2e";

    private readonly Process _process;
    private readonly E2EProcessLog _log;
    private bool _disposed;

    private E2EApiProcess(Process process, E2EProcessLog log, string origin, int port)
    {
        _process = process;
        _log = log;
        Origin = origin;
        Port = port;
        ProcessId = process.Id;
    }

    public string Origin { get; }

    public int Port { get; }

    public int ProcessId { get; }

    public bool TerminationEstablished { get; private set; }

    public static async Task<E2EApiProcess> StartAsync(
        string connectionString,
        string signingKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        if (signingKey == "finopsdesk-integration-test-signing-key")
        {
            throw new InvalidOperationException("E2E refused the integration-test signing key.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException("E2E signing key is shorter than 32 UTF-8 bytes.");
        }

        string catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        E2EDatabase.RequireOwnedName(catalog);

        int port = LoopbackPort.Reserve();
        string origin = $"http://127.0.0.1:{port}";
        E2EProcessLog log = new(signingKey, connectionString);
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = RepoPaths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(RepoPaths.ApiProject);
        start.ArgumentList.Add("--no-launch-profile");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(RepoPaths.Configuration);
        start.ArgumentList.Add("--no-build");
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "E2E";
        start.Environment["Authentication__Composition"] = "DevelopmentJwt";
        start.Environment["ASPNETCORE_URLS"] = origin;
        start.Environment["ConnectionStrings__FinOpsDesk"] = connectionString;
        start.Environment["Authentication__JwtBearer__Authority"] = string.Empty;
        start.Environment["Authentication__JwtBearer__Issuer"] = Issuer;
        start.Environment["Authentication__JwtBearer__Audience"] = Audience;
        start.Environment["Authentication__JwtBearer__SigningKey"] = signingKey;
        start.Environment["Authentication__JwtBearer__OrganizationClaimType"] = "org_id";
        start.Environment["Authentication__JwtBearer__RequireHttpsMetadata"] = "true";

        Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("E2E could not start the API process.");
        }

        E2EApiProcess api;
        try
        {
            log.Attach(process);
            api = new E2EApiProcess(process, log, origin, port);
        }
        catch (Exception exception)
        {
            throw await StartupCompletion.AbandonAsync(process, exception);
        }

        await StartupCompletion.CompleteAsync(
            () => WaitForHealthAsync(process, log, origin, cancellationToken),
            api.DisposeAsync,
            () => api.TerminationEstablished);
        return api;
    }

    public static Task<E2EApiProcess> StartCookieAsync(
        string connectionString,
        string authority,
        string browserOrigin,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        return StartCookieAsync(
            connectionString,
            authority,
            browserOrigin,
            clientId,
            clientSecret,
            financialReadAuthorizationEnabled: false,
            cancellationToken);
    }

    public static async Task<E2EApiProcess> StartCookieAsync(
        string connectionString,
        string authority,
        string browserOrigin,
        string clientId,
        string clientSecret,
        bool financialReadAuthorizationEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(browserOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        string catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        E2EDatabase.RequireOwnedName(catalog);

        int port = LoopbackPort.Reserve();
        // Antiforgery refuses to issue a token unless this request is HTTPS.
        // The browser still uses http://localhost through Vite.
        string origin = $"https://localhost:{port}";
        E2EProcessLog log = new(clientSecret, connectionString);
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = RepoPaths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(RepoPaths.ApiProject);
        start.ArgumentList.Add("--no-launch-profile");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(RepoPaths.Configuration);
        start.ArgumentList.Add("--no-build");
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Authentication__Composition"] = "ProductionCookie";
        start.Environment["ASPNETCORE_URLS"] = origin;
        start.Environment["ConnectionStrings__FinOpsDesk"] = connectionString;
        start.Environment["Authentication__Oidc__Authority"] = authority;
        start.Environment["Authentication__Oidc__ClientId"] = clientId;
        start.Environment["Authentication__Oidc__ClientSecret"] = clientSecret;
        start.Environment["Authentication__Oidc__BrowserOrigin"] = browserOrigin;
        start.Environment["Authentication__Oidc__RequireHttpsMetadata"] = "false";
        start.Environment["Authentication__FinancialReadAuthorization"] = financialReadAuthorizationEnabled
            ? "Enabled"
            : string.Empty;

        Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("E2E could not start the cookie API process.");
        }

        E2EApiProcess api;
        try
        {
            log.Attach(process);
            api = new E2EApiProcess(process, log, origin, port);
        }
        catch (Exception exception)
        {
            throw await StartupCompletion.AbandonAsync(process, exception);
        }

        await StartupCompletion.CompleteAsync(
            () => WaitForHealthAsync(process, log, origin, cancellationToken),
            api.DisposeAsync,
            () => api.TerminationEstablished);
        return api;
    }

    public string Diagnostics()
    {
        return "API " + Origin + " exit=" + ProcessExit.Describe(_process) + Environment.NewLine + _log.Tail();
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
                    "E2E could not confirm the API process stopped (" + result.Detail + ").");
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    internal static HttpClient CreateHealthClient()
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    internal static async Task EnsureSuccessfulHealthAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new ApiHealthRedirectException(response.StatusCode);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode && body.Contains("Healthy", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("API health returned " + status + ".");
    }

    private static async Task WaitForHealthAsync(
        Process process,
        E2EProcessLog log,
        string origin,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHealthClient();
        string url = origin + "/health";
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"API process exited early with code {process.ExitCode}.{Environment.NewLine}{log.Tail()}");
            }

            try
            {
                using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await EnsureSuccessfulHealthAsync(client, url, requestTimeout.Token);
                return;
            }
            catch (ApiHealthRedirectException)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                lastException = exception;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new InvalidOperationException(
            $"API health at {url} was not ready.{Environment.NewLine}{log.Tail()}",
            lastException);
    }
}

internal sealed class ApiHealthRedirectException : InvalidOperationException
{
    public ApiHealthRedirectException(HttpStatusCode status)
        : base(
            "API health redirected with status " + (int)status
            + ". Loopback HTTP hosting is not sufficient for this E2E run. "
            + "HTTPS redirection was left unchanged.")
    {
    }
}