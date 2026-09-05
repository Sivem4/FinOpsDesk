using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly ReconciliationDbContext _dbContext;

    public InvoiceRepository(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public Task<Invoice?> GetByIdAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        return _dbContext.Invoices.SingleOrDefaultAsync(
            invoice => invoice.OrganizationId == organizationId
                && invoice.Id == invoiceId,
            cancellationToken);
    }
}