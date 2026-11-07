using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationDisputeResolutionQueries : IAllocationDisputeResolutionQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationDisputeResolutionQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<AllocationDisputeResolutionStateResponse?> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (disputeId == Guid.Empty)
        {
            return null;
        }

        AllocationDisputeId id = new(disputeId);
        bool exists = await _dbContext.AllocationDisputes
            .AsNoTracking()
            .AnyAsync(
                dispute => dispute.OrganizationId == organizationId && dispute.Id == id,
                cancellationToken);
        if (!exists)
        {
            return null;
        }

        StoredResolution? stored = await _dbContext.AllocationDisputeResolutions
            .AsNoTracking()
            .Where(resolution => resolution.OrganizationId == organizationId
                && resolution.AllocationDisputeId == id)
            .Select(resolution => new StoredResolution(
                resolution.Outcome,
                resolution.Reason,
                resolution.ActorSource,
                resolution.ActorId,
                resolution.DecidedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return new AllocationDisputeResolutionStateResponse(disputeId, null);
        }

        return new AllocationDisputeResolutionStateResponse(
            disputeId,
            new AllocationDisputeResolutionResponse(
                disputeId,
                AllocationDisputeResolution.OutcomeName(stored.Outcome),
                stored.Reason,
                RequireStoredActorSource(stored.ActorSource),
                stored.ActorId,
                stored.DecidedAt));
    }

    private static string RequireStoredActorSource(ReviewCaseActorSource source)
    {
        return source switch
        {
            ReviewCaseActorSource.DevelopmentJwt =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.DevelopmentJwt),
            ReviewCaseActorSource.ProductionCookie =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.ProductionCookie),
            _ => throw new InvalidOperationException("Stored resolution actor source is not defined."),
        };
    }

    private sealed record StoredResolution(
        AllocationDisputeResolutionOutcome Outcome,
        string Reason,
        ReviewCaseActorSource ActorSource,
        string ActorId,
        DateTimeOffset DecidedAt);
}