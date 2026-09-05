using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal sealed class TestCurrentOrganizationContext : ICurrentOrganizationContext
{
    public TestCurrentOrganizationContext(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        OrganizationId = organizationId;
    }

    public OrganizationId OrganizationId { get; }
}