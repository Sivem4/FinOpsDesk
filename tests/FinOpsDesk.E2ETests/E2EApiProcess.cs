using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

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
        CancellationToken cancellationToken,
        bool financialWriteAuthorizationEnabled = false)
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
        start.Environment["Authentication__FinancialWriteAuthorization"] = financialWriteAuthorizationEnabled
            ? "Enabled"
            : string.Empty;

        DevelopmentCertificateExport.DevelopmentCertificateMaterial material = DevelopmentCertificateExport.LoadMaterial();
        using X509Certificate2 trusted = X509CertificateLoader.LoadCertificate(material.PublicDer);
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

        return await CompleteStartupAsync(
            api,
            () => WaitForHealthAsync(process, log, origin, cancellationToken, trusted));
    }

    internal static async Task<E2EApiProcess> CompleteStartupAsync(E2EApiProcess api, Func<Task> waitUntilReady)
    {
        try
        {
            await StartupCompletion.CompleteAsync(waitUntilReady, api.DisposeAsync, () => api.TerminationEstablished);
            return api;
        }
        catch (ProcessStartupException exception)
        {
            throw new E2EApiStartupException(api, exception);
        }
    }

    public string Diagnostics()
    {
        return "API " + Origin + " exit=" + ProcessExit.Describe(_process) + Environment.NewLine + _log.Tail();
    }

    public async ValueTask DisposeAsync()
    {
        if (TerminationEstablished)
        {
            return;
        }

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
            if (TerminationEstablished)
            {
                _process.Dispose();
            }
        }
    }

    internal static E2EApiProcess ForTerminationTest(Process process)
    {
        return new E2EApiProcess(process, new E2EProcessLog(), "http://127.0.0.1", 9);
    }

    internal static HttpClient CreateHealthClient(X509Certificate2? trustedDevelopmentCertificate = null)
    {
        HttpClientHandler handler = new() { AllowAutoRedirect = false };
        if (trustedDevelopmentCertificate is not null)
        {
            X509Certificate2 trusted = trustedDevelopmentCertificate;
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                AllowsDevelopmentCertificate(request.RequestUri, certificate, chain, errors, trusted);
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    internal static bool AllowsDevelopmentCertificate(
        Uri? requestUri,
        X509Certificate2? presented,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2 trusted)
    {
        if (requestUri is null
            || presented is null
            || chain is null
            || !requestUri.IsAbsoluteUri
            || !string.Equals(requestUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        if ((errors & ~(SslPolicyErrors.None | SslPolicyErrors.RemoteCertificateChainErrors)) != 0)
        {
            return false;
        }

        if (!string.Equals(presented.Thumbprint, trusted.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The platform chain was built against the system trust store. Rebuild it
        // against only this certificate. chain.Build checks time and signature.
        // It does not check the requested hostname; that is the SslPolicyErrors
        // name check above. The development certificate has no CRL, so revocation
        // is not consulted. CustomRootTrust accepts this self-signed end-entity
        // certificate as the sole trust anchor.
        using X509Chain custom = new();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(trusted);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        custom.ChainPolicy.VerificationTime = DateTime.UtcNow;
        return custom.Build(presented);
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

    internal static bool IsPermanentCertificateFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task WaitForHealthAsync(
        Process process,
        E2EProcessLog log,
        string origin,
        CancellationToken cancellationToken,
        X509Certificate2? trustedDevelopmentCertificate = null)
    {
        if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && trustedDevelopmentCertificate is null)
        {
            throw new InvalidOperationException(
                "The cookie HTTPS health probe has no development certificate. Startup was not continued.");
        }

        using HttpClient client = CreateHealthClient(trustedDevelopmentCertificate);
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
                if (IsPermanentCertificateFailure(exception))
                {
                    throw new InvalidOperationException(
                        "API health failed TLS certificate validation for " + origin
                        + ". The probe trusts only the ASP.NET Core localhost development certificate. Startup was not retried.",
                        exception);
                }

                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new InvalidOperationException(
            $"API health at {url} was not ready.{Environment.NewLine}{log.Tail()}",
            lastException);
    }
}

internal sealed class E2EApiStartupException : Exception
{
    public E2EApiStartupException(E2EApiProcess api, Exception innerException)
        : base("Cookie API startup failed.", innerException)
    {
        Api = api;
    }

    public E2EApiProcess Api { get; }
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