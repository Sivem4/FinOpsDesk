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
        IReviewCaseDelegationDirectory delegationDirectory,
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
            delegationDirectory,
            unitOfWork,
            timeProvider);
    }

    public Task<Result<ChangeReviewCaseAssignmentResponse>> Handle(
        ClaimReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ReviewCaseAssignmentCommand.MapAssignmentAsync(_command.ExecuteAsync(
            ReviewCaseAssignmentOperation.Claim,
            command.ReviewCaseId,
            command.RowVersion,
            command.CanonicalConcurrencyToken,
            command.IdempotencyKey,
            recipientActorId: null,
            cancellationToken));
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
        IReviewCaseDelegationDirectory delegationDirectory,
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
            delegationDirectory,
            unitOfWork,
            timeProvider);
    }

    public Task<Result<ChangeReviewCaseAssignmentResponse>> Handle(
        ReleaseReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ReviewCaseAssignmentCommand.MapAssignmentAsync(_command.ExecuteAsync(
            ReviewCaseAssignmentOperation.Release,
            command.ReviewCaseId,
            command.RowVersion,
            command.CanonicalConcurrencyToken,
            command.IdempotencyKey,
            recipientActorId: null,
            cancellationToken));
    }
}

public sealed class DelegateReviewCaseHandler
{
    private readonly ReviewCaseAssignmentCommand _command;

    public DelegateReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseAssignmentLookup assignmentLookup,
        IReviewCaseAssignmentWriter assignmentWriter,
        IReviewCaseAssignmentConflict assignmentConflict,
        IReviewCaseDelegationDirectory delegationDirectory,
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
            delegationDirectory,
            unitOfWork,
            timeProvider);
    }

    public Task<Result<ChangeReviewCaseDelegationResponse>> Handle(
        DelegateReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ReviewCaseAssignmentCommand.MapDelegationAsync(_command.ExecuteAsync(
            ReviewCaseAssignmentOperation.Delegate,
            command.ReviewCaseId,
            command.RowVersion,
            command.CanonicalConcurrencyToken,
            command.IdempotencyKey,
            command.RecipientActorId,
            cancellationToken));
    }
}

public sealed class GetReviewCaseDelegationRecipientsHandler
{
    private readonly IReviewCaseDelegationDirectory _delegationDirectory;

    public GetReviewCaseDelegationRecipientsHandler(IReviewCaseDelegationDirectory delegationDirectory)
    {
        ArgumentNullException.ThrowIfNull(delegationDirectory);

        _delegationDirectory = delegationDirectory;
    }

    public Task<Result<ReviewCaseDelegationRecipientsResponse>> Handle(
        GetReviewCaseDelegationRecipientsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (_delegationDirectory.ReadCurrentOrganization() is not ReviewCaseDelegationDirectoryRead.Available available)
        {
            return Task.FromResult(Result<ReviewCaseDelegationRecipientsResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseDelegationUnavailable));
        }

        return Task.FromResult(Result<ReviewCaseDelegationRecipientsResponse>.Success(
            new ReviewCaseDelegationRecipientsResponse(available.ActorIds)));
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
    private readonly IReviewCaseDelegationDirectory _delegationDirectory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ReviewCaseAssignmentCommand(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseAssignmentLookup assignmentLookup,
        IReviewCaseAssignmentWriter assignmentWriter,
        IReviewCaseAssignmentConflict assignmentConflict,
        IReviewCaseDelegationDirectory delegationDirectory,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorAttribution);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(assignmentLookup);
        ArgumentNullException.ThrowIfNull(assignmentWriter);
        ArgumentNullException.ThrowIfNull(assignmentConflict);
        ArgumentNullException.ThrowIfNull(delegationDirectory);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorAttribution = currentActorAttribution;
        _reviewCaseRepository = reviewCaseRepository;
        _assignmentLookup = assignmentLookup;
        _assignmentWriter = assignmentWriter;
        _assignmentConflict = assignmentConflict;
        _delegationDirectory = delegationDirectory;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    internal static async Task<Result<ChangeReviewCaseAssignmentResponse>> MapAssignmentAsync(
        Task<Result<ReviewCaseAssignmentExecution>> execution)
    {
        Result<ReviewCaseAssignmentExecution> result = await execution;
        if (result.IsFailure)
        {
            return Result<ChangeReviewCaseAssignmentResponse>.Failure(result.Error);
        }

        return Result<ChangeReviewCaseAssignmentResponse>.Success(
            new ChangeReviewCaseAssignmentResponse(ToResponse(result.Value.Entry), result.Value.Created));
    }

    internal static async Task<Result<ChangeReviewCaseDelegationResponse>> MapDelegationAsync(
        Task<Result<ReviewCaseAssignmentExecution>> execution)
    {
        Result<ReviewCaseAssignmentExecution> result = await execution;
        if (result.IsFailure)
        {
            return Result<ChangeReviewCaseDelegationResponse>.Failure(result.Error);
        }

        ReviewCaseAssignmentEntry entry = result.Value.Entry;
        return Result<ChangeReviewCaseDelegationResponse>.Success(
            new ChangeReviewCaseDelegationResponse(
                new ReviewCaseDelegationResponse(
                    entry.ReviewCaseId.Value,
                    "delegate",
                    ActorSourceName(entry.ActorSource),
                    entry.ActorId,
                    entry.RecipientActorId!,
                    entry.RecordedAt),
                result.Value.Created));
    }

