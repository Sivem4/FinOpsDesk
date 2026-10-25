using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class ExactMatchCandidateQueries : IExactMatchCandidateQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public ExactMatchCandidateQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
        OrganizationId organizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (limit < 1 || limit > ExactMatchCandidateLimits.MaxPerSide)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Candidate limit must be between 1 and the open-book maximum.");
        }

        // A corrupt row is a request failure. Skipping it could hide a competing candidate.
        if (await HasInconsistentInvoiceMoneyAsync(organizationId, cancellationToken)
            || await HasInconsistentBankTransactionMoneyAsync(organizationId, cancellationToken))
        {
            return new ExactMatchCandidateSet(
                [],
                [],
                invoiceLimitExceeded: false,
                bankTransactionLimitExceeded: false,
                inconsistentMoney: true);
        }

        int fetchCount = checked(limit + 1);
        List<InvoiceCandidateRow> invoices = await LoadOpenInvoicesAsync(
            organizationId,
            fetchCount,
            cancellationToken);
        List<BankTransactionCandidateRow> bankTransactions = await LoadOpenBankTransactionsAsync(
            organizationId,
            fetchCount,
            cancellationToken);

        bool invoiceLimitExceeded = invoices.Count > limit;
        bool bankTransactionLimitExceeded = bankTransactions.Count > limit;

        if (invoiceLimitExceeded || bankTransactionLimitExceeded)
        {
            return new ExactMatchCandidateSet(
                [],
                [],
                invoiceLimitExceeded,
                bankTransactionLimitExceeded,
                inconsistentMoney: false);
        }

        if (!TryMapInvoices(invoices, out MatchInvoiceSnapshot[] mappedInvoices)
            || !TryMapBankTransactions(
                bankTransactions,
                out MatchBankTransactionSnapshot[] mappedBankTransactions))
        {
            return new ExactMatchCandidateSet(
                [],
                [],
                invoiceLimitExceeded: false,
                bankTransactionLimitExceeded: false,
                inconsistentMoney: true);
        }

        return new ExactMatchCandidateSet(
            mappedInvoices,
            mappedBankTransactions,
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: false);
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

    private Task<List<InvoiceCandidateRow>> LoadOpenInvoicesAsync(
        OrganizationId organizationId,
        int fetchCount,
        CancellationToken cancellationToken)
    {
        // The currency value converter constructs CurrencyCode while materializing.
        // Selecting the char(3) column keeps a non-letter code as a string so the
        // caller can reject it without a second query.
        return _dbContext.Database.SqlQuery<InvoiceCandidateRow>(
            $"""
            SELECT TOP ({fetchCount})
                [OrganizationId] AS [OrganizationId],
                [Id] AS [InvoiceId],
                [InvoiceNumber] AS [InvoiceNumber],
                [TotalAmount] - [AllocatedAmount] AS [OutstandingAmount],
                [TotalCurrency] AS [Currency]
            FROM [reconciliation].[Invoices]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [TotalAmount] > 0
                AND [AllocatedAmount] >= 0
                AND [TotalAmount] > [AllocatedAmount]
                AND [TotalCurrency] = [AllocatedCurrency]
            ORDER BY [Id]
            """)
            .ToListAsync(cancellationToken);
    }

    private Task<List<BankTransactionCandidateRow>> LoadOpenBankTransactionsAsync(
        OrganizationId organizationId,
        int fetchCount,
        CancellationToken cancellationToken)
    {
        return _dbContext.Database.SqlQuery<BankTransactionCandidateRow>(
            $"""
            SELECT TOP ({fetchCount})
                [OrganizationId] AS [OrganizationId],
                [Id] AS [BankTransactionId],
                [Reference] AS [Reference],
                [Amount] - [AllocatedAmount] AS [UnappliedAmount],
                [Currency] AS [Currency]
            FROM [reconciliation].[BankTransactions]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [Amount] > 0
                AND [AllocatedAmount] >= 0
                AND [Amount] > [AllocatedAmount]
                AND [Currency] = [AllocatedCurrency]
            ORDER BY [Id]
            """)
            .ToListAsync(cancellationToken);
    }

    private static bool TryMapInvoices(
        List<InvoiceCandidateRow> rows,
        out MatchInvoiceSnapshot[] snapshots)
    {
        snapshots = new MatchInvoiceSnapshot[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            InvoiceCandidateRow row = rows[index];
            if (!IsThreeLetterCurrency(row.Currency))
            {
                return false;
            }

            snapshots[index] = new MatchInvoiceSnapshot(
                new OrganizationId(row.OrganizationId),
                new InvoiceId(row.InvoiceId),
                row.InvoiceNumber,
                new Money(row.OutstandingAmount, new CurrencyCode(row.Currency)));
        }

        return true;
    }

    private static bool TryMapBankTransactions(
        List<BankTransactionCandidateRow> rows,
        out MatchBankTransactionSnapshot[] snapshots)
    {
        snapshots = new MatchBankTransactionSnapshot[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            BankTransactionCandidateRow row = rows[index];
            if (!IsThreeLetterCurrency(row.Currency))
            {
                return false;
            }

            snapshots[index] = new MatchBankTransactionSnapshot(
                new OrganizationId(row.OrganizationId),
                new BankTransactionId(row.BankTransactionId),
                row.Reference,
                new Money(row.UnappliedAmount, new CurrencyCode(row.Currency)));
        }

        return true;
    }

    private static bool IsThreeLetterCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return false;
        }

        string normalized = currency.Trim();
        if (normalized.Length != 3)
        {
            return false;
        }

        foreach (char character in normalized)
        {
            if (character is (< 'A' or > 'Z') and (< 'a' or > 'z'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record InvoiceCandidateRow(
        Guid OrganizationId,
        Guid InvoiceId,
        string InvoiceNumber,
        decimal OutstandingAmount,
        string Currency);

    private sealed record BankTransactionCandidateRow(
        Guid OrganizationId,
        Guid BankTransactionId,
        string? Reference,
        decimal UnappliedAmount,
        string Currency);
}