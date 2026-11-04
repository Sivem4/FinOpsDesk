using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FinOpsDesk.E2ETests;

public sealed class E2ELifecycleTests
{
    [Fact]
    public async Task Completed_journey_does_not_stop_the_browser()
    {
        bool stopped = false;
        await E2EJourneyLimit.WaitAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            () =>
            {
                stopped = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.False(stopped);
    }

    [Fact]
    public async Task Timed_out_journey_stops_before_later_financial_work()
    {
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopRequested = false;
        bool financialStep = false;
        Task journey = Task.Run(async () =>
        {
            await release.Task;
            if (!stopRequested)
            {
                financialStep = true;
            }
        });

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            E2EJourneyLimit.WaitAsync(
                journey,
                TimeSpan.FromMilliseconds(100),
                () =>
                {
                    stopRequested = true;
                    release.TrySetResult(true);
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(2)));

        Assert.False(financialStep);
        Assert.Contains("Browser work stopped before cleanup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journey_that_does_not_stop_blocks_the_owned_database_drop()
    {
        using CancellationTokenSource hanging = new();
        Task journey = Task.Delay(Timeout.InfiniteTimeSpan, hanging.Token);
        try
        {
            E2EJourneyOutstandingException exception = await Assert.ThrowsAsync<E2EJourneyOutstandingException>(() =>
                E2EJourneyLimit.WaitAsync(
                    journey,
                    TimeSpan.FromMilliseconds(100),
                    () => Task.CompletedTask,
                    TimeSpan.FromMilliseconds(100)));

            E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
                browserWorkOutstanding: true,
                apiTerminationEstablished: true,
                viteTerminationEstablished: true,
                browserStopFailed: false);

            Assert.False(decision.DropOwnedDatabase);
            Assert.True(decision.FailForProcessLifecycle);
            Assert.Contains("will not be dropped", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            hanging.Cancel();
            try
            {
                await journey;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public void Unconfirmed_api_stop_leaves_the_owned_database()
    {
        E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
            browserWorkOutstanding: false,
            apiTerminationEstablished: false,
            viteTerminationEstablished: true,
            browserStopFailed: false);

        Assert.False(decision.DropOwnedDatabase);
        Assert.True(decision.FailForProcessLifecycle);
    }

    [Fact]
    public void Vite_stop_failure_still_fails_after_a_confirmed_api_stop()
    {
        E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
            browserWorkOutstanding: false,
            apiTerminationEstablished: true,
            viteTerminationEstablished: false,
            browserStopFailed: false);

        Assert.True(decision.DropOwnedDatabase);
        Assert.True(decision.FailForProcessLifecycle);
    }

    [Fact]
    public void Confirmed_process_stop_allows_the_owned_database_drop()
    {
        E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
            browserWorkOutstanding: false,
            apiTerminationEstablished: true,
            viteTerminationEstablished: true,
            browserStopFailed: false);

        Assert.True(decision.DropOwnedDatabase);
        Assert.False(decision.FailForProcessLifecycle);
    }

    [Fact]
    public async Task Live_process_stop_is_confirmed_and_a_disposed_process_is_not()
    {
        Process? process = StartIdleProcess();
        try
        {
            int processId = process.Id;
            ProcessStopResult stopped = await ProcessTermination.ConfirmStoppedAsync(process, TimeSpan.FromSeconds(15));
            Assert.True(stopped.Stopped);
            Assert.False(ProcessTermination.IsAlive(processId));

            process.Dispose();
            Process disposed = process;
            process = null;
            ProcessStopResult unknown = await ProcessTermination.ConfirmStoppedAsync(disposed, TimeSpan.FromSeconds(1));
            Assert.False(unknown.Stopped);

            E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
                browserWorkOutstanding: false,
                apiTerminationEstablished: unknown.Stopped,
                viteTerminationEstablished: true,
                browserStopFailed: false);
            Assert.False(decision.DropOwnedDatabase);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }
        }
    }

    [Fact]
    public async Task Health_probe_rejects_the_original_redirect_status()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(() => WriteHttpResponseAsync(listener, "307 Temporary Redirect"));

        using HttpClient client = E2EApiProcess.CreateHealthClient();
        ApiHealthRedirectException exception = await Assert.ThrowsAsync<ApiHealthRedirectException>(() =>
            E2EApiProcess.EnsureSuccessfulHealthAsync(client, "http://127.0.0.1:" + port + "/health", CancellationToken.None));

        await server;
        Assert.Contains("307", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS redirection was left unchanged", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_probe_accepts_a_healthy_loopback_response()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(() => WriteHttpResponseAsync(listener, "200 OK", "Healthy"));

        using HttpClient client = E2EApiProcess.CreateHealthClient();
        await E2EApiProcess.EnsureSuccessfulHealthAsync(
            client,
            "http://127.0.0.1:" + port + "/health",
            CancellationToken.None);
        await server;
    }

    [Fact]
    public async Task Https_health_client_accepts_the_localhost_development_certificate()
    {
        using X509Certificate2 serverCertificate = DevelopmentCertificateExport.CopyServerCertificate();
        using X509Certificate2 trusted = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
        string body = await ReadHttpsHealthAsync(serverCertificate, trusted, "https://localhost:{0}/health");
        Assert.Equal("Healthy", body);
        Assert.False(body.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Https_health_client_rejects_a_substituted_certificate()
    {
        using X509Certificate2 serverCertificate = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using X509Certificate2 development = DevelopmentCertificateExport.CopyServerCertificate();
        using X509Certificate2 trusted = X509CertificateLoader.LoadCertificate(development.Export(X509ContentType.Cert));
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ReadHttpsHealthAsync(serverCertificate, trusted, "https://localhost:{0}/health"));
        Assert.IsType<AuthenticationException>(exception.InnerException);
        Assert.False(exception.ToString().Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Https_health_client_rejects_a_different_hostname()
    {
        using X509Certificate2 serverCertificate = CreateCertificate("example.com", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using X509Certificate2 trusted = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ReadHttpsHealthAsync(serverCertificate, trusted, "https://localhost:{0}/health"));
        Assert.IsType<AuthenticationException>(exception.InnerException);
    }

    [Fact]
    public async Task Https_health_client_rejects_an_expired_certificate_without_retrying()
    {
        using X509Certificate2 serverCertificate = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddMinutes(-5));
        using X509Certificate2 trusted = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
        Process? process = StartIdleProcess();
        using TcpListener listener = StartHttpsListener();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeHttpsAsync(listener, serverCertificate);
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                E2EApiProcess.WaitForHealthAsync(
                    process,
                    new E2EProcessLog(),
                    "https://localhost:" + port,
                    CancellationToken.None,
                    trusted));
            watch.Stop();
            Assert.Contains("Startup was not retried", exception.Message, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
            Assert.False(exception.ToString().Contains("PRIVATE KEY", StringComparison.Ordinal));
        }
        finally
        {
            await SwallowServerAsync(server);
            StopProcess(process);
        }
    }

    [Fact]
    public async Task Missing_development_certificate_fails_the_https_probe_closed()
    {
        Process? process = StartIdleProcess();
        try
        {
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                E2EApiProcess.WaitForHealthAsync(
                    process,
                    new E2EProcessLog(),
                    "https://localhost:9",
                    CancellationToken.None,
                    trustedDevelopmentCertificate: null));
            Assert.Contains("no development certificate", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            StopProcess(process);
        }
    }

    [Fact]
    public async Task Confirmed_api_stop_can_be_disposed_twice_and_an_unconfirmed_stop_keeps_the_database()
    {
        Process? running = StartIdleProcess();
        try
        {
            int processId = running.Id;
            E2EApiProcess api = E2EApiProcess.ForTerminationTest(running);
            await api.DisposeAsync();
            Assert.True(api.TerminationEstablished);
            Assert.False(ProcessTermination.IsAlive(processId));
            await api.DisposeAsync();
            running = null;

            Process detached = StartIdleProcess();
            int detachedId = detached.Id;
            E2EApiProcess unconfirmed = E2EApiProcess.ForTerminationTest(detached);
            detached.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => unconfirmed.DisposeAsync().AsTask());
            Assert.False(unconfirmed.TerminationEstablished);
            E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
                browserWorkOutstanding: false,
                apiTerminationEstablished: unconfirmed.TerminationEstablished,
                viteTerminationEstablished: true,
                browserStopFailed: false);
            Assert.False(decision.DropOwnedDatabase);
            using Process leftover = Process.GetProcessById(detachedId);
            leftover.Kill(entireProcessTree: true);
        }
        finally
        {
            StopProcess(running);
        }

        E2EApiProcess? visible = E2EApiProcess.ForTerminationTest(StartIdleProcess());
        try
        {
            E2EApiStartupException exposed = await Assert.ThrowsAsync<E2EApiStartupException>(() =>
                E2EApiProcess.CompleteStartupAsync(
                    visible,
                    () => throw new InvalidOperationException(
                        "API health failed TLS certificate validation for https://localhost:9. Startup was not retried.")));
            Assert.Same(visible, exposed.Api);
            Assert.True(exposed.Api.TerminationEstablished);
            E2ECleanupDecision drop = E2ECleanupPolicy.Decide(
                browserWorkOutstanding: false,
                apiTerminationEstablished: exposed.Api.TerminationEstablished,
                viteTerminationEstablished: true,
                browserStopFailed: false);
            Assert.True(drop.DropOwnedDatabase);
            visible = null;
        }
        finally
        {
            if (visible is not null)
            {
                await visible.DisposeAsync();
            }
        }
    }

    private static async Task<string> ReadHttpsHealthAsync(
        X509Certificate2 serverCertificate,
        X509Certificate2 trusted,
        string urlFormat)
    {
        using TcpListener listener = StartHttpsListener();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeHttpsAsync(listener, serverCertificate);
        Exception? clientFailure = null;
        try
        {
            using HttpClient client = E2EApiProcess.CreateHealthClient(trusted);
            using HttpResponseMessage response = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, urlFormat, port));
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception)
        {
            clientFailure = exception;
            throw;
        }
        finally
        {
            await SwallowServerAsync(server, clientFailure);
        }
    }

    private static TcpListener StartHttpsListener()
    {
        TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = true;
        listener.Start();
        return listener;
    }

    private static async Task ServeHttpsAsync(TcpListener listener, X509Certificate2 serverCertificate)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream network = client.GetStream();
        await using SslStream ssl = new(network);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null),
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            ClientCertificateRequired = false,
        });
        byte[] buffer = new byte[1024];
        using MemoryStream received = new();
        while (true)
        {
            int read = await ssl.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
            if (Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string body = "Healthy";
        byte[] payload = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
        await ssl.WriteAsync(payload);
    }

    private static async Task SwallowServerAsync(Task server, Exception? clientFailure = null)
    {
        try
        {
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) when (clientFailure is not null || server.IsFaulted || server.IsCanceled)
        {
        }
    }

    private static X509Certificate2 CreateCertificate(string dnsName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=" + dnsName, key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        OidCollection serverAuthentication = new()
        {
            new Oid("1.3.6.1.5.5.7.3.1"),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuthentication, critical: false));
        X509Certificate2 created = request.CreateSelfSigned(notBefore, notAfter);
        string password = Guid.NewGuid().ToString("N");
        byte[] pfx = created.Export(X509ContentType.Pfx, password);
        created.Dispose();
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    [Fact]
    public void Diagnostics_redact_tokens_passwords_and_authorization_headers()
    {
        const string signingKey = "signing-key-value-that-is-not-logged";
        string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJlMmUtb3BlcmF0b3IifQ.c2lnbmF0dXJl";
        string input = string.Join(
            " ",
            "Password=s3cret;",
            "Authorization: Bearer opaque-token",
            jwt,
            signingKey,
            "Server=(localdb)\\mssqllocaldb;Password=s3cret;TrustServerCertificate=True");

        string output = SensitiveText.Redact(input, signingKey);

        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-token", output, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, output, StringComparison.Ordinal);
        Assert.DoesNotContain(signingKey, output, StringComparison.Ordinal);
        Assert.Contains("Authorization: [redacted]", output, StringComparison.Ordinal);
        Assert.Contains("[redacted-jwt]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_failure_before_process_start_allows_database_cleanup()
    {
        E2ECleanupDecision decision = StartupOwnership.Decide(
            apiObjectHeld: false,
            apiTerminationEstablished: false,
            ProcessLifetime.NotStarted,
            viteObjectHeld: false,
            viteTerminationEstablished: false,
            ProcessLifetime.NotStarted,
            browserWorkOutstanding: false,
            browserStopFailed: false);

        Assert.True(decision.DropOwnedDatabase);
        Assert.False(decision.FailForProcessLifecycle);
    }

    [Fact]
    public async Task Api_readiness_failure_with_confirmed_stop_allows_database_cleanup()
    {
        RecordingStartup startup = new(confirmTermination: true);
        ProcessStartupException exception = await Assert.ThrowsAsync<ProcessStartupException>(() =>
            StartupCompletion.CompleteAsync(
                () => Task.FromException(new InvalidOperationException("readiness")),
                startup.DisposeAsync,
                () => startup.TerminationEstablished));

        Assert.Equal(ProcessLifetime.ConfirmedStopped, exception.Lifetime);
        Assert.True(startup.Disposed);
        E2ECleanupDecision decision = StartupOwnership.Decide(
            apiObjectHeld: false,
            apiTerminationEstablished: false,
            exception.Lifetime,
            viteObjectHeld: false,
            viteTerminationEstablished: false,
            ProcessLifetime.NotStarted,
            browserWorkOutstanding: false,
            browserStopFailed: false);

        Assert.True(decision.DropOwnedDatabase);
        Assert.False(decision.FailForProcessLifecycle);
    }

    [Fact]
    public async Task Api_readiness_failure_with_unconfirmed_stop_forbids_database_cleanup()
    {
        RecordingStartup startup = new(confirmTermination: false);
        ProcessStartupException exception = await Assert.ThrowsAsync<ProcessStartupException>(() =>
            StartupCompletion.CompleteAsync(
                () => Task.FromException(new InvalidOperationException("readiness")),
                startup.DisposeAsync,
                () => startup.TerminationEstablished));

        Assert.Equal(ProcessLifetime.Unconfirmed, exception.Lifetime);
        E2ECleanupDecision decision = StartupOwnership.Decide(
            apiObjectHeld: false,
            apiTerminationEstablished: false,
            exception.Lifetime,
            viteObjectHeld: false,
            viteTerminationEstablished: false,
            ProcessLifetime.NotStarted,
            browserWorkOutstanding: false,
            browserStopFailed: false);

        Assert.False(decision.DropOwnedDatabase);
        Assert.True(decision.FailForProcessLifecycle);
        Assert.False(StartupOwnership.AllowsCleanup(true, false, ProcessLifetime.NotStarted));
    }

    [Fact]
    public async Task Vite_readiness_failure_with_unconfirmed_stop_fails_the_lifecycle()
    {
        RecordingStartup startup = new(confirmTermination: false);
        ProcessStartupException exception = await Assert.ThrowsAsync<ProcessStartupException>(() =>
            StartupCompletion.CompleteAsync(
                () => Task.FromException(new InvalidOperationException("readiness")),
                startup.DisposeAsync,
                () => startup.TerminationEstablished));

        E2ECleanupDecision decision = StartupOwnership.Decide(
            apiObjectHeld: true,
            apiTerminationEstablished: true,
            ProcessLifetime.NotStarted,
            viteObjectHeld: false,
            viteTerminationEstablished: false,
            exception.Lifetime,
            browserWorkOutstanding: false,
            browserStopFailed: false);

        Assert.Equal(ProcessLifetime.Unconfirmed, exception.Lifetime);
        Assert.True(decision.DropOwnedDatabase);
        Assert.True(decision.FailForProcessLifecycle);
    }

    [Fact]
    public async Task Successful_readiness_leaves_the_owned_process_running()
    {
        RecordingStartup startup = new(confirmTermination: true);
        await StartupCompletion.CompleteAsync(
            () => Task.CompletedTask,
            startup.DisposeAsync,
            () => startup.TerminationEstablished);

        Assert.False(startup.Disposed);
        E2ECleanupDecision decision = StartupOwnership.Decide(
            apiObjectHeld: true,
            apiTerminationEstablished: true,
            ProcessLifetime.NotStarted,
            viteObjectHeld: true,
            viteTerminationEstablished: true,
            ProcessLifetime.NotStarted,
            browserWorkOutstanding: false,
            browserStopFailed: false);
        Assert.True(decision.DropOwnedDatabase);
        Assert.False(decision.FailForProcessLifecycle);
    }

    private sealed class RecordingStartup
    {
        private readonly bool _confirmTermination;

        public RecordingStartup(bool confirmTermination)
        {
            _confirmTermination = confirmTermination;
        }

        public bool Disposed { get; private set; }

        public bool TerminationEstablished { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            TerminationEstablished = _confirmTermination;
            if (!_confirmTermination)
            {
                throw new InvalidOperationException("termination unconfirmed");
            }

            return ValueTask.CompletedTask;
        }
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    private static Process StartIdleProcess()
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
                Arguments = OperatingSystem.IsWindows() ? "/d /c ping 127.0.0.1 -n 60" : "-c \"sleep 60\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        return process;
    }

    private static async Task WriteHttpResponseAsync(TcpListener listener, string status, string body = "")
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[256];
        using MemoryStream received = new();
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
            if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }
        string response = "HTTP/1.1 " + status + "\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body)
            + "\r\nConnection: close\r\n\r\n" + body;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }
}