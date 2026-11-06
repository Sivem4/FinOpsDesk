using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseAssignmentStore :
    IReviewCaseAssignmentLookup,
    IReviewCaseAssignmentWriter
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public ReviewCaseAssignmentStore(
        ReconciliationDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public void Add(ReviewCaseAssignmentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _dbContext.ReviewCaseAssignmentEntries.Add(entry);
    }

    public async Task<ReviewCaseAssignmentEntry?> FindByIdempotencyKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>();
        IFinancialCommitGuard guard = scope.ServiceProvider.GetRequiredService<IFinancialCommitGuard>();
        return await FinancialCommitRead.ExecuteAsync(
            context,
            guard,
            token => context.ReviewCaseAssignmentEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.OrganizationId == organizationId
                        && entry.IdempotencyKey == idempotencyKey,
                    token),
            cancellationToken);
    }
}

internal sealed class ReviewCaseAssignmentConflict : IReviewCaseAssignmentConflict
{
    public bool IsIdempotencyKeyConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && sqlException.Message.Contains(
                    ReviewCaseAssignmentEntryConfiguration.IdempotencyKeyIndexName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}