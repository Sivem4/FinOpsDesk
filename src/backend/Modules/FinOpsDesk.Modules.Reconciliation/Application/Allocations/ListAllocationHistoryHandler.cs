using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ListAllocationHistoryHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationHistoryQueries _allocationHistoryQueries;

    public ListAllocationHistoryHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationHistoryQueries allocationHistoryQueries)
    {
        _currentOrganizationContext = currentOrganizationContext;
        _allocationHistoryQueries = allocationHistoryQueries;
    }

    public async Task<Result<AllocationHistoryListResponse>> Handle(
        ListAllocationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AllocationHistoryListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<AllocationHistoryListResponse>.Failure(
                ReconciliationApplicationErrors.AllocationHistoryInvalidPagination);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        AllocationHistoryListResponse response = await _allocationHistoryQueries.ListAsync(
            organizationId,
            query.Page,
            query.PageSize,
            query.InvoiceId,
            query.BankTransactionId,
            cancellationToken);
        return Result<AllocationHistoryListResponse>.Success(response);
    }
}