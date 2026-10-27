using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class OpenReviewBlockQueries : IOpenReviewBlockQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public OpenReviewBlockQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ReviewBlockSubject>> ListOpenAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        List<ReviewSubjectRow> rows = await OpenCases(organizationId)
            .Select(reviewCase => new ReviewSubjectRow(
                reviewCase.Status,
                reviewCase.Reason,
                reviewCase.InvoiceId,
                reviewCase.BankTransactionId))
            .ToListAsync(cancellationToken);

        return Map(rows);
    }

    public async Task<IReadOnlyList<ReviewBlockSubject>> ListOpenForPairAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        List<ReviewSubjectRow> rows = await OpenCases(organizationId)
            .Where(reviewCase => reviewCase.InvoiceId == invoiceId
                || reviewCase.BankTransactionId == bankTransactionId)
            .Select(reviewCase => new ReviewSubjectRow(
                reviewCase.Status,
                reviewCase.Reason,
                reviewCase.InvoiceId,
                reviewCase.BankTransactionId))
            .ToListAsync(cancellationToken);

        return Map(rows);
    }

    private IQueryable<ReviewCase> OpenCases(OrganizationId organizationId)
    {
        return _dbContext.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId
                && reviewCase.Status == ReviewCaseStatus.Open);
    }

    private static ReviewBlockSubject[] Map(List<ReviewSubjectRow> rows)
    {
        ReviewBlockSubject[] subjects = new ReviewBlockSubject[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            ReviewSubjectRow row = rows[index];
            subjects[index] = new ReviewBlockSubject(
                row.Status,
                row.Reason,
                row.InvoiceId,
                row.BankTransactionId);
        }

        return subjects;
    }

    private sealed record ReviewSubjectRow(
        ReviewCaseStatus Status,
        ReviewCaseReason Reason,
        InvoiceId? InvoiceId,
        BankTransactionId? BankTransactionId);
}