using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ResolveAllocationDisputeHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorAttribution _currentActorAttribution;
    private readonly IAllocationDisputeResolutionLookup _lookup;
    private readonly IAllocationDisputeResolutionWriter _writer;
    private readonly IAllocationDisputeResolutionConflict _conflict;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ResolveAllocationDisputeHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IAllocationDisputeResolutionLookup lookup,
        IAllocationDisputeResolutionWriter writer,
        IAllocationDisputeResolutionConflict conflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorAttribution);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorAttribution = currentActorAttribution;
        _lookup = lookup;
        _writer = writer;
        _conflict = conflict;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ResolveAllocationDisputeResult>> Handle(
        ResolveAllocationDisputeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IdempotencyKey == Guid.Empty)
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyKeyInvalid);
        }

        if (!_currentActorAttribution.TryGet(out ReviewCaseActorSource actorSource, out string actorId))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        if (!Enum.IsDefined(command.Outcome))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest);
        }

        if (!AllocationDisputeResolution.TryNormalizeReason(
                command.Reason,
                out string reason,
                out AllocationDisputeResolutionReasonIssue issue))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(ReasonError(issue));
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        AllocationDisputeResolution? storedKey = await _lookup.FindByIdempotencyKeyAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);
        if (storedKey is not null)
        {
            return Classify(storedKey, command.DisputeId, command.Outcome, reason);
        }

        if (!await _lookup.DisputeExistsAsync(organizationId, command.DisputeId, cancellationToken))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        AllocationDisputeResolution? storedDispute = await _lookup.FindByDisputeAsync(
            organizationId,
            command.DisputeId,
            cancellationToken);
        if (storedDispute is not null)
        {
            return await ClassifyStoredDisputeAsync(
                storedDispute,
                organizationId,
                command.DisputeId,
                command.Outcome,
                reason,
                command.IdempotencyKey,
                cancellationToken);
        }

        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            organizationId,
            command.DisputeId,
            command.Outcome,
            reason,
            actorSource,
            actorId,
            _timeProvider.GetUtcNow(),
            command.IdempotencyKey);
        _writer.Add(resolution);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintConflictException exception)
            when (_conflict.IsRecognizedUniqueConflict(exception))
        {
            return await RecoverAsync(
                organizationId,
                command.DisputeId,
                command.Outcome,
                reason,
                command.IdempotencyKey,
                cancellationToken);
        }
        catch (DbUpdateException exception)
            when (_conflict.IsDisputeForeignKeyConflict(exception))
        {
            return await RecoverForeignKeyAsync(organizationId, command.DisputeId, cancellationToken);
        }

        return Created(resolution);
    }

    private async Task<Result<ResolveAllocationDisputeResult>> ClassifyStoredDisputeAsync(
        AllocationDisputeResolution storedDispute,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (storedDispute.IdempotencyKey == idempotencyKey)
        {
            return Classify(storedDispute, disputeId, outcome, reason);
        }

        return await ClassifyKeyOrAlreadyResolvedAsync(
            organizationId,
            disputeId,
            outcome,
            reason,
            idempotencyKey,
            cancellationToken);
    }

    private async Task<Result<ResolveAllocationDisputeResult>> RecoverAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        AllocationDisputeResolution? storedKey = await _lookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (storedKey is not null)
        {
            return Classify(storedKey, disputeId, outcome, reason);
        }

        AllocationDisputeResolution? storedDispute = await _lookup.FindByDisputeAsync(
            organizationId,
            disputeId,
            cancellationToken);
        if (storedDispute is null)
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeResolutionConcurrencyConflict);
        }

        if (storedDispute.IdempotencyKey == idempotencyKey)
        {
            return Classify(storedDispute, disputeId, outcome, reason);
        }

        return await ClassifyKeyOrAlreadyResolvedAsync(
            organizationId,
            disputeId,
            outcome,
            reason,
            idempotencyKey,
            cancellationToken);
    }

    private async Task<Result<ResolveAllocationDisputeResult>> ClassifyKeyOrAlreadyResolvedAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        AllocationDisputeResolution? racedKey = await _lookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (racedKey is not null)
        {
            return Classify(racedKey, disputeId, outcome, reason);
        }

        return Result<ResolveAllocationDisputeResult>.Failure(
            ReconciliationApplicationErrors.AllocationDisputeResolutionAlreadyResolved);
    }

    private async Task<Result<ResolveAllocationDisputeResult>> RecoverForeignKeyAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        if (!await _lookup.DisputeExistsAsync(organizationId, disputeId, cancellationToken))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        return Result<ResolveAllocationDisputeResult>.Failure(
            ReconciliationApplicationErrors.AllocationDisputeResolutionConcurrencyConflict);
    }

    private static Result<ResolveAllocationDisputeResult> Classify(
        AllocationDisputeResolution stored,
        AllocationDisputeId disputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason)
    {
        if (!stored.Matches(disputeId, outcome, reason))
        {
            return Result<ResolveAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyPayloadConflict);
        }

        return Result<ResolveAllocationDisputeResult>.Success(
            new ResolveAllocationDisputeResult(ToResponse(stored), Created: false));
    }

    private static Result<ResolveAllocationDisputeResult> Created(AllocationDisputeResolution resolution)
    {
        return Result<ResolveAllocationDisputeResult>.Success(
            new ResolveAllocationDisputeResult(ToResponse(resolution), Created: true));
    }

    private static AllocationDisputeResolutionResponse ToResponse(AllocationDisputeResolution resolution)
    {
        return new AllocationDisputeResolutionResponse(
            resolution.AllocationDisputeId.Value,
            AllocationDisputeResolution.OutcomeName(resolution.Outcome),
            resolution.Reason,
            AllocationDispute.ActorSourceName(resolution.ActorSource),
            resolution.ActorId,
            resolution.DecidedAt);
    }

    private static Error ReasonError(AllocationDisputeResolutionReasonIssue issue)
    {
        return issue == AllocationDisputeResolutionReasonIssue.TooLong
            ? ReconciliationApplicationErrors.AllocationDisputeResolutionReasonTooLong
            : ReconciliationApplicationErrors.AllocationDisputeResolutionReasonRequired;
    }
}