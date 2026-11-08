using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationDisputeCorrectionQueries
{
    Task<AllocationDisputeCorrectionStateResponse?> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken);
}

public sealed class GetAllocationDisputeCorrectionHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationDisputeCorrectionQueries _queries;

    public GetAllocationDisputeCorrectionHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationDisputeCorrectionQueries queries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(queries);

        _currentOrganizationContext = currentOrganizationContext;
        _queries = queries;
    }

    public async Task<Result<AllocationDisputeCorrectionStateResponse>> Handle(
        GetAllocationDisputeCorrectionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        AllocationDisputeCorrectionStateResponse? correction = await _queries.GetAsync(
            _currentOrganizationContext.OrganizationId,
            query.DisputeId,
            cancellationToken);
        if (correction is null)
        {
            return Result<AllocationDisputeCorrectionStateResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        return Result<AllocationDisputeCorrectionStateResponse>.Success(correction);
    }
}