using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public interface IInvoiceRepository
{
    Task<Invoice?> GetByIdAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken);
}