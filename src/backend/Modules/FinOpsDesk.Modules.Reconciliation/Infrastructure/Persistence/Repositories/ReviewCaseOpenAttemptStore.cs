using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseOpenAttemptStore :
    IReviewCaseOpenAttemptLookup,
    IReviewCaseOpenAttemptWriter
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public ReviewCaseOpenAttemptStore(
        ReconciliationDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public void Add(ReviewCaseOpenAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        _dbContext.ReviewCaseOpenAttempts.Add(attempt);
    }

    public async Task InsertAsync(ReviewCaseOpenAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();
        context.ReviewCaseOpenAttempts.Add(attempt);
        await ((IUnitOfWork)context).SaveChangesAsync(cancellationToken);
    }

    public async Task<ReviewCaseOpenAttempt?> FindAsync(
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
            token => context.ReviewCaseOpenAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    attempt => attempt.OrganizationId == organizationId
                        && attempt.IdempotencyKey == idempotencyKey,
                    token),
            cancellationToken);
    }
}