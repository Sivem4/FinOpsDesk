using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationContextQueries : IAllocationContextQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationContextQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<AllocationContextInvoiceColumns?> ReadInvoiceAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);

        // Currency and source identity are read as stored strings. Materializing
        // them through their value converters would throw before the handler can
        // return a controlled inconsistent-money response.
        List<AllocationContextInvoiceColumns> rows = await _dbContext.Database
            .SqlQuery<AllocationContextInvoiceColumns>(
                $"""
                SELECT
                    [Id] AS [InvoiceId],
                    [InvoiceNumber] AS [InvoiceNumber],
                    [TotalAmount] AS [TotalAmount],
                    [TotalCurrency] AS [TotalCurrency],
                    [AllocatedAmount] AS [AllocatedAmount],
                    [AllocatedCurrency] AS [AllocatedCurrency],
                    [SourceSystem] AS [SourceSystem],
                    [ExternalId] AS [ExternalId]
                FROM [reconciliation].[Invoices]
                WHERE [OrganizationId] = {organizationId.Value}
                    AND [Id] = {invoiceId.Value}
                """)
            .ToListAsync(cancellationToken);

        return SingleOrAbsent(rows);
    }

    public async Task<AllocationContextBankTransactionColumns?> ReadBankTransactionAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        List<AllocationContextBankTransactionColumns> rows = await _dbContext.Database
            .SqlQuery<AllocationContextBankTransactionColumns>(
                $"""
                SELECT
                    [Id] AS [BankTransactionId],
                    [Reference] AS [Reference],
                    [BookingDate] AS [BookingDate],
                    [Amount] AS [Amount],
                    [Currency] AS [Currency],
                    [AllocatedAmount] AS [AllocatedAmount],
                    [AllocatedCurrency] AS [AllocatedCurrency],
                    [SourceSystem] AS [SourceSystem],
                    [ExternalId] AS [ExternalId]
                FROM [reconciliation].[BankTransactions]
                WHERE [OrganizationId] = {organizationId.Value}
                    AND [Id] = {bankTransactionId.Value}
                """)
            .ToListAsync(cancellationToken);

        return SingleOrAbsent(rows);
    }

    private static T? SingleOrAbsent<T>(List<T> rows)
        where T : class
    {
        if (rows.Count == 0)
        {
            return null;
        }

        if (rows.Count == 1)
        {
            return rows[0];
        }

        throw new InvalidOperationException("An allocation context lookup returned more than one row.");
    }
}