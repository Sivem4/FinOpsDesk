using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class ReviewCaseQueries : IReviewCaseQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public ReviewCaseQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<ReviewCaseListResponse> ListAsync(
        OrganizationId organizationId,
        ReviewCaseStatus? status,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        IQueryable<ReviewCase> reviewCases = _dbContext.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId);
        if (status is not null)
        {
            reviewCases = reviewCases.Where(reviewCase => reviewCase.Status == status);
        }

        if (invoiceId is not null)
        {
            reviewCases = reviewCases.Where(reviewCase => reviewCase.InvoiceId == invoiceId);
        }

        if (bankTransactionId is not null)
        {
            reviewCases = reviewCases.Where(reviewCase => reviewCase.BankTransactionId == bankTransactionId);
        }

        int totalCount = await reviewCases.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<ReviewCaseRow> rows = await reviewCases
            .OrderByDescending(reviewCase => reviewCase.OpenedAt)
            .ThenByDescending(reviewCase => reviewCase.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(reviewCase => new ReviewCaseRow(
                reviewCase.Id,
                reviewCase.Reason,
                reviewCase.Status,
                reviewCase.InvoiceId,
                reviewCase.BankTransactionId,
                reviewCase.SourceLabel,
                reviewCase.Note,
                reviewCase.Outcome,
                reviewCase.ResolutionNote,
                reviewCase.AllocationId,
                reviewCase.OpenedAt,
                reviewCase.ClosedAt,
                EF.Property<byte[]>(reviewCase, "RowVersion")))
            .ToListAsync(cancellationToken);

        return new ReviewCaseListResponse(
            rows.Select(ToResponse).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<Result<ReviewCaseDetailResponse>> GetAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        ReviewCaseRow? row = await _dbContext.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId
                && reviewCase.Id == reviewCaseId)
            .Select(reviewCase => new ReviewCaseRow(
                reviewCase.Id,
                reviewCase.Reason,
                reviewCase.Status,
                reviewCase.InvoiceId,
                reviewCase.BankTransactionId,
                reviewCase.SourceLabel,
                reviewCase.Note,
                reviewCase.Outcome,
                reviewCase.ResolutionNote,
                reviewCase.AllocationId,
                reviewCase.OpenedAt,
                reviewCase.ClosedAt,
                EF.Property<byte[]>(reviewCase, "RowVersion")))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Result<ReviewCaseDetailResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        Result<ReviewCaseLinkedFinancials> linked = await ReadLinkedFinancialsAsync(
            organizationId,
            row,
            cancellationToken);
        if (linked.IsFailure)
        {
            return Result<ReviewCaseDetailResponse>.Failure(linked.Error);
        }

        return Result<ReviewCaseDetailResponse>.Success(
            new ReviewCaseDetailResponse(ToResponse(row), linked.Value));
    }

    private async Task<Result<ReviewCaseLinkedFinancials>> ReadLinkedFinancialsAsync(
        OrganizationId organizationId,
        ReviewCaseRow row,
        CancellationToken cancellationToken)
    {
        bool invoiceFound = false;
        MoneyDto? invoiceOutstanding = null;
        if (row.InvoiceId is InvoiceId invoiceId)
        {
            LinkedAmountRead invoice = await ReadInvoiceRemainingAsync(
                organizationId,
                invoiceId,
                cancellationToken);
            if (invoice.Inconsistent)
            {
                return Result<ReviewCaseLinkedFinancials>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney);
            }

            invoiceFound = invoice.Amount is not null;
            invoiceOutstanding = invoice.Amount;
        }

        bool bankFound = false;
        MoneyDto? bankUnapplied = null;
        if (row.BankTransactionId is BankTransactionId bankTransactionId)
        {
            LinkedAmountRead bankTransaction = await ReadBankRemainingAsync(
                organizationId,
                bankTransactionId,
                cancellationToken);
            if (bankTransaction.Inconsistent)
            {
                return Result<ReviewCaseLinkedFinancials>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney);
            }

            bankFound = bankTransaction.Amount is not null;
            bankUnapplied = bankTransaction.Amount;
        }

        return Result<ReviewCaseLinkedFinancials>.Success(
            new ReviewCaseLinkedFinancials(
                invoiceFound,
                invoiceOutstanding,
                bankFound,
                bankUnapplied));
    }

    private async Task<LinkedAmountRead> ReadInvoiceRemainingAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        List<StoredMoneyColumns> rows = await _dbContext.Database.SqlQuery<StoredMoneyColumns>(
            $"""
            SELECT
                [TotalAmount] AS [PrincipalAmount],
                [TotalCurrency] AS [PrincipalCurrency],
                [AllocatedAmount] AS [AllocatedAmount],
                [AllocatedCurrency] AS [AllocatedCurrency]
            FROM [reconciliation].[Invoices]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [Id] = {invoiceId.Value}
            """)
            .ToListAsync(cancellationToken);
        return ToLinkedAmount(rows.Count == 0 ? null : rows[0]);
    }

    private async Task<LinkedAmountRead> ReadBankRemainingAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        List<StoredMoneyColumns> rows = await _dbContext.Database.SqlQuery<StoredMoneyColumns>(
            $"""
            SELECT
                [Amount] AS [PrincipalAmount],
                [Currency] AS [PrincipalCurrency],
                [AllocatedAmount] AS [AllocatedAmount],
                [AllocatedCurrency] AS [AllocatedCurrency]
            FROM [reconciliation].[BankTransactions]
            WHERE [OrganizationId] = {organizationId.Value}
                AND [Id] = {bankTransactionId.Value}
            """)
            .ToListAsync(cancellationToken);
        return ToLinkedAmount(rows.Count == 0 ? null : rows[0]);
    }

    private static LinkedAmountRead ToLinkedAmount(StoredMoneyColumns? columns)
    {
        if (columns is null)
        {
            return new LinkedAmountRead(null, Inconsistent: false);
        }

        if (!TryGetRemaining(columns, out MoneyDto remaining))
        {
            return new LinkedAmountRead(null, Inconsistent: true);
        }

        return new LinkedAmountRead(remaining, Inconsistent: false);
    }

    private static bool TryGetRemaining(StoredMoneyColumns columns, out MoneyDto remaining)
    {
        remaining = null!;
        if (!TryNormalizeCurrency(columns.PrincipalCurrency, out string principalCurrency)
            || !TryNormalizeCurrency(columns.AllocatedCurrency, out string allocatedCurrency)
            || !string.Equals(principalCurrency, allocatedCurrency, StringComparison.Ordinal)
            || columns.PrincipalAmount <= 0m
            || columns.AllocatedAmount < 0m)
        {
            return false;
        }

        Money principal = new(columns.PrincipalAmount, new CurrencyCode(principalCurrency));
        Money allocated = new(columns.AllocatedAmount, new CurrencyCode(allocatedCurrency));
        if (allocated.IsGreaterThan(principal))
        {
            return false;
        }

        Money unapplied = principal.Subtract(allocated);
        remaining = new MoneyDto(unapplied.Amount, unapplied.Currency.Value);
        return true;
    }

    private static bool TryNormalizeCurrency(string? value, out string currency)
    {
        currency = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3)
        {
            return false;
        }

        foreach (char character in normalized)
        {
            if (character is < 'A' or > 'Z')
            {
                return false;
            }
        }

        currency = normalized;
        return true;
    }

    private readonly record struct LinkedAmountRead(MoneyDto? Amount, bool Inconsistent);

    private sealed record StoredMoneyColumns(
        decimal PrincipalAmount,
        string PrincipalCurrency,
        decimal AllocatedAmount,
        string AllocatedCurrency);

    private static ReviewCaseResponse ToResponse(ReviewCaseRow row)
    {
        return new ReviewCaseResponse(
            row.ReviewCaseId.Value,
            row.Reason.ToString(),
            row.Status.ToString(),
            row.InvoiceId?.Value,
            row.BankTransactionId?.Value,
            row.SourceLabel?.Value,
            row.Note,
            row.Outcome?.ToString(),
            row.ResolutionNote,
            row.AllocationId?.Value,
            row.OpenedAt,
            row.ClosedAt,
            ReviewCaseConcurrencyToken.Encode(row.RowVersion));
    }

    private sealed record ReviewCaseRow(
        ReviewCaseId ReviewCaseId,
        ReviewCaseReason Reason,
        ReviewCaseStatus Status,
        InvoiceId? InvoiceId,
        BankTransactionId? BankTransactionId,
        ReviewCaseSource? SourceLabel,
        string? Note,
        ReviewCaseOutcome? Outcome,
        string? ResolutionNote,
        AllocationId? AllocationId,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        byte[] RowVersion);
}