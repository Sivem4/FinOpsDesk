using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class ClaimReviewCaseHandler
{
    private readonly ReviewCaseAssignmentCommand _command;

    public ClaimReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseAssignmentLookup assignmentLookup,
        IReviewCaseAssignmentWriter assignmentWriter,
        IReviewCaseAssignmentConflict assignmentConflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _command = new ReviewCaseAssignmentCommand(
            currentOrganizationContext,
            currentActorAttribution,
            reviewCaseRepository,
            assignmentLookup,
            assignmentWriter,
            assignmentConflict,
            unitOfWork,
            timeProvider);
    }

    public Task<Result<ChangeReviewCaseAssignmentResponse>> Handle(
        ClaimReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _command.ExecuteAsync(
            ReviewCaseAssignmentOperation.Claim,
            command.ReviewCaseId,
            command.RowVersion,
            command.CanonicalConcurrencyToken,
            command.IdempotencyKey,
            cancellationToken);
    }
}

public sealed class ReleaseReviewCaseHandler
{
    private readonly ReviewCaseAssignmentCommand _command;

    public ReleaseReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseAssignmentLookup assignmentLookup,
        IReviewCaseAssignmentWriter assignmentWriter,
        IReviewCaseAssignmentConflict assignmentConflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _command = new ReviewCaseAssignmentCommand(
            currentOrganizationContext,
            currentActorAttribution,
            reviewCaseRepository,
            assignmentLookup,
            assignmentWriter,
            assignmentConflict,
            unitOfWork,
            timeProvider);
    }

    public Task<Result<ChangeReviewCaseAssignmentResponse>> Handle(
        ReleaseReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _command.ExecuteAsync(
            ReviewCaseAssignmentOperation.Release,
            command.ReviewCaseId,
            command.RowVersion,
            command.CanonicalConcurrencyToken,
            command.IdempotencyKey,
            cancellationToken);
    }
}

public sealed class GetReviewCaseAssignmentHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseRepository _reviewCaseRepository;

    public GetReviewCaseAssignmentHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseRepository reviewCaseRepository)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);

        _currentOrganizationContext = currentOrganizationContext;
        _reviewCaseRepository = reviewCaseRepository;
    }

    public async Task<Result<ReviewCaseAssignmentStateResponse>> Handle(
        GetReviewCaseAssignmentQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        ReviewCase? reviewCase = await _reviewCaseRepository.GetByIdAsync(
            _currentOrganizationContext.OrganizationId,
            query.ReviewCaseId,
            cancellationToken);
        if (reviewCase is null)
        {
            return Result<ReviewCaseAssignmentStateResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        return Result<ReviewCaseAssignmentStateResponse>.Success(
            new ReviewCaseAssignmentStateResponse(
                reviewCase.Id.Value,
                ReviewCaseAssignmentCommand.AssigneeOf(reviewCase),
                ReviewCaseConcurrencyToken.Encode(_reviewCaseRepository.GetRowVersion(reviewCase))));
    }
}

