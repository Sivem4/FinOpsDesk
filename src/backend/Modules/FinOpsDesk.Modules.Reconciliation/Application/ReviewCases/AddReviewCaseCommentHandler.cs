using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class AddReviewCaseCommentHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorContext _currentActorContext;
    private readonly IReviewCaseCommentLookup _commentLookup;
    private readonly IReviewCaseCommentWriter _commentWriter;
    private readonly IReviewCaseCommentConflict _commentConflict;
    private readonly IReviewCaseCommentQueries _commentQueries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public AddReviewCaseCommentHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorContext currentActorContext,
        IReviewCaseCommentLookup commentLookup,
        IReviewCaseCommentWriter commentWriter,
        IReviewCaseCommentConflict commentConflict,
        IReviewCaseCommentQueries commentQueries,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorContext);
        ArgumentNullException.ThrowIfNull(commentLookup);
        ArgumentNullException.ThrowIfNull(commentWriter);
        ArgumentNullException.ThrowIfNull(commentConflict);
        ArgumentNullException.ThrowIfNull(commentQueries);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorContext = currentActorContext;
        _commentLookup = commentLookup;
        _commentWriter = commentWriter;
        _commentConflict = commentConflict;
        _commentQueries = commentQueries;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AddReviewCaseCommentResponse>> Handle(
        AddReviewCaseCommentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IdempotencyKey == Guid.Empty
            || !TryNormalize(command.Text, out string text))
        {
            return Result<AddReviewCaseCommentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
        }

        if (!_currentActorContext.TryGetActorId(out string actorId))
        {
            return Result<AddReviewCaseCommentResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ReviewCaseComment? stored = await _commentLookup.FindByIdempotencyKeyAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);
        if (stored is not null)
        {
            return Classify(stored, command.ReviewCaseId, text);
        }

        if (!await _commentQueries.ExistsAsync(organizationId, command.ReviewCaseId, cancellationToken))
        {
            return Result<AddReviewCaseCommentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        ReviewCaseComment comment;
        try
        {
            comment = ReviewCaseComment.Create(
                ReviewCaseCommentId.New(),
                organizationId,
                command.ReviewCaseId,
                text,
                actorId,
                _timeProvider.GetUtcNow(),
                command.IdempotencyKey);
        }
        catch (ArgumentException)
        {
            return Result<AddReviewCaseCommentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
        }

        _commentWriter.Add(comment);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_commentConflict.IsIdempotencyKeyConflict(exception))
        {
            ReviewCaseComment? winner = await _commentLookup.FindByIdempotencyKeyAsync(
                organizationId,
                command.IdempotencyKey,
                cancellationToken);
            if (winner is null)
            {
                return Result<AddReviewCaseCommentResponse>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseCommentConcurrencyConflict);
            }

            return Classify(winner, command.ReviewCaseId, text);
        }

        return Created(comment);
    }

    private static Result<AddReviewCaseCommentResponse> Classify(
        ReviewCaseComment stored,
        ReviewCaseId reviewCaseId,
        string text)
    {
        if (!stored.Matches(reviewCaseId, text))
        {
            return Result<AddReviewCaseCommentResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict);
        }

        return Result<AddReviewCaseCommentResponse>.Success(
            new AddReviewCaseCommentResponse(ToResponse(stored), Created: false));
    }

    private static Result<AddReviewCaseCommentResponse> Created(ReviewCaseComment comment)
    {
        return Result<AddReviewCaseCommentResponse>.Success(
            new AddReviewCaseCommentResponse(ToResponse(comment), Created: true));
    }

    private static ReviewCaseCommentResponse ToResponse(ReviewCaseComment comment)
    {
        return new ReviewCaseCommentResponse(
            comment.Id.Value,
            comment.Text,
            comment.ActorId,
            comment.RecordedAt);
    }

    private static bool TryNormalize(string? text, out string normalized)
    {
        try
        {
            normalized = ReviewCaseComment.NormalizeText(text);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}