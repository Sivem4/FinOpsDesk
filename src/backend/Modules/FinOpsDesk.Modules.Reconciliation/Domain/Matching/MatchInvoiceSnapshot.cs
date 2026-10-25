using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record MatchInvoiceSnapshot
{
    public OrganizationId OrganizationId { get; }

    public InvoiceId InvoiceId { get; }

    public string? InvoiceNumber { get; }

    public Money OutstandingAmount { get; }

    public MatchInvoiceSnapshot(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        string? invoiceNumber,
        Money outstandingAmount)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(outstandingAmount);

        OrganizationId = organizationId;
        InvoiceId = invoiceId;
        InvoiceNumber = invoiceNumber;
        OutstandingAmount = outstandingAmount;
    }
}