internal sealed class ReviewCaseAssignmentCommand
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorAttribution _currentActorAttribution;
    private readonly IReviewCaseRepository _reviewCaseRepository;
    private readonly IReviewCaseAssignmentLookup _assignmentLookup;
    private readonly IReviewCaseAssignmentWriter _assignmentWriter;
    private readonly IReviewCaseAssignmentConflict _assignmentConflict;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ReviewCaseAssignmentCommand(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseAssignmentLookup assignmentLookup,
        IReviewCaseAssignmentWriter assignmentWriter,
        IReviewCaseAssignmentConflict assignmentConflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorAttribution);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(assignmentLookup);
        ArgumentNullException.ThrowIfNull(assignmentWriter);
        ArgumentNullException.ThrowIfNull(assignmentConflict);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorAttribution = currentActorAttribution;
        _reviewCaseRepository = reviewCaseRepository;
        _assignmentLookup = assignmentLookup;
        _assignmentWriter = assignmentWriter;
        _assignmentConflict = assignmentConflict;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ChangeReviewCaseAssignmentResponse>> ExecuteAsync(
        ReviewCaseAssignmentOperation operation,
        ReviewCaseId reviewCaseId,
        byte[] rowVersion,
        string canonicalConcurrencyToken,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (reviewCaseId is null
            || idempotencyKey == Guid.Empty
            || !IsCanonical(rowVersion, canonicalConcurrencyToken))
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
        }

        if (!_currentActorAttribution.TryGet(out ReviewCaseActorSource actorSource, out string actorId))
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ReviewCaseAssignmentEntry? stored = await _assignmentLookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (stored is not null)
        {
            return Classify(stored, reviewCaseId, operation, actorSource, actorId, canonicalConcurrencyToken);
        }

        ReviewCase? reviewCase = await _reviewCaseRepository.GetByIdAsync(
            organizationId,
            reviewCaseId,
            cancellationToken);
        if (reviewCase is null)
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        byte[] current = _reviewCaseRepository.GetRowVersion(reviewCase);
        if (!current.AsSpan().SequenceEqual(rowVersion))
        {
            return await RecoverAsync(
                organizationId,
                idempotencyKey,
                reviewCaseId,
                operation,
                actorSource,
                actorId,
                canonicalConcurrencyToken,
                cancellationToken);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        ReviewCaseAssignmentRejection? rejection = operation == ReviewCaseAssignmentOperation.Claim
            ? reviewCase.Claim(actorSource, actorId, now)
            : reviewCase.Release(actorSource, actorId);
        if (rejection is not null)
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(ErrorFor(rejection.Value));
        }

        ReviewCaseAssignmentEntry entry = ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            organizationId,
            reviewCaseId,
            operation,
            actorSource,
            actorId,
            canonicalConcurrencyToken,
            idempotencyKey,
            now,
            operation == ReviewCaseAssignmentOperation.Claim ? now : null);
        _assignmentWriter.Add(entry);
        _reviewCaseRepository.ExpectRowVersion(reviewCase, rowVersion);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_assignmentConflict.IsIdempotencyKeyConflict(exception))
        {
            return await RecoverAsync(
                organizationId,
                idempotencyKey,
                reviewCaseId,
                operation,
                actorSource,
                actorId,
                canonicalConcurrencyToken,
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return await RecoverAsync(
                organizationId,
                idempotencyKey,
                reviewCaseId,
                operation,
                actorSource,
                actorId,
                canonicalConcurrencyToken,
                cancellationToken);
        }

        return Result<ChangeReviewCaseAssignmentResponse>.Success(
            new ChangeReviewCaseAssignmentResponse(ToResponse(entry), Created: true));
    }

    internal static ReviewCaseAssigneeResponse? AssigneeOf(ReviewCase reviewCase)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        if (reviewCase.AssignedActorSource is null)
        {
            return null;
        }

        return new ReviewCaseAssigneeResponse(
            ActorSourceName(reviewCase.AssignedActorSource.Value),
            reviewCase.AssignedActorId!,
            reviewCase.AssignedAt!.Value);
    }

    private async Task<Result<ChangeReviewCaseAssignmentResponse>> RecoverAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string canonicalConcurrencyToken,
        CancellationToken cancellationToken)
    {
        ReviewCaseAssignmentEntry? winner = await _assignmentLookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (winner is null)
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict);
        }

        return Classify(winner, reviewCaseId, operation, actorSource, actorId, canonicalConcurrencyToken);
    }

    private static Result<ChangeReviewCaseAssignmentResponse> Classify(
        ReviewCaseAssignmentEntry stored,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string canonicalConcurrencyToken)
    {
        if (!stored.Matches(reviewCaseId, operation, actorSource, actorId, canonicalConcurrencyToken))
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict);
        }

        return Result<ChangeReviewCaseAssignmentResponse>.Success(
            new ChangeReviewCaseAssignmentResponse(ToResponse(stored), Created: false));
    }

    private static ReviewCaseAssignmentResponse ToResponse(ReviewCaseAssignmentEntry entry)
    {
        ReviewCaseAssigneeResponse? assignee = entry.Operation == ReviewCaseAssignmentOperation.Claim
            ? new ReviewCaseAssigneeResponse(
                ActorSourceName(entry.ActorSource),
                entry.ActorId,
                entry.AssignedAt!.Value)
            : null;
        return new ReviewCaseAssignmentResponse(
            entry.ReviewCaseId.Value,
            OperationName(entry.Operation),
            assignee,
            entry.RecordedAt);
    }

    internal static string OperationName(ReviewCaseAssignmentOperation operation)
    {
        return operation == ReviewCaseAssignmentOperation.Claim ? "claim" : "release";
    }

    internal static string ActorSourceName(ReviewCaseActorSource source)
    {
        return source == ReviewCaseActorSource.DevelopmentJwt ? "developmentJwt" : "productionCookie";
    }

    private static bool IsCanonical(byte[]? rowVersion, string? canonicalConcurrencyToken)
    {
        if (rowVersion is null
            || rowVersion.Length != ReviewCaseConcurrencyToken.RowVersionLength
            || canonicalConcurrencyToken is null
            || canonicalConcurrencyToken.Length != ReviewCaseAssignmentEntry.CanonicalTokenLength)
        {
            return false;
        }

        return string.Equals(
            ReviewCaseConcurrencyToken.Encode(rowVersion),
            canonicalConcurrencyToken,
            StringComparison.Ordinal);
    }

    private static Error ErrorFor(ReviewCaseAssignmentRejection rejection)
    {
        return rejection switch
        {
            ReviewCaseAssignmentRejection.Terminal =>
                ReconciliationApplicationErrors.ReviewCaseAssignmentTerminal,
            ReviewCaseAssignmentRejection.AlreadyAssigned =>
                ReconciliationApplicationErrors.ReviewCaseAssignmentAlreadyAssigned,
            ReviewCaseAssignmentRejection.NotAssigned =>
                ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssigned,
            ReviewCaseAssignmentRejection.NotAssignee =>
                ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssignee,
            _ => throw new InvalidOperationException("Assignment rejection is not defined."),
        };
    }
}