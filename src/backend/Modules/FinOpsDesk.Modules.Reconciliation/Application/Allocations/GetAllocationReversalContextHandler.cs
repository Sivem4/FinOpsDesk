using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class GetAllocationReversalContextHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationReversalContextQueries _reversalContextQueries;

    public GetAllocationReversalContextHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationReversalContextQueries reversalContextQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reversalContextQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _reversalContextQueries = reversalContextQueries;
    }

    public async Task<Result<AllocationReversalContextResponse>> Handle(
        GetAllocationReversalContextQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        AllocationReversalContextResponse? response = await _reversalContextQueries.FindAsync(
            _currentOrganizationContext.OrganizationId,
            query.AllocationId,
            cancellationToken);

        if (response is null)
        {
            return Result<AllocationReversalContextResponse>.Failure(
                ReconciliationApplicationErrors.AllocationNotFound);
        }

        return Result<AllocationReversalContextResponse>.Success(response);
    }
}