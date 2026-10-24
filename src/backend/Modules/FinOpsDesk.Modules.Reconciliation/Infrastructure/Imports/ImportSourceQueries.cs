using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;

internal static class ImportSourceQueries
{
    public static IQueryable<Invoice> MatchingInvoices(
        IQueryable<Invoice> invoices,
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        ExternalId[] externalIds)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(externalIds);

        return invoices.Where(invoice =>
            invoice.OrganizationId == organizationId
            && invoice.SourceIdentity != null
            && invoice.SourceIdentity.SourceSystem == sourceSystem
            && externalIds.Contains(invoice.SourceIdentity.ExternalId));
    }

    public static IQueryable<BankTransaction> MatchingBankTransactions(
        IQueryable<BankTransaction> bankTransactions,
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        ExternalId[] externalIds)
    {
        ArgumentNullException.ThrowIfNull(bankTransactions);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(externalIds);

        return bankTransactions.Where(transaction =>
            transaction.OrganizationId == organizationId
            && transaction.SourceIdentity != null
            && transaction.SourceIdentity.SourceSystem == sourceSystem
            && externalIds.Contains(transaction.SourceIdentity.ExternalId));
    }
}