using System.Diagnostics;

using FinOpsDesk.ComposePrepare;

namespace FinOpsDesk.UnitTests.Compose;

public sealed class ComposeSecretLocationTests
{
    [Fact]
    public void Path_inside_the_repository_is_refused_without_creating_it()
    {
        string repository = CreateDirectory();
        try
        {
            string destination = Path.Combine(repository, ".finopsdesk", "compose");
            using StringWriter error = new();

            int code = ComposeSecretLocation.Check(destination, repository, error);

            Assert.Equal(ComposeSecretLocation.Refused, code);
            Assert.False(Directory.Exists(destination));
            Assert.Contains("repository", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public void Redirect_into_a_synthetic_repository_is_refused_before_any_write()
    {
        string root = CreateDirectory();
        string repository = Path.Combine(root, "repository");
        string target = Path.Combine(repository, "secrets");
        string profile = Path.Combine(root, "profile");
        string link = Path.Combine(profile, ".finopsdesk");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(profile);
            string markerPath = Path.Combine(target, "marker.txt");
            File.WriteAllText(markerPath, "keep-marker");
            CreateRedirect(link, target);
            string destination = Path.Combine(link, "compose");
            using StringWriter error = new();

            int code = ComposeSecretLocation.Check(destination, repository, error);

            Assert.Equal(ComposeSecretLocation.Refused, code);
            Assert.False(Directory.Exists(destination));
            Assert.False(Directory.Exists(Path.Combine(target, "compose")));
            Assert.Equal("keep-marker", File.ReadAllText(markerPath));
            Assert.DoesNotContain("keep-marker", error.ToString(), StringComparison.Ordinal);
            foreach (string name in new[] { "sql-password.txt", "jwt-signing-key.txt", "api.crt", "api.key" })
            {
                Assert.False(File.Exists(Path.Combine(target, name)));
                Assert.False(File.Exists(Path.Combine(destination, name)));
            }
        }
        finally
        {
            try
            {
                RemoveRedirect(link);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Existing_secret_file_redirected_into_the_repository_is_refused()
    {
        string root = CreateDirectory();
        string repository = Path.Combine(root, "repository");
        string outside = Path.Combine(root, "outside");
        string landed = Path.Combine(repository, "landed");
        string secretLink = Path.Combine(outside, "sql-password.txt");
        try
        {
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(landed);
            File.WriteAllText(Path.Combine(landed, "marker.txt"), "secret-marker");
            CreateRedirect(secretLink, landed);
            using StringWriter error = new();

            int code = ComposeSecretLocation.Check(outside, repository, error);

            Assert.Equal(ComposeSecretLocation.Refused, code);
            Assert.Equal("secret-marker", File.ReadAllText(Path.Combine(landed, "marker.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "jwt-signing-key.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "api.crt")));
            Assert.False(File.Exists(Path.Combine(outside, "api.key")));
            Assert.DoesNotContain("secret-marker", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                RemoveRedirect(secretLink);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Directory_outside_the_repository_is_accepted_without_creating_it()
    {
        string root = CreateDirectory();
        string repository = Path.Combine(root, "repository");
        string destination = Path.Combine(root, "outside");
        Directory.CreateDirectory(repository);
        try
        {
            int code = ComposeSecretLocation.Check(destination, repository, TextWriter.Null);

            Assert.Equal(ComposeSecretLocation.Outside, code);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "finopsdesk-compose-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CreateRedirect(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("Windows junction process was not started.");
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return;
        }

        Directory.CreateSymbolicLink(link, target);
    }

    private static void RemoveRedirect(string link)
    {
        var info = new DirectoryInfo(link);
        if (info.LinkTarget is null && !info.Exists)
        {
            return;
        }

        Directory.Delete(link, recursive: false);
    }
}