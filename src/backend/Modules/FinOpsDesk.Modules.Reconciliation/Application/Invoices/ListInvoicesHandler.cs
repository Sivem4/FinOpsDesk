using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public sealed class ListInvoicesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IInvoiceQueries _invoiceQueries;

    public ListInvoicesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IInvoiceQueries invoiceQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(invoiceQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _invoiceQueries = invoiceQueries;
    }

    public async Task<Result<InvoiceListResponse>> Handle(
        ListInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!InvoiceListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<InvoiceListResponse>.Failure(
                ReconciliationApplicationErrors.InvalidInvoicePagination);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;

        InvoiceListResponse response = await _invoiceQueries.ListAsync(
            organizationId,
            query.Page,
            query.PageSize,
            cancellationToken);

        return Result<InvoiceListResponse>.Success(response);
    }
}