using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;

public sealed class GetAllocationAuditHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IFinancialAuditQueries _financialAuditQueries;

    public GetAllocationAuditHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IFinancialAuditQueries financialAuditQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(financialAuditQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _financialAuditQueries = financialAuditQueries;
    }

    public async Task<Result<FinancialAuditListResponse>> Handle(
        GetAllocationAuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!FinancialAuditListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<FinancialAuditListResponse>.Failure(
                ReconciliationApplicationErrors.AllocationAuditInvalidPagination);
        }

        return await _financialAuditQueries.GetForAllocationAsync(
            _currentOrganizationContext.OrganizationId,
            query.AllocationId,
            query.Page,
            query.PageSize,
            cancellationToken);
    }
}