    public async Task<Result<ReviewCaseAssignmentExecution>> ExecuteAsync(
        ReviewCaseAssignmentOperation operation,
        ReviewCaseId reviewCaseId,
        byte[] rowVersion,
        string canonicalConcurrencyToken,
        Guid idempotencyKey,
        string? recipientActorId,
        CancellationToken cancellationToken)
    {
        if (reviewCaseId is null
            || idempotencyKey == Guid.Empty
            || !IsCanonical(rowVersion, canonicalConcurrencyToken)
            || !IsRecipientShape(operation, recipientActorId))
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
        }

        if (!_currentActorAttribution.TryGet(out ReviewCaseActorSource actorSource, out string actorId))
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ReviewCaseAssignmentEntry? stored = await _assignmentLookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (stored is not null)
        {
            return Classify(
                stored,
                reviewCaseId,
                operation,
                actorSource,
                actorId,
                canonicalConcurrencyToken,
                recipientActorId);
        }

        ReviewCase? reviewCase = await _reviewCaseRepository.GetByIdAsync(
            organizationId,
            reviewCaseId,
            cancellationToken);
        if (reviewCase is null)
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(
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
                recipientActorId,
                cancellationToken);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (operation == ReviewCaseAssignmentOperation.Delegate)
        {
            ReviewCaseAssignmentRejection? gate = DelegationGate(
                reviewCase,
                organizationId,
                actorSource,
                actorId,
                recipientActorId!);
            if (gate is not null)
            {
                return Result<ReviewCaseAssignmentExecution>.Failure(ErrorFor(gate.Value));
            }
        }

        ReviewCaseAssignmentRejection? rejection = operation switch
        {
            ReviewCaseAssignmentOperation.Claim => reviewCase.Claim(actorSource, actorId, now),
            ReviewCaseAssignmentOperation.Release => reviewCase.Release(actorSource, actorId),
            ReviewCaseAssignmentOperation.Delegate => reviewCase.Delegate(
                actorSource,
                actorId,
                recipientActorId!,
                now),
            _ => throw new InvalidOperationException("Assignment operation is not defined."),
        };
        if (rejection is not null)
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(ErrorFor(rejection.Value));
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
            operation == ReviewCaseAssignmentOperation.Release ? null : now,
            operation == ReviewCaseAssignmentOperation.Delegate ? recipientActorId : null);
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
                recipientActorId,
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
                recipientActorId,
                cancellationToken);
        }

        return Result<ReviewCaseAssignmentExecution>.Success(
            new ReviewCaseAssignmentExecution(entry, Created: true));
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

    private async Task<Result<ReviewCaseAssignmentExecution>> RecoverAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string canonicalConcurrencyToken,
        string? recipientActorId,
        CancellationToken cancellationToken)
    {
        ReviewCaseAssignmentEntry? winner = await _assignmentLookup.FindByIdempotencyKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (winner is null)
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict);
        }

        return Classify(
            winner,
            reviewCaseId,
            operation,
            actorSource,
            actorId,
            canonicalConcurrencyToken,
            recipientActorId);
    }

    private static Result<ReviewCaseAssignmentExecution> Classify(
        ReviewCaseAssignmentEntry stored,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string canonicalConcurrencyToken,
        string? recipientActorId)
    {
        if (!stored.Matches(
            reviewCaseId,
            operation,
            actorSource,
            actorId,
            canonicalConcurrencyToken,
            recipientActorId))
        {
            return Result<ReviewCaseAssignmentExecution>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict);
        }

        return Result<ReviewCaseAssignmentExecution>.Success(
            new ReviewCaseAssignmentExecution(stored, Created: false));
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
        return operation switch
        {
            ReviewCaseAssignmentOperation.Claim => "claim",
            ReviewCaseAssignmentOperation.Release => "release",
            ReviewCaseAssignmentOperation.Delegate => "delegate",
            _ => throw new InvalidOperationException("Assignment operation is not defined."),
        };
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
            ReviewCaseAssignmentRejection.DelegateeUnavailable =>
                ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable,
            _ => throw new InvalidOperationException("Assignment rejection is not defined."),
        };
    }

    private ReviewCaseAssignmentRejection? DelegationGate(
        ReviewCase reviewCase,
        OrganizationId organizationId,
        ReviewCaseActorSource actorSource,
        string actorId,
        string recipientActorId)
    {
        if (reviewCase.Status != ReviewCaseStatus.Open)
        {
            return ReviewCaseAssignmentRejection.Terminal;
        }

        if (reviewCase.AssignedActorSource is null)
        {
            return ReviewCaseAssignmentRejection.NotAssigned;
        }

        if (reviewCase.AssignedActorSource != actorSource
            || !string.Equals(reviewCase.AssignedActorId, actorId, StringComparison.Ordinal))
        {
            return ReviewCaseAssignmentRejection.NotAssignee;
        }

        if (string.Equals(actorId, recipientActorId, StringComparison.Ordinal)
            || !_delegationDirectory.Allows(organizationId, recipientActorId))
        {
            return ReviewCaseAssignmentRejection.DelegateeUnavailable;
        }

        return null;
    }

    private static bool IsRecipientShape(ReviewCaseAssignmentOperation operation, string? recipientActorId)
    {
        if (operation != ReviewCaseAssignmentOperation.Delegate)
        {
            return recipientActorId is null;
        }

        return recipientActorId is not null
            && recipientActorId.Length > 0
            && recipientActorId.Length <= ReviewCaseAssignmentEntry.MaxActorIdLength
            && recipientActorId == recipientActorId.Trim();
    }
}