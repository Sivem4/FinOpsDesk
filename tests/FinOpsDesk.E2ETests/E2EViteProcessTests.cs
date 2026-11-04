using System.Diagnostics;

namespace FinOpsDesk.E2ETests;

public sealed class E2EViteProcessTests
{
    [Fact]
    public void Vite_starts_through_node_on_the_platform_path()
    {
        string? previous = Environment.GetEnvironmentVariable("NODE_TLS_REJECT_UNAUTHORIZED");
        Environment.SetEnvironmentVariable("NODE_TLS_REJECT_UNAUTHORIZED", "0");
        try
        {
            string viteEntry = Path.Combine(RepoPaths.FrontendDirectory, "node_modules", "vite", "bin", "vite.js");
            ProcessStartInfo start = E2EViteProcess.CreateStartInfo(
                "https://localhost:9",
                new E2EViteLaunch
                {
                    Host = "localhost",
                    Port = 9,
                    CookieMode = true,
                    NodeExtraCaCerts = Path.Combine(Path.GetTempPath(), "localhost-public.crt"),
                },
                9,
                viteEntry);

            Assert.Equal("node", start.FileName);
            Assert.Equal(viteEntry, start.ArgumentList[0]);
            Assert.Contains(Path.DirectorySeparatorChar, viteEntry.ToCharArray());
            Assert.DoesNotContain("npm", start.ArgumentList);
            Assert.Contains("--mode", start.ArgumentList);
            Assert.Contains("cookie", start.ArgumentList);
            Assert.Contains("--strictPort", start.ArgumentList);
            Assert.Equal("https://localhost:9", start.Environment["FINOPSDESK_E2E_API_ORIGIN"]);
            Assert.Equal("cookie", start.Environment["VITE_AUTH_MODE"]);
            Assert.False(string.IsNullOrWhiteSpace(start.Environment["NODE_EXTRA_CA_CERTS"]));
            Assert.False(start.Environment.ContainsKey("NODE_TLS_REJECT_UNAUTHORIZED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODE_TLS_REJECT_UNAUTHORIZED", previous);
        }
    }

    [Fact]
    public async Task Owned_node_process_stops_without_stopping_another_node()
    {
        Process? owned = null;
        Process? other = null;
        try
        {
            owned = StartIdleNode();
            other = StartIdleNode();
            int ownedId = owned.Id;
            int otherId = other.Id;
            ProcessStopResult stopped = await ProcessTermination.ConfirmStoppedAsync(owned, TimeSpan.FromSeconds(15));

            Assert.True(stopped.Stopped);
            Assert.False(ProcessTermination.IsAlive(ownedId));
            Assert.True(ProcessTermination.IsAlive(otherId));
        }
        finally
        {
            if (other is not null)
            {
                await ProcessTermination.ConfirmStoppedAsync(other, TimeSpan.FromSeconds(15));
                other.Dispose();
            }

            owned?.Dispose();
        }
    }

    [Fact]
    public void Process_start_failure_releases_the_process_object()
    {
        ProcessStartInfo start = new()
        {
            FileName = Path.Combine(Path.GetTempPath(), "finopsdesk-missing-node-" + Guid.NewGuid().ToString("N")),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => E2EViteProcess.StartOwnedProcess(start));
    }

    [Fact]
    public async Task Exited_process_is_not_a_ready_vite_server()
    {
        Process process = E2EViteProcess.StartOwnedProcess(new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { "-e", "process.exit(7)" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        try
        {
            Assert.True(process.WaitForExit(5_000));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                E2EViteProcess.WaitUntilReadyAsync(process, new E2EProcessLog(), "http://127.0.0.1:9", CancellationToken.None));
            Assert.Contains("exited early", exception.Message, StringComparison.Ordinal);
            Assert.Contains("7", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public void Missing_chromium_is_a_failed_test()
    {
        Exception failure = E2EChromium.Unavailable(new InvalidOperationException("Executable doesn't exist"));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("Chromium is not available", failure.Message, StringComparison.Ordinal);
    }

    private static Process StartIdleNode()
    {
        return E2EViteProcess.StartOwnedProcess(new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { "-e", "setInterval(() => {}, 1000)" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
    }
}