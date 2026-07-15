using FinOpsDesk.Modules.Reconciliation;

namespace FinOpsDesk.ArchitectureTests;

public sealed class ModuleDependencyTests
{
    [Fact]
    public void Reconciliation_module_must_not_reference_api_host()
    {
        string[] referencedAssemblyNames = typeof(ReconciliationModule)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            "FinOpsDesk.Api",
            referencedAssemblyNames);
    }
}