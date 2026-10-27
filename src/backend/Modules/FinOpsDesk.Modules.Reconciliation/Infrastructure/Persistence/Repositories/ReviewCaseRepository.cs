using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseRepository : IReviewCaseRepository
{
    private readonly ReconciliationDbContext _dbContext;

    public ReviewCaseRepository(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public void Add(ReviewCase reviewCase)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        _dbContext.ReviewCases.Add(reviewCase);
    }

    public Task<ReviewCase?> GetByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        return _dbContext.ReviewCases.SingleOrDefaultAsync(
            reviewCase => reviewCase.OrganizationId == organizationId
                && reviewCase.Id == reviewCaseId,
            cancellationToken);
    }

    public byte[] GetRowVersion(ReviewCase reviewCase)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        byte[]? rowVersion = _dbContext.Entry(reviewCase)
            .Property<byte[]>("RowVersion")
            .CurrentValue;
        if (rowVersion is null || rowVersion.Length != ReviewCaseConcurrencyToken.RowVersionLength)
        {
            throw new InvalidOperationException("Review case row version is missing.");
        }

        return rowVersion.ToArray();
    }

    public void ExpectRowVersion(ReviewCase reviewCase, byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);
        ArgumentNullException.ThrowIfNull(rowVersion);

        _dbContext.Entry(reviewCase)
            .Property<byte[]>("RowVersion")
            .OriginalValue = rowVersion.ToArray();
    }
}