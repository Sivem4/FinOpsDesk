using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
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