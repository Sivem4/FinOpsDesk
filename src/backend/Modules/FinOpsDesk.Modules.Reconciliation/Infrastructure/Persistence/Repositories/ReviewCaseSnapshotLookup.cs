using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseSnapshotLookup : IReviewCaseSnapshotLookup
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ReviewCaseSnapshotLookup(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    public Task<ReviewCaseSnapshot?> FindOpenBySubjectAsync(
        OrganizationId organizationId,
        string subjectKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKey);

        return FindAsync(
            organizationId,
            reviewCase => reviewCase.SubjectKey == subjectKey
                && reviewCase.Status == ReviewCaseStatus.Open,
            cancellationToken);
    }

    public Task<ReviewCaseSnapshot?> FindByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        return FindAsync(
            organizationId,
            reviewCase => reviewCase.Id == reviewCaseId,
            cancellationToken);
    }

    private async Task<ReviewCaseSnapshot?> FindAsync(
        OrganizationId organizationId,
        System.Linq.Expressions.Expression<Func<ReviewCase, bool>> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>();
        ReviewCase? reviewCase = await context.ReviewCases
            .Where(reviewCase => reviewCase.OrganizationId == organizationId)
            .SingleOrDefaultAsync(predicate, cancellationToken);
        if (reviewCase is null)
        {
            return null;
        }

        byte[] rowVersion = context.Entry(reviewCase)
            .Property<byte[]>("RowVersion")
            .CurrentValue!
            .ToArray();
        return new ReviewCaseSnapshot(
            reviewCase.Id,
            reviewCase.OrganizationId,
            reviewCase.Reason,
            reviewCase.Status,
            reviewCase.InvoiceId,
            reviewCase.BankTransactionId,
            reviewCase.SubjectKey,
            reviewCase.SourceLabel,
            reviewCase.Note,
            reviewCase.Outcome,
            reviewCase.ResolutionNote,
            reviewCase.AllocationId,
            reviewCase.OpenedAt,
            reviewCase.ClosedAt,
            rowVersion);
    }
}