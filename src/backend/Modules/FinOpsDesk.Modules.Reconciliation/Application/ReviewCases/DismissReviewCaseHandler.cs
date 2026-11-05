using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class DismissReviewCaseHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorContext _currentActorContext;
    private readonly IReviewCaseRepository _reviewCaseRepository;
    private readonly IReviewCaseSnapshotLookup _snapshotLookup;
    private readonly IReviewCaseAuditWriter _auditWriter;
    private readonly IReviewCaseAuditConflict _auditConflict;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public DismissReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorContext currentActorContext,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseSnapshotLookup snapshotLookup,
        IReviewCaseAuditWriter auditWriter,
        IReviewCaseAuditConflict auditConflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorContext);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(snapshotLookup);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(auditConflict);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorContext = currentActorContext;
        _reviewCaseRepository = reviewCaseRepository;
        _snapshotLookup = snapshotLookup;
        _auditWriter = auditWriter;
        _auditConflict = auditConflict;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ReviewCaseResponse>> Handle(
        DismissReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!ReviewCaseConcurrencyToken.TryDecode(command.ConcurrencyToken, out byte[] expectedVersion))
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken);
        }

        string? resolutionNote;
        try
        {
            resolutionNote = ReviewCase.NormalizeNote(command.ResolutionNote);
        }
        catch (ArgumentException)
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        if (!_currentActorContext.TryGetActorId(out string actorId))
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ReviewCase? reviewCase = await _reviewCaseRepository.GetByIdAsync(
            organizationId,
            command.ReviewCaseId,
            cancellationToken);
        if (reviewCase is null)
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        if (reviewCase.Status != ReviewCaseStatus.Open)
        {
            Result<ReviewCaseDecision> terminal = reviewCase.Dismiss(
                resolutionNote,
                _timeProvider.GetUtcNow());
            if (terminal.IsFailure)
            {
                return Result<ReviewCaseResponse>.Failure(terminal.Error);
            }

            return Result<ReviewCaseResponse>.Success(
                ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)));
        }

        if (!ReviewCaseResponses.SameRowVersion(
            _reviewCaseRepository.GetRowVersion(reviewCase),
            expectedVersion))
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict);
        }

        Result<ReviewCaseDecision> decision = reviewCase.Dismiss(
            resolutionNote,
            _timeProvider.GetUtcNow());
        if (decision.IsFailure)
        {
            return Result<ReviewCaseResponse>.Failure(decision.Error);
        }

        _reviewCaseRepository.ExpectRowVersion(reviewCase, expectedVersion);
        _auditWriter.AppendDismissed(reviewCase, actorId);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return await RecoverAsync(organizationId, command, resolutionNote, cancellationToken);
        }
        catch (UniqueConstraintConflictException exception)
            when (_auditConflict.IsAuditUniquenessConflict(exception))
        {
            return await RecoverAsync(organizationId, command, resolutionNote, cancellationToken);
        }

        return Result<ReviewCaseResponse>.Success(
            ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)));
    }

    private async Task<Result<ReviewCaseResponse>> RecoverAsync(
        OrganizationId organizationId,
        DismissReviewCaseCommand command,
        string? resolutionNote,
        CancellationToken cancellationToken)
    {
        ReviewCaseSnapshot? winner = await _snapshotLookup.FindByIdAsync(
            organizationId,
            command.ReviewCaseId,
            cancellationToken);
        if (winner is not null && ReviewCaseResponses.IsIdenticalDismissal(winner, resolutionNote))
        {
            return Result<ReviewCaseResponse>.Success(ReviewCaseResponses.From(winner));
        }

        if (winner is not null && winner.Status != ReviewCaseStatus.Open)
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationErrors.ReviewCaseTerminalConflict);
        }

        return Result<ReviewCaseResponse>.Failure(
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict);
    }
}