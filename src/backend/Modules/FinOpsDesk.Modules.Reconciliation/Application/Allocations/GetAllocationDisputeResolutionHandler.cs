using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class GetAllocationDisputeResolutionHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationDisputeResolutionQueries _queries;

    public GetAllocationDisputeResolutionHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationDisputeResolutionQueries queries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(queries);

        _currentOrganizationContext = currentOrganizationContext;
        _queries = queries;
    }

    public async Task<Result<AllocationDisputeResolutionStateResponse>> Handle(
        GetAllocationDisputeResolutionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        AllocationDisputeResolutionStateResponse? resolution = await _queries.GetAsync(
            _currentOrganizationContext.OrganizationId,
            query.DisputeId,
            cancellationToken);
        if (resolution is null)
        {
            return Result<AllocationDisputeResolutionStateResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        return Result<AllocationDisputeResolutionStateResponse>.Success(resolution);
    }
}