using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ResolveAllocationDisputeCommand(
    AllocationDisputeId DisputeId,
    AllocationDisputeResolutionOutcome Outcome,
    string? Reason,
    Guid IdempotencyKey);

public sealed record AllocationDisputeResolutionResponse(
    Guid DisputeId,
    string Outcome,
    string Reason,
    string ActorSource,
    string ActorId,
    DateTimeOffset DecidedAt);

public sealed record ResolveAllocationDisputeResult(
    AllocationDisputeResolutionResponse Decision,
    bool Created);

public sealed record AllocationDisputeResolutionStateResponse(
    Guid DisputeId,
    AllocationDisputeResolutionResponse? Decision);

public sealed record GetAllocationDisputeResolutionQuery(Guid DisputeId);

public interface IAllocationDisputeResolutionQueries
{
    Task<AllocationDisputeResolutionStateResponse?> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeResolutionLookup
{
    Task<AllocationDisputeResolution?> FindByIdempotencyKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<AllocationDisputeResolution?> FindByDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken);

    Task<bool> DisputeExistsAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeResolutionWriter
{
    void Add(AllocationDisputeResolution resolution);
}

public interface IAllocationDisputeResolutionConflict
{
    bool IsRecognizedUniqueConflict(Exception exception);

    bool IsDisputeForeignKeyConflict(Exception exception);
}