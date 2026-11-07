using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class GetAllocationDisputesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationDisputeQueries _queries;

    public GetAllocationDisputesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationDisputeQueries queries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(queries);

        _currentOrganizationContext = currentOrganizationContext;
        _queries = queries;
    }

    public Task<Result<AllocationDisputeListResponse>> Handle(
        GetAllocationDisputesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.AllocationId == Guid.Empty)
        {
            return Task.FromResult(Result<AllocationDisputeListResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeInvalidRequest));
        }

        if (!AllocationDisputeListPagination.IsValid(query.Page, query.PageSize))
        {
            return Task.FromResult(Result<AllocationDisputeListResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeInvalidPagination));
        }

        return _queries.ListAsync(
            _currentOrganizationContext.OrganizationId,
            query.Page,
            query.PageSize,
            query.AllocationId,
            cancellationToken);
    }
}

public sealed class GetAllocationDisputeHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationDisputeQueries _queries;

    public GetAllocationDisputeHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationDisputeQueries queries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(queries);

        _currentOrganizationContext = currentOrganizationContext;
        _queries = queries;
    }

    public Task<Result<AllocationDisputeResponse>> Handle(
        GetAllocationDisputeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.DisputeId == Guid.Empty)
        {
            return Task.FromResult(Result<AllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeInvalidRequest));
        }

        return _queries.GetAsync(
            _currentOrganizationContext.OrganizationId,
            query.DisputeId,
            cancellationToken);
    }
}