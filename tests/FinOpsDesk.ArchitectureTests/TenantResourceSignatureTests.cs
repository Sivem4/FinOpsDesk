using System.Reflection;

using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.ArchitectureTests;

public sealed class TenantResourceSignatureTests
{
    [Fact]
    public void Tenant_point_and_list_reads_take_organization_id()
    {
        // This test checks public query signatures only.
        // An OrganizationId parameter does not prove that the generated SQL contains an OrganizationId predicate.
        const string applicationNamespace = "FinOpsDesk.Modules.Reconciliation.Application";
        MethodInfo[] methods = typeof(IInvoiceRepository).Assembly
            .GetTypes()
            .Where(type =>
                type.IsInterface
                && type.IsPublic
                && IsApplicationNamespace(type.Namespace, applicationNamespace))
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name is "GetByIdAsync" or "ListAsync")
            .Distinct()
            .ToArray();

        Assert.NotEmpty(methods);

        foreach (MethodInfo method in methods)
        {
            Assert.True(
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(OrganizationId)),
                method.DeclaringType?.FullName + "." + method.Name);
        }
    }

    private static bool IsApplicationNamespace(string? typeNamespace, string applicationNamespace)
    {
        return typeNamespace == applicationNamespace
            || (typeNamespace?.StartsWith(applicationNamespace + ".", StringComparison.Ordinal) ?? false);
    }
}