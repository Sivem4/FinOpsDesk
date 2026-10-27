using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class CompetitorProbeQueries : ICompetitorProbeQueries
{
    private const int FetchCount = CompetitorProbeRules.MaxCompleteRowCount + 1;

    private readonly ReconciliationDbContext _dbContext;

    public CompetitorProbeQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<CompetitorProbeQueryResult> ProbeInvoicesAsync(
        OrganizationId organizationId,
        Guid excludedInvoiceId,
        string currency,
        decimal remainingAmount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (await HasInconsistentInvoiceMoneyAsync(organizationId, cancellationToken)
            || await HasInconsistentBankTransactionMoneyAsync(organizationId, cancellationToken))
        {
            return new CompetitorProbeQueryResult(true, [], false);
        }

        List<ProbeReferenceRow> rows = await _dbContext.Database.SqlQuery<ProbeReferenceRow>(
            $"""
            SELECT TOP ({FetchCount})
                [InvoiceNumber] AS [Reference]
            FROM [reconciliation].[Invoices]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [Id] <> {excludedInvoiceId}
                AND [TotalCurrency] = {currency}
                AND [AllocatedCurrency] = {currency}
                AND [TotalAmount] > 0
                AND [AllocatedAmount] >= 0
                AND [TotalAmount] > [AllocatedAmount]
                AND ([TotalAmount] - [AllocatedAmount]) = {remainingAmount}
            ORDER BY [Id]
            """)
            .ToListAsync(cancellationToken);

        return ToResult(rows);
    }

    public async Task<CompetitorProbeQueryResult> ProbeBankTransactionsAsync(
        OrganizationId organizationId,
        Guid excludedBankTransactionId,
        string currency,
        decimal remainingAmount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (await HasInconsistentInvoiceMoneyAsync(organizationId, cancellationToken)
            || await HasInconsistentBankTransactionMoneyAsync(organizationId, cancellationToken))
        {
            return new CompetitorProbeQueryResult(true, [], false);
        }

        List<ProbeReferenceRow> rows = await _dbContext.Database.SqlQuery<ProbeReferenceRow>(
            $"""
            SELECT TOP ({FetchCount})
                [Reference] AS [Reference]
            FROM [reconciliation].[BankTransactions]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [Id] <> {excludedBankTransactionId}
                AND [Currency] = {currency}
                AND [AllocatedCurrency] = {currency}
                AND [Amount] > 0
                AND [AllocatedAmount] >= 0
                AND [Amount] > [AllocatedAmount]
                AND ([Amount] - [AllocatedAmount]) = {remainingAmount}
            ORDER BY [Id]
            """)
            .ToListAsync(cancellationToken);

        return ToResult(rows);
    }

    private Task<bool> HasInconsistentInvoiceMoneyAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        return _dbContext.Invoices
            .AsNoTracking()
            .AnyAsync(
                invoice => invoice.OrganizationId == organizationId
                    && (invoice.TotalAmount.Amount <= 0m
                        || invoice.AllocatedAmount.Amount < 0m
                        || invoice.AllocatedAmount.Amount > invoice.TotalAmount.Amount
                        || invoice.TotalAmount.Currency != invoice.AllocatedAmount.Currency),
                cancellationToken);
    }

    private Task<bool> HasInconsistentBankTransactionMoneyAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        return _dbContext.BankTransactions
            .AsNoTracking()
            .AnyAsync(
                bankTransaction => bankTransaction.OrganizationId == organizationId
                    && (bankTransaction.Amount.Amount <= 0m
                        || bankTransaction.AllocatedAmount.Amount < 0m
                        || bankTransaction.AllocatedAmount.Amount > bankTransaction.Amount.Amount
                        || bankTransaction.Amount.Currency
                            != bankTransaction.AllocatedAmount.Currency),
                cancellationToken);
    }

    private static CompetitorProbeQueryResult ToResult(List<ProbeReferenceRow> rows)
    {
        CompetitorProbeRow[] mapped = new CompetitorProbeRow[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            mapped[index] = new CompetitorProbeRow(rows[index].Reference);
        }

        return new CompetitorProbeQueryResult(
            false,
            mapped,
            rows.Count > CompetitorProbeRules.MaxCompleteRowCount);
    }

    private sealed record ProbeReferenceRow(string? Reference);
}