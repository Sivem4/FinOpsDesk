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

    public async Task<Result<AllocationDisputeQueueCursorResponse>> ListQueueCursorAsync(
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int pageSize,
        AllocationDisputeQueueCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (!AllocationDisputeListPagination.IsValidPageSize(pageSize))
        {
            throw new InvalidOperationException("Allocation dispute pagination was not validated.");
        }

        if (!Enum.IsDefined(queue))
        {
            throw new InvalidOperationException("Allocation dispute queue was not validated.");
        }

        if (cursor is not null && (cursor.Queue != queue || !Enum.IsDefined(cursor.Direction)))
        {
            throw new InvalidOperationException("Allocation dispute cursor was not validated.");
        }

        bool forward = cursor is null || cursor.Direction == AllocationDisputeQueueCursorDirection.Forward;
        IQueryable<AllocationDispute> source = cursor is null
            ? _dbContext.AllocationDisputes
            : KeysetSource(cursor, forward);
        IQueryable<AllocationDispute> disputes = queue == AllocationDisputeQueue.NeedsDecision
            ? NeedsDecision(organizationId, source)
            : NeedsCorrection(organizationId, source);

        IOrderedQueryable<AllocationDispute> ordered = forward
            ? disputes.OrderBy(dispute => dispute.RecordedAt).ThenBy(dispute => dispute.Id)
            : disputes.OrderByDescending(dispute => dispute.RecordedAt).ThenByDescending(dispute => dispute.Id);
        List<StoredDispute> fetched = await ordered
            .Take(pageSize + 1)
            .Select(dispute => new StoredDispute(
                dispute.Id,
                dispute.AllocationId,
                dispute.Statement,
                dispute.ActorSource,
                dispute.ActorId,
                dispute.RecordedAt))
            .ToListAsync(cancellationToken);
        bool hasExtra = fetched.Count == pageSize + 1;
        if (hasExtra)
        {
            fetched.RemoveAt(fetched.Count - 1);
        }

        if (!forward)
        {
            fetched.Reverse();
        }

        AllocationDisputeResponse[] items = new AllocationDisputeResponse[fetched.Count];
        for (int index = 0; index < fetched.Count; index++)
        {
            items[index] = ToResponse(fetched[index]);
        }

        string? nextCursor = null;
        string? previousCursor = null;
        if (fetched.Count == 0)
        {
            if (forward && cursor is not null)
            {
                previousCursor = Encode(queue, AllocationDisputeQueueCursorDirection.Backward, cursor.RecordedAt, cursor.DisputeId);
            }
        }
        else
        {
            StoredDispute first = fetched[0];
            StoredDispute last = fetched[^1];
            if (forward)
            {
                if (hasExtra)
                {
                    nextCursor = Encode(queue, AllocationDisputeQueueCursorDirection.Forward, last.RecordedAt, last.Id.Value);
                }

                if (cursor is not null)
                {
                    previousCursor = Encode(queue, AllocationDisputeQueueCursorDirection.Backward, first.RecordedAt, first.Id.Value);
                }
            }
            else
            {
                nextCursor = Encode(queue, AllocationDisputeQueueCursorDirection.Forward, last.RecordedAt, last.Id.Value);
                if (hasExtra)
                {
                    previousCursor = Encode(queue, AllocationDisputeQueueCursorDirection.Backward, first.RecordedAt, first.Id.Value);
                }
            }
        }

        return Result<AllocationDisputeQueueCursorResponse>.Success(
            new AllocationDisputeQueueCursorResponse(items, pageSize, nextCursor, previousCursor));
    }

    private static string Encode(
        AllocationDisputeQueue queue,
        AllocationDisputeQueueCursorDirection direction,
        DateTimeOffset recordedAt,
        Guid disputeId)
    {
        return AllocationDisputeQueueCursor.Encode(direction, queue, recordedAt, disputeId);
    }

    private IQueryable<AllocationDispute> NeedsDecision(OrganizationId organizationId)
    {
        return NeedsDecision(organizationId, _dbContext.AllocationDisputes);
    }

    private IQueryable<AllocationDispute> NeedsDecision(
        OrganizationId organizationId,
        IQueryable<AllocationDispute> source)
    {
        return source
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId)
            .Where(dispute => !_dbContext.AllocationDisputeResolutions.Any(resolution =>
                resolution.OrganizationId == dispute.OrganizationId
                && resolution.AllocationDisputeId == dispute.Id));
    }

    private IQueryable<AllocationDispute> NeedsCorrection(OrganizationId organizationId)
    {
        return NeedsCorrection(organizationId, _dbContext.AllocationDisputes);
    }

    private IQueryable<AllocationDispute> NeedsCorrection(
        OrganizationId organizationId,
        IQueryable<AllocationDispute> source)
    {
        return source
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

    private IQueryable<AllocationDispute> KeysetSource(AllocationDisputeQueueCursor cursor, bool forward)
    {
        DateTimeOffset boundaryAt = cursor.RecordedAt;
        Guid boundaryId = cursor.DisputeId;
        if (forward)
        {
            return _dbContext.AllocationDisputes.FromSqlInterpolated($"""
                SELECT [d].*
                FROM [reconciliation].[AllocationDisputes] AS [d]
                WHERE [d].[RecordedAt] > {boundaryAt}
                    OR ([d].[RecordedAt] = {boundaryAt} AND [d].[Id] > {boundaryId})
                """);
        }

        return _dbContext.AllocationDisputes.FromSqlInterpolated($"""
            SELECT [d].*
            FROM [reconciliation].[AllocationDisputes] AS [d]
            WHERE [d].[RecordedAt] < {boundaryAt}
                OR ([d].[RecordedAt] = {boundaryAt} AND [d].[Id] < {boundaryId})
            """);
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