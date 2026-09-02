using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public interface ICurrentOrganizationContext
{
    OrganizationId OrganizationId { get; }
}