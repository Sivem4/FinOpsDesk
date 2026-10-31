using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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