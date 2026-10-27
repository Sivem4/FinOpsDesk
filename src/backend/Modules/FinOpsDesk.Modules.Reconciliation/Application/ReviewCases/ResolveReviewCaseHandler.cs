using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class ResolveReviewCaseHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseRepository _reviewCaseRepository;
    private readonly IReviewCaseSnapshotLookup _snapshotLookup;
    private readonly IAllocationLinkLookup _allocationLinkLookup;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ResolveReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseSnapshotLookup snapshotLookup,
        IAllocationLinkLookup allocationLinkLookup,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(snapshotLookup);
        ArgumentNullException.ThrowIfNull(allocationLinkLookup);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _reviewCaseRepository = reviewCaseRepository;
        _snapshotLookup = snapshotLookup;
        _allocationLinkLookup = allocationLinkLookup;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ReviewCaseResponse>> Handle(
        ResolveReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!ReviewCaseConcurrencyToken.TryDecode(command.ConcurrencyToken, out byte[] expectedVersion))
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken);
        }

        if (!TryNormalize(command, out string? resolutionNote, out Error? shapeError))
        {
            return Result<ReviewCaseResponse>.Failure(shapeError!);
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
            return ClassifyTerminal(reviewCase, command.Outcome, command.AllocationId, resolutionNote);
        }

        if (!ReviewCaseResponses.SameRowVersion(
            _reviewCaseRepository.GetRowVersion(reviewCase),
            expectedVersion))
        {
            return Result<ReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict);
        }

        if (command.Outcome == ReviewCaseOutcome.AllocationRecorded)
        {
            AllocationLink? link = await _allocationLinkLookup.FindAsync(
                organizationId,
                command.AllocationId!,
                cancellationToken);
            if (!Covers(reviewCase, link))
            {
                return Result<ReviewCaseResponse>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseAllocationMismatch);
            }
        }

        Result<ReviewCaseDecision> decision = reviewCase.Resolve(
            command.Outcome,
            command.AllocationId,
            resolutionNote,
            _timeProvider.GetUtcNow());
        if (decision.IsFailure)
        {
            return Result<ReviewCaseResponse>.Failure(decision.Error);
        }

        if (decision.Value == ReviewCaseDecision.IdenticalReplay)
        {
            return Result<ReviewCaseResponse>.Success(
                ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)));
        }

        _reviewCaseRepository.ExpectRowVersion(reviewCase, expectedVersion);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return await RecoverAsync(
                organizationId,
                command,
                resolutionNote,
                cancellationToken);
        }

        return Result<ReviewCaseResponse>.Success(
            ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)));
    }

    private async Task<Result<ReviewCaseResponse>> RecoverAsync(
        OrganizationId organizationId,
        ResolveReviewCaseCommand command,
        string? resolutionNote,
        CancellationToken cancellationToken)
    {
        ReviewCaseSnapshot? winner = await _snapshotLookup.FindByIdAsync(
            organizationId,
            command.ReviewCaseId,
            cancellationToken);
        if (winner is not null
            && ReviewCaseResponses.IsIdenticalResolution(
                winner,
                command.Outcome,
                command.AllocationId,
                resolutionNote))
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

    private Result<ReviewCaseResponse> ClassifyTerminal(
        ReviewCase reviewCase,
        ReviewCaseOutcome outcome,
        AllocationId? allocationId,
        string? resolutionNote)
    {
        Result<ReviewCaseDecision> decision = reviewCase.Resolve(
            outcome,
            allocationId,
            resolutionNote,
            _timeProvider.GetUtcNow());
        if (decision.IsFailure)
        {
            return Result<ReviewCaseResponse>.Failure(decision.Error);
        }

        return Result<ReviewCaseResponse>.Success(
            ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)));
    }

    private static bool TryNormalize(
        ResolveReviewCaseCommand command,
        out string? resolutionNote,
        out Error? error)
    {
        resolutionNote = null;
        error = null;
        if (!Enum.IsDefined(command.Outcome))
        {
            error = ReconciliationApplicationErrors.ReviewCaseInvalidRequest;
            return false;
        }

        try
        {
            resolutionNote = ReviewCase.NormalizeNote(command.ResolutionNote);
        }
        catch (ArgumentException)
        {
            error = ReconciliationApplicationErrors.ReviewCaseInvalidRequest;
            return false;
        }

        if (command.Outcome == ReviewCaseOutcome.AllocationRecorded)
        {
            if (command.AllocationId is null)
            {
                error = ReconciliationErrors.ReviewCaseAllocationRequired;
                return false;
            }
        }
        else if (command.AllocationId is not null)
        {
            error = ReconciliationErrors.ReviewCaseAllocationForbidden;
            return false;
        }

        return true;
    }

    private static bool Covers(ReviewCase reviewCase, AllocationLink? link)
    {
        if (link is null || link.Status != AllocationStatus.Confirmed)
        {
            return false;
        }

        if (reviewCase.Reason == ReviewCaseReason.CompetingMatch)
        {
            return link.InvoiceId == reviewCase.InvoiceId
                && link.BankTransactionId == reviewCase.BankTransactionId;
        }

        if (reviewCase.InvoiceId is not null)
        {
            return link.InvoiceId == reviewCase.InvoiceId;
        }

        return link.BankTransactionId == reviewCase.BankTransactionId;
    }
}