using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationDisputeQueries : IAllocationDisputeQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationDisputeQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<Result<AllocationDisputeListResponse>> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        Guid? allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (!AllocationDisputeListPagination.IsValid(page, pageSize))
        {
            throw new InvalidOperationException("Allocation dispute pagination was not validated.");
        }

        IQueryable<AllocationDispute> disputes = _dbContext.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId);
        if (allocationId is Guid filterValue)
        {
            AllocationId filter = new(filterValue);
            disputes = disputes.Where(dispute => dispute.AllocationId == filter);
        }

        // The count and the page are separate statements. A dispute committed
        // between them can make totalCount differ from items. This read does
        // not join Allocation. The response is the stored registration, which
        // stays unchanged after reversal. Current allocation state remains on
        // the allocation reads. A join is possible and is not used here.
        long totalCount = await disputes.LongCountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<StoredDispute> rows = await disputes
            .OrderBy(dispute => dispute.RecordedAt)
            .ThenBy(dispute => dispute.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(dispute => new StoredDispute(
                dispute.Id,
                dispute.AllocationId,
                dispute.Statement,
                dispute.ActorSource,
                dispute.ActorId,
                dispute.RecordedAt))
            .ToListAsync(cancellationToken);

        AllocationDisputeResponse[] items = new AllocationDisputeResponse[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            items[index] = ToResponse(rows[index]);
        }

        return Result<AllocationDisputeListResponse>.Success(
            new AllocationDisputeListResponse(items, page, pageSize, totalCount));
    }

    public async Task<Result<AllocationDisputeResponse>> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        AllocationDisputeId id = new(disputeId);
        StoredDispute? row = await _dbContext.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId && dispute.Id == id)
            .Select(dispute => new StoredDispute(
                dispute.Id,
                dispute.AllocationId,
                dispute.Statement,
                dispute.ActorSource,
                dispute.ActorId,
                dispute.RecordedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Result<AllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        return Result<AllocationDisputeResponse>.Success(ToResponse(row));
    }

    public async Task<Result<AllocationDisputeWorkSummaryResponse>> SummarizeWorkAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        long needsDecision = await NeedsDecision(organizationId).LongCountAsync(cancellationToken);
        long needsCorrection = await NeedsCorrection(organizationId).LongCountAsync(cancellationToken);
        return Result<AllocationDisputeWorkSummaryResponse>.Success(
            new AllocationDisputeWorkSummaryResponse(needsDecision, needsCorrection));
    }

    public async Task<Result<AllocationDisputeListResponse>> ListQueueAsync(
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (!AllocationDisputeListPagination.IsValid(page, pageSize))
        {
            throw new InvalidOperationException("Allocation dispute pagination was not validated.");
        }

        if (!Enum.IsDefined(queue))
        {
            throw new InvalidOperationException("Allocation dispute queue was not validated.");
        }

        IQueryable<AllocationDispute> disputes = queue == AllocationDisputeQueue.NeedsDecision
            ? NeedsDecision(organizationId)
            : NeedsCorrection(organizationId);
        long totalCount = await disputes.LongCountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<StoredDispute> rows = await disputes
            .OrderBy(dispute => dispute.RecordedAt)
            .ThenBy(dispute => dispute.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(dispute => new StoredDispute(
                dispute.Id,
                dispute.AllocationId,
                dispute.Statement,
                dispute.ActorSource,
                dispute.ActorId,
                dispute.RecordedAt))
            .ToListAsync(cancellationToken);
        AllocationDisputeResponse[] items = new AllocationDisputeResponse[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            items[index] = ToResponse(rows[index]);
        }

        return Result<AllocationDisputeListResponse>.Success(
            new AllocationDisputeListResponse(items, page, pageSize, totalCount));
    }

    private IQueryable<AllocationDispute> NeedsDecision(OrganizationId organizationId)
    {
        return _dbContext.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId)
            .Where(dispute => !_dbContext.AllocationDisputeResolutions.Any(resolution =>
                resolution.OrganizationId == dispute.OrganizationId
                && resolution.AllocationDisputeId == dispute.Id));
    }

    private IQueryable<AllocationDispute> NeedsCorrection(OrganizationId organizationId)
    {
        return _dbContext.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId)
            .Where(dispute => _dbContext.AllocationDisputeResolutions.Any(resolution =>
                resolution.OrganizationId == dispute.OrganizationId
                && resolution.AllocationDisputeId == dispute.Id
                && resolution.Outcome == AllocationDisputeResolutionOutcome.Upheld))
            .Where(dispute => !_dbContext.AllocationDisputeCorrections.Any(correction =>
                correction.OrganizationId == dispute.OrganizationId
                && correction.AllocationDisputeId == dispute.Id))
            .Where(dispute => _dbContext.Allocations.Any(allocation =>
                allocation.OrganizationId == dispute.OrganizationId
                && allocation.Id == dispute.AllocationId
                && allocation.Status == AllocationStatus.Confirmed));
    }

    private static AllocationDisputeResponse ToResponse(StoredDispute row)
    {
        return new AllocationDisputeResponse(
            row.Id.Value,
            row.AllocationId.Value,
            row.Statement,
            RequireStoredActorSource(row.ActorSource),
            row.ActorId,
            row.RecordedAt);
    }

    private static string RequireStoredActorSource(ReviewCaseActorSource source)
    {
        return source switch
        {
            ReviewCaseActorSource.DevelopmentJwt =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.DevelopmentJwt),
            ReviewCaseActorSource.ProductionCookie =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.ProductionCookie),
            _ => throw new InvalidOperationException(
                "Stored allocation dispute actor source is not one of the two allowed values."),
        };
    }

    private sealed record StoredDispute(
        AllocationDisputeId Id,
        AllocationId AllocationId,
        string Statement,
        ReviewCaseActorSource ActorSource,
        string ActorId,
        DateTimeOffset RecordedAt);
}