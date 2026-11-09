using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record RegisterAllocationDisputeCommand(
    AllocationId AllocationId,
    string? Statement,
    Guid IdempotencyKey);

public sealed record AllocationDisputeResponse(
    Guid DisputeId,
    Guid AllocationId,
    string Statement,
    string ActorSource,
    string ActorId,
    DateTimeOffset RecordedAt);

public sealed record RegisterAllocationDisputeResult(
    AllocationDisputeResponse Dispute,
    bool Created);

public sealed record AllocationDisputeListResponse(
    IReadOnlyList<AllocationDisputeResponse> Items,
    int Page,
    int PageSize,
    long TotalCount);

public sealed record GetAllocationDisputesQuery(
    int Page,
    int PageSize,
    Guid? AllocationId);

public sealed record GetAllocationDisputeQuery(Guid DisputeId);

public enum AllocationDisputeQueue
{
    NeedsDecision = 1,
    NeedsCorrection = 2,
}

public sealed record AllocationDisputeWorkSummaryResponse(
    long NeedsDecision,
    long NeedsCorrection);

public sealed record GetAllocationDisputeWorkSummaryQuery;

public sealed record GetAllocationDisputeQueueQuery(
    AllocationDisputeQueue Queue,
    int Page,
    int PageSize);

public sealed record AllocationDisputeQueueCursorResponse(
    IReadOnlyList<AllocationDisputeResponse> Items,
    int PageSize,
    string? NextCursor,
    string? PreviousCursor);

public sealed record GetAllocationDisputeQueueCursorQuery(
    AllocationDisputeQueue Queue,
    int PageSize,
    AllocationDisputeQueueCursor? Cursor);

public interface IAllocationDisputeQueries
{
    Task<Result<AllocationDisputeListResponse>> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        Guid? allocationId,
        CancellationToken cancellationToken);

    Task<Result<AllocationDisputeResponse>> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken);

    Task<Result<AllocationDisputeWorkSummaryResponse>> SummarizeWorkAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken);

    Task<Result<AllocationDisputeListResponse>> ListQueueAsync(
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Result<AllocationDisputeQueueCursorResponse>> ListQueueCursorAsync(
        OrganizationId organizationId,
        AllocationDisputeQueue queue,
        int pageSize,
        AllocationDisputeQueueCursor? cursor,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeLookup
{
    Task<AllocationDispute?> FindByIdempotencyKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeWriter
{
    Task<IAllocationRowLock> LockAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);

    void Add(AllocationDispute dispute);
}

public interface IAllocationRowLock : IAsyncDisposable
{
    bool Found { get; }

    AllocationStatus? Status { get; }

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IAllocationDisputeConflict
{
    bool IsIdempotencyKeyConflict(Exception exception);
}