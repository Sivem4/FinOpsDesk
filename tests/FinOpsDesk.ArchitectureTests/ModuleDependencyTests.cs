using FinOpsDesk.Modules.Access;
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
        Assert.DoesNotContain(
            "FinOpsDesk.Modules.Access",
            referencedAssemblyNames);
    }

    [Fact]
    public void Access_module_must_not_reference_api_host()
    {
        string[] referencedAssemblyNames = typeof(AccessModule)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            "FinOpsDesk.Api",
            referencedAssemblyNames);
        Assert.DoesNotContain(
            "FinOpsDesk.Modules.Reconciliation",
            referencedAssemblyNames);
    }
}