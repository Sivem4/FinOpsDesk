using System.Diagnostics;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

using Microsoft.Data.SqlClient;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AccessBootstrapProcessTests
{
    private const string GuardDatabaseName = "FinOpsDesk_BootstrapCliGuard";

    private readonly SqlServerFixture _fixture;

    public AccessBootstrapProcessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Invalid_role_commands_do_not_open_sql_and_valid_commands_parse()
    {
        await AssertNoGuardDatabaseAsync();
        try
        {
            await AssertProcessAsync(InvalidArgument("grant-role"), "invalid-argument");
            await AssertProcessAsync(EmptyOperator(), "invalid-operator");
            await AssertProcessAsync(DuplicateOperator(), "invalid-argument");
            await AssertProcessAsync(UnknownOption(), "invalid-argument");
            await AssertProcessAsync(Valid("grant-role", "Operator"), "unavailable");
            await AssertProcessAsync(Valid("revoke-role", "OrganizationAdmin"), "unavailable");
            await AssertProcessAsync(["disable-user", "--issuer", "https://issuer.example", "--subject", "subject-1"], "invalid-argument");
            await AssertProcessAsync(["disable-user", "--issuer", "not a uri", "--subject", "subject-1", "--operator", "phase-7d5-operator"], "unavailable");
            await AssertProcessAsync(Emergency("disable-user"), "unavailable");
            await AssertProcessAsync(Emergency("revoke-membership"), "unavailable");
            await AssertProcessAsync(Emergency("disable-organization"), "unavailable");
            await AssertProcessAsync(["disable-organization", "--organization-id", "not-a-guid", "--operator", "phase-7d5-operator"], "invalid-argument");
            await AssertNoGuardDatabaseAsync();
        }
        finally
        {
            await DropGuardDatabaseAsync();
        }
    }

    private async Task AssertProcessAsync(string[] args, string expected)
    {
        ProcessResult result = await RunAsync(args);
        if (result.StandardError.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("Server=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Bootstrap process wrote connection details.");
        }

        Assert.Equal(expected, result.Token);
        Assert.Equal(1, result.ExitCode);
    }

    private async Task<ProcessResult> RunAsync(string[] args)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        string configuration = directory.Parent?.Name ?? string.Empty;
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string dll = Path.Combine(
            repo,
            "src",
            "backend",
            "FinOpsDesk.AccessBootstrap",
            "bin",
            configuration,
            "net10.0",
            "FinOpsDesk.AccessBootstrap.dll");
        Assert.True(File.Exists(dll), "Bootstrap executable was not built.");
        SqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
        {
            InitialCatalog = GuardDatabaseName,
        };
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["ConnectionStrings__FinOpsDesk"] = builder.ConnectionString;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Bootstrap process did not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string token = output.Trim();
        const string prefix = "result=";
        if (token.StartsWith(prefix, StringComparison.Ordinal))
        {
            token = token[prefix.Length..];
        }

        return new ProcessResult(process.ExitCode, token, error);
    }

    private async Task AssertNoGuardDatabaseAsync()
    {
        Assert.Equal(0, await CountGuardDatabaseAsync());
    }

    private async Task<int> CountGuardDatabaseAsync()
    {
        SqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using SqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
            connection);
        command.Parameters.Add(new SqlParameter("@name", GuardDatabaseName));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task DropGuardDatabaseAsync()
    {
        if (await CountGuardDatabaseAsync() == 0)
        {
            return;
        }

        SqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using SqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "ALTER DATABASE [FinOpsDesk_BootstrapCliGuard] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [FinOpsDesk_BootstrapCliGuard];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string[] InvalidArgument(string command)
    {
        return
        [
            command,
            "--issuer",
            "https://issuer.example",
            "--subject",
            "subject-1",
            "--organization-id",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "--role",
            "Viewer",
        ];
    }

    private static string[] EmptyOperator()
    {
        return [.. InvalidArgument("grant-role"), "--operator", " "];
    }

    private static string[] DuplicateOperator()
    {
        return [.. InvalidArgument("revoke-role"), "--operator", "one", "--operator", "two"];
    }

    private static string[] UnknownOption()
    {
        return [.. InvalidArgument("grant-role"), "--operator", "phase-7d2-operator", "--source", "environment"];
    }

    private static string[] Emergency(string command)
    {
        return command switch
        {
            "disable-user" => [command, "--issuer", "https://issuer.example", "--subject", "subject-1", "--operator", "phase-7d5-operator"],
            "disable-organization" => [command, "--organization-id", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--operator", "phase-7d5-operator"],
            _ => [command, "--issuer", "https://issuer.example", "--subject", "subject-1", "--organization-id", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--operator", "phase-7d5-operator"],
        };
    }

    private static string[] Valid(string command, string role)
    {
        return
        [
            command,
            "--issuer",
            "https://issuer.example",
            "--subject",
            "subject-1",
            "--organization-id",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "--role",
            role,
            "--operator",
            "phase-7d2-operator",
        ];
    }

    private sealed record ProcessResult(int ExitCode, string Token, string StandardError);
}