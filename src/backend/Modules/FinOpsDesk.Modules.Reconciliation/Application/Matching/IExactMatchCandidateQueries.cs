using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Matching;

public interface IExactMatchCandidateQueries
{
    Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
        OrganizationId organizationId,
        int limit,
        CancellationToken cancellationToken);
}