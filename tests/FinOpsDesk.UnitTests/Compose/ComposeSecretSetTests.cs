using FinOpsDesk.ComposePrepare;

namespace FinOpsDesk.UnitTests.Compose;

public sealed class ComposeSecretSetTests
{
    [Fact]
    public async Task Empty_directory_is_created_and_a_second_run_keeps_the_same_bytes()
    {
        string directory = CreateDirectory();
        try
        {
            int created = await ComposeSecretSet.ExecuteAsync(directory, TextWriter.Null, TextWriter.Null);
            Dictionary<string, byte[]> first = ReadFiles(directory);

            int second = await ComposeSecretSet.ExecuteAsync(directory, TextWriter.Null, TextWriter.Null);
            Dictionary<string, byte[]> again = ReadFiles(directory);

            Assert.Equal(ComposeSecretSet.Created, created);
            Assert.Equal(ComposeSecretSet.Unchanged, second);
            Assert.Equal(first.Keys, again.Keys);
            foreach (string name in first.Keys)
            {
                Assert.Equal(first[name], again[name]);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Partial_set_is_refused_without_writing_or_deleting()
    {
        string directory = CreateDirectory();
        try
        {
            byte[] existing = "KeepThisPassword1a"u8.ToArray();
            string passwordPath = Path.Combine(directory, "sql-password.txt");
            await File.WriteAllBytesAsync(passwordPath, existing);
            using StringWriter error = new();

            int code = await ComposeSecretSet.ExecuteAsync(directory, TextWriter.Null, error);

            Assert.Equal(ComposeSecretSet.Refused, code);
            Assert.Equal(existing, await File.ReadAllBytesAsync(passwordPath));
            Assert.False(File.Exists(Path.Combine(directory, "jwt-signing-key.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "api.crt")));
            Assert.False(File.Exists(Path.Combine(directory, "api.key")));
            Assert.Contains("jwt-signing-key.txt", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("KeepThisPassword1a", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_certificate_is_refused_without_replacing_bytes()
    {
        string directory = CreateDirectory();
        try
        {
            Assert.Equal(
                ComposeSecretSet.Created,
                await ComposeSecretSet.ExecuteAsync(directory, TextWriter.Null, TextWriter.Null));
            Dictionary<string, byte[]> before = ReadFiles(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "api.crt"), "not-a-certificate");
            byte[] broken = await File.ReadAllBytesAsync(Path.Combine(directory, "api.crt"));
            using StringWriter error = new();

            int code = await ComposeSecretSet.ExecuteAsync(directory, TextWriter.Null, error);

            Assert.Equal(ComposeSecretSet.Refused, code);
            Assert.Equal(broken, await File.ReadAllBytesAsync(Path.Combine(directory, "api.crt")));
            Assert.Equal(before["sql-password.txt"], await File.ReadAllBytesAsync(Path.Combine(directory, "sql-password.txt")));
            Assert.Equal(before["jwt-signing-key.txt"], await File.ReadAllBytesAsync(Path.Combine(directory, "jwt-signing-key.txt")));
            Assert.Equal(before["api.key"], await File.ReadAllBytesAsync(Path.Combine(directory, "api.key")));
            Assert.Contains("api.crt", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failure_after_the_first_write_deletes_only_files_from_that_attempt()
    {
        string directory = CreateDirectory();
        try
        {
            string keeperPath = Path.Combine(directory, "unrelated.txt");
            byte[] keeper = "keep"u8.ToArray();
            await File.WriteAllBytesAsync(keeperPath, keeper);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ComposeSecretSet.ExecuteAsync(
                    directory,
                    TextWriter.Null,
                    TextWriter.Null,
                    index => index == 0
                        ? throw new InvalidOperationException("stop after the first file")
                        : Task.CompletedTask));

            Assert.Equal(keeper, await File.ReadAllBytesAsync(keeperPath));
            Assert.False(File.Exists(Path.Combine(directory, "sql-password.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "jwt-signing-key.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "api.crt")));
            Assert.False(File.Exists(Path.Combine(directory, "api.key")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "finopsdesk-compose-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Dictionary<string, byte[]> ReadFiles(string directory)
    {
        Dictionary<string, byte[]> files = [];
        foreach (string path in Directory.GetFiles(directory))
        {
            files.Add(Path.GetFileName(path), File.ReadAllBytes(path));
        }

        return files;
    }
}