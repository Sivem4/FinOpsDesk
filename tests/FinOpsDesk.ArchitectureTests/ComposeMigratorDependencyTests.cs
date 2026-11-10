using FinOpsDesk.ComposeMigrate;

using Microsoft.Extensions.Configuration;

namespace FinOpsDesk.ArchitectureTests;

public sealed class ComposeMigratorDependencyTests
{
    [Fact]
    public void Compose_migrator_must_not_reference_the_api_host()
    {
        string[] referencedAssemblyNames = typeof(ComposeMigrator)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("FinOpsDesk.Api", referencedAssemblyNames);
    }

    [Fact]
    public async Task Missing_connection_string_fails_without_a_secret_value()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        await using StringWriter error = new();

        int code = await ComposeMigrator.RunAsync(configuration, error, CancellationToken.None);

        Assert.Equal(1, code);
        string text = error.ToString();
        Assert.Contains("Connection string 'FinOpsDesk' is not configured.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", text, StringComparison.Ordinal);
    }
}