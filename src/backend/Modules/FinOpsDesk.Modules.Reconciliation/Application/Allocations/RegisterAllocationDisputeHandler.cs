using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class RegisterAllocationDisputeHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorAttribution _currentActorAttribution;
    private readonly IAllocationDisputeLookup _lookup;
    private readonly IAllocationDisputeWriter _writer;
    private readonly IAllocationDisputeConflict _conflict;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RegisterAllocationDisputeHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IAllocationDisputeLookup lookup,
        IAllocationDisputeWriter writer,
        IAllocationDisputeConflict conflict,
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

    public async Task<Result<RegisterAllocationDisputeResult>> Handle(
        RegisterAllocationDisputeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IdempotencyKey == Guid.Empty)
        {
            return Result<RegisterAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeIdempotencyKeyInvalid);
        }

        if (!_currentActorAttribution.TryGet(out ReviewCaseActorSource actorSource, out string actorId))
        {
            return Result<RegisterAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        if (!AllocationDispute.TryNormalizeStatement(
                command.Statement,
                out string statement,
                out AllocationDisputeStatementIssue issue))
        {
            return Result<RegisterAllocationDisputeResult>.Failure(StatementError(issue));
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        AllocationDispute? stored = await _lookup.FindByIdempotencyKeyAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);
        if (stored is not null)
        {
            return Classify(stored, command.AllocationId, statement);
        }

        await using IAllocationRowLock registration = await _writer.LockAllocationAsync(
            organizationId,
            command.AllocationId,
            cancellationToken);
        if (registration.Found && registration.Status == AllocationStatus.Confirmed)
        {
            AllocationDispute dispute = AllocationDispute.Create(
                AllocationDisputeId.New(),
                organizationId,
                command.AllocationId,
                statement,
                actorSource,
                actorId,
                _timeProvider.GetUtcNow(),
                command.IdempotencyKey);
            _writer.Add(dispute);
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintConflictException exception)
                when (_conflict.IsIdempotencyKeyConflict(exception))
            {
                await registration.RollbackAsync(cancellationToken);
                return await ClassifyFreshAsync(
                    organizationId,
                    command.AllocationId,
                    command.IdempotencyKey,
                    statement,
                    missingWinner: ReconciliationApplicationErrors.AllocationDisputeConcurrencyConflict,
                    cancellationToken);
            }

            await registration.CommitAsync(cancellationToken);
            return Created(dispute);
        }

        await registration.RollbackAsync(cancellationToken);
        return await ClassifyFreshAsync(
            organizationId,
            command.AllocationId,
            command.IdempotencyKey,
            statement,
            missingWinner: registration.Found
                ? ReconciliationApplicationErrors.AllocationDisputeAllocationNotConfirmed
                : ReconciliationApplicationErrors.AllocationNotFound,
            cancellationToken);
    }

    private async Task<Result<RegisterAllocationDisputeResult>> ClassifyFreshAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid idempotencyKey,
        string statement,
        Error missingWinner,
        CancellationToken cancellationToken)
    {
        AllocationDispute? winner = await _lookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (winner is null)
        {
            return Result<RegisterAllocationDisputeResult>.Failure(missingWinner);
        }

        return Classify(winner, allocationId, statement);
    }

    private static Result<RegisterAllocationDisputeResult> Classify(
        AllocationDispute stored,
        AllocationId allocationId,
        string statement)
    {
        if (!stored.Matches(allocationId, statement))
        {
            return Result<RegisterAllocationDisputeResult>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeIdempotencyPayloadConflict);
        }

        return Result<RegisterAllocationDisputeResult>.Success(
            new RegisterAllocationDisputeResult(ToResponse(stored), Created: false));
    }

    private static Result<RegisterAllocationDisputeResult> Created(AllocationDispute dispute)
    {
        return Result<RegisterAllocationDisputeResult>.Success(
            new RegisterAllocationDisputeResult(ToResponse(dispute), Created: true));
    }

    private static AllocationDisputeResponse ToResponse(AllocationDispute dispute)
    {
        return new AllocationDisputeResponse(
            dispute.Id.Value,
            dispute.AllocationId.Value,
            dispute.Statement,
            AllocationDispute.ActorSourceName(dispute.ActorSource),
            dispute.ActorId,
            dispute.RecordedAt);
    }

    private static Error StatementError(AllocationDisputeStatementIssue issue)
    {
        return issue == AllocationDisputeStatementIssue.TooLong
            ? ReconciliationApplicationErrors.AllocationDisputeStatementTooLong
            : ReconciliationApplicationErrors.AllocationDisputeStatementRequired;
    }
}