using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class InvoiceQueries : IInvoiceQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public InvoiceQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<InvoiceListResponse> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        IQueryable<Invoice> invoices = _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.OrganizationId == organizationId)
            .OrderBy(invoice => invoice.Id);

        int totalCount = await invoices.CountAsync(cancellationToken);

        long offset = ((long)page - 1L) * pageSize;
        List<InvoiceListItemDto> items = await invoices
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(invoice => new InvoiceListItemDto(
                invoice.Id.Value,
                invoice.InvoiceNumber,
                new MoneyDto(
                    invoice.TotalAmount.Amount,
                    invoice.TotalAmount.Currency.Value),
                new MoneyDto(
                    invoice.AllocatedAmount.Amount,
                    invoice.AllocatedAmount.Currency.Value),
                new MoneyDto(
                    invoice.TotalAmount.Amount - invoice.AllocatedAmount.Amount,
                    invoice.TotalAmount.Currency.Value)))
            .ToListAsync(cancellationToken);

        return new InvoiceListResponse(items, page, pageSize, totalCount);
    }
}