using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class OpenReviewCaseHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorContext _currentActorContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IReviewCaseRepository _reviewCaseRepository;
    private readonly IReviewCaseSnapshotLookup _snapshotLookup;
    private readonly IReviewCaseOpenConflict _openConflict;
    private readonly IReviewCaseOpenAttemptLookup _attemptLookup;
    private readonly IReviewCaseOpenAttemptWriter _attemptWriter;
    private readonly IReviewCaseOpenAttemptConflict _attemptConflict;
    private readonly IReviewCaseAuditWriter _auditWriter;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public OpenReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorContext currentActorContext,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseSnapshotLookup snapshotLookup,
        IReviewCaseOpenConflict openConflict,
        IReviewCaseOpenAttemptLookup attemptLookup,
        IReviewCaseOpenAttemptWriter attemptWriter,
        IReviewCaseOpenAttemptConflict attemptConflict,
        IReviewCaseAuditWriter auditWriter,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorContext);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(snapshotLookup);
        ArgumentNullException.ThrowIfNull(openConflict);
        ArgumentNullException.ThrowIfNull(attemptLookup);
        ArgumentNullException.ThrowIfNull(attemptWriter);
        ArgumentNullException.ThrowIfNull(attemptConflict);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorContext = currentActorContext;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _reviewCaseRepository = reviewCaseRepository;
        _snapshotLookup = snapshotLookup;
        _openConflict = openConflict;
        _attemptLookup = attemptLookup;
        _attemptWriter = attemptWriter;
        _attemptConflict = attemptConflict;
        _auditWriter = auditWriter;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<OpenReviewCaseResponse>> Handle(
        OpenReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IdempotencyKey == Guid.Empty
            || !TryNormalize(command, out NormalizedOpenRequest request))
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        if (!_currentActorContext.TryGetActorId(out string actorId))
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        if (command.IdempotencyKey is Guid idempotencyKey)
        {
            return await HandleKeyedAsync(
                organizationId,
                actorId,
                idempotencyKey,
                request,
                cancellationToken);
        }

        return await HandleKeylessAsync(organizationId, actorId, request, cancellationToken);
    }

    private async Task<Result<OpenReviewCaseResponse>> HandleKeylessAsync(
        OrganizationId organizationId,
        string actorId,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        Result<OpenReviewCaseResponse>? missingDocument = await MissingDocumentAsync(
            organizationId,
            request,
            cancellationToken);
        if (missingDocument is not null)
        {
            return missingDocument;
        }

        ReviewCase? prototype = TryOpen(organizationId, request);
        if (prototype is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        ReviewCase reviewCase = prototype;
        ReviewCaseSnapshot? existing = await _snapshotLookup.FindOpenBySubjectAsync(
            organizationId,
            reviewCase.SubjectKey,
            cancellationToken);
        if (existing is not null)
        {
            return Replay(existing);
        }

        _reviewCaseRepository.Add(reviewCase);
        _auditWriter.AppendOpened(reviewCase, actorId);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_openConflict.IsOpenSubjectConflict(exception))
        {
            ReviewCaseSnapshot? winner = await _snapshotLookup.FindOpenBySubjectAsync(
                organizationId,
                reviewCase.SubjectKey,
                cancellationToken);
            if (winner is null)
            {
                return Result<OpenReviewCaseResponse>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseSubjectConflict);
            }

            return Replay(winner);
        }

        return Created(reviewCase);
    }

    private async Task<Result<OpenReviewCaseResponse>> HandleKeyedAsync(
        OrganizationId organizationId,
        string actorId,
        Guid idempotencyKey,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        Result<OpenReviewCaseResponse>? advisory = await ReadCommittedAttemptAsync(
            organizationId,
            idempotencyKey,
            request,
            cancellationToken);
        if (advisory is not null)
        {
            return advisory;
        }

        Result<OpenReviewCaseResponse>? missingDocument = await MissingDocumentOrAttemptAsync(
            organizationId,
            idempotencyKey,
            request,
            cancellationToken);
        if (missingDocument is not null)
        {
            return missingDocument;
        }

        ReviewCase? prototype = TryOpen(organizationId, request);
        if (prototype is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        ReviewCase reviewCase = prototype;
        ReviewCaseSnapshot? open = await _snapshotLookup.FindOpenBySubjectAsync(
            organizationId,
            reviewCase.SubjectKey,
            cancellationToken);
        Result<OpenReviewCaseResponse>? raced = await ReadCommittedAttemptAsync(
            organizationId,
            idempotencyKey,
            request,
            cancellationToken);
        if (raced is not null)
        {
            return raced;
        }

        if (open is not null)
        {
            if (!request.Matches(open))
            {
                return Result<OpenReviewCaseResponse>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseOpenRequestConflict);
            }

            _attemptWriter.Add(AttemptFor(organizationId, idempotencyKey, open.ReviewCaseId, request));
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintConflictException exception) when (_attemptConflict.IsPrimaryKeyConflict(exception))
            {
                return await ClassifyStoredAttemptAsync(
                    organizationId,
                    idempotencyKey,
                    request,
                    cancellationToken);
            }

            return Replay(open);
        }

        _reviewCaseRepository.Add(reviewCase);
        _auditWriter.AppendOpened(reviewCase, actorId);
        _attemptWriter.Add(AttemptFor(organizationId, idempotencyKey, reviewCase.Id, request));
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_attemptConflict.IsPrimaryKeyConflict(exception))
        {
            return await ClassifyStoredAttemptAsync(
                organizationId,
                idempotencyKey,
                request,
                cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_openConflict.IsOpenSubjectConflict(exception))
        {
            return await RecoverOpenSubjectAsync(
                organizationId,
                idempotencyKey,
                reviewCase.SubjectKey,
                request,
                cancellationToken);
        }

        return Created(reviewCase);
    }

    private async Task<Result<OpenReviewCaseResponse>> RecoverOpenSubjectAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        string subjectKey,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        Result<OpenReviewCaseResponse>? stored = await ReadCommittedAttemptAsync(
            organizationId,
            idempotencyKey,
            request,
            cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        ReviewCaseSnapshot? winner = await _snapshotLookup.FindOpenBySubjectAsync(
            organizationId,
            subjectKey,
            cancellationToken);
        stored = await ReadCommittedAttemptAsync(
            organizationId,
            idempotencyKey,
            request,
            cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        if (winner is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseSubjectConflict);
        }

        if (!request.Matches(winner))
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseOpenRequestConflict);
        }

        try
        {
            await _attemptWriter.InsertAsync(
                AttemptFor(organizationId, idempotencyKey, winner.ReviewCaseId, request),
                cancellationToken);
        }
        catch (UniqueConstraintConflictException exception) when (_attemptConflict.IsPrimaryKeyConflict(exception))
        {
            return await ClassifyStoredAttemptAsync(
                organizationId,
                idempotencyKey,
                request,
                cancellationToken);
        }

        return Replay(winner);
    }

    private async Task<Result<OpenReviewCaseResponse>?> MissingDocumentOrAttemptAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InvoiceId is not null
            && await _invoiceRepository.GetByIdAsync(
                organizationId,
                request.InvoiceId,
                cancellationToken) is null)
        {
            Result<OpenReviewCaseResponse>? stored = await ReadCommittedAttemptAsync(
                organizationId,
                idempotencyKey,
                request,
                cancellationToken);
            return stored ?? Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        if (request.BankTransactionId is not null
            && await _bankTransactionRepository.GetByIdAsync(
                organizationId,
                request.BankTransactionId,
                cancellationToken) is null)
        {
            Result<OpenReviewCaseResponse>? stored = await ReadCommittedAttemptAsync(
                organizationId,
                idempotencyKey,
                request,
                cancellationToken);
            return stored ?? Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        return null;
    }

    private async Task<Result<OpenReviewCaseResponse>?> MissingDocumentAsync(
        OrganizationId organizationId,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InvoiceId is not null
            && await _invoiceRepository.GetByIdAsync(
                organizationId,
                request.InvoiceId,
                cancellationToken) is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        if (request.BankTransactionId is not null
            && await _bankTransactionRepository.GetByIdAsync(
                organizationId,
                request.BankTransactionId,
                cancellationToken) is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        return null;
    }

    private async Task<Result<OpenReviewCaseResponse>?> ReadCommittedAttemptAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        ReviewCaseOpenAttempt? stored = await _attemptLookup.FindAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return await ClassifyAsync(organizationId, stored, request, cancellationToken);
    }

    private async Task<Result<OpenReviewCaseResponse>> ClassifyStoredAttemptAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        ReviewCaseOpenAttempt? stored = await _attemptLookup.FindAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        if (stored is null)
        {
            // The primary key rejected the insert, but the winning row is not visible.
            // This is not a rowversion mismatch and it is not retried on the failed context.
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict);
        }

        return await ClassifyAsync(organizationId, stored, request, cancellationToken);
    }

    private async Task<Result<OpenReviewCaseResponse>> ClassifyAsync(
        OrganizationId organizationId,
        ReviewCaseOpenAttempt stored,
        NormalizedOpenRequest request,
        CancellationToken cancellationToken)
    {
        if (!stored.Matches(
                request.Reason,
                request.InvoiceId,
                request.BankTransactionId,
                request.SourceLabel,
                request.Note))
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseIdempotencyPayloadConflict);
        }

        ReviewCaseSnapshot? snapshot = await _snapshotLookup.FindByIdAsync(
            organizationId,
            stored.ReviewCaseId,
            cancellationToken);
        if (snapshot is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseSubjectConflict);
        }

        return Replay(snapshot);
    }

    private Result<OpenReviewCaseResponse> Created(ReviewCase reviewCase)
    {
        return Result<OpenReviewCaseResponse>.Success(
            new OpenReviewCaseResponse(
                ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)),
                Created: true));
    }

    private static Result<OpenReviewCaseResponse> Replay(ReviewCaseSnapshot snapshot)
    {
        return Result<OpenReviewCaseResponse>.Success(
            new OpenReviewCaseResponse(ReviewCaseResponses.From(snapshot), Created: false));
    }

    private static ReviewCaseOpenAttempt AttemptFor(
        OrganizationId organizationId,
        Guid idempotencyKey,
        ReviewCaseId reviewCaseId,
        NormalizedOpenRequest request)
    {
        return ReviewCaseOpenAttempt.Create(
            organizationId,
            idempotencyKey,
            reviewCaseId,
            request.Reason,
            request.InvoiceId,
            request.BankTransactionId,
            request.SourceLabel,
            request.Note);
    }

    private ReviewCase? TryOpen(OrganizationId organizationId, NormalizedOpenRequest request)
    {
        try
        {
            return ReviewCase.Open(
                ReviewCaseId.New(),
                organizationId,
                request.Reason,
                request.InvoiceId,
                request.BankTransactionId,
                request.SourceLabel,
                request.Note,
                _timeProvider.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryNormalize(OpenReviewCaseCommand command, out NormalizedOpenRequest request)
    {
        request = default;
        if (!Enum.IsDefined(command.Reason))
        {
            return false;
        }

        bool hasInvoice = command.InvoiceId is not null;
        bool hasBankTransaction = command.BankTransactionId is not null;
        if (command.Reason == ReviewCaseReason.CompetingMatch)
        {
            if (!hasInvoice || !hasBankTransaction)
            {
                return false;
            }
        }
        else if (hasInvoice == hasBankTransaction)
        {
            return false;
        }

        ReviewCaseSource? sourceLabel = null;
        if (command.SourceLabel is not null)
        {
            try
            {
                sourceLabel = ReviewCaseSource.Parse(command.SourceLabel);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        string? note;
        try
        {
            note = ReviewCase.NormalizeNote(command.Note);
        }
        catch (ArgumentException)
        {
            return false;
        }

        request = new NormalizedOpenRequest(
            command.Reason,
            command.InvoiceId,
            command.BankTransactionId,
            sourceLabel,
            note);
        return true;
    }

    private readonly record struct NormalizedOpenRequest(
        ReviewCaseReason Reason,
        InvoiceId? InvoiceId,
        BankTransactionId? BankTransactionId,
        ReviewCaseSource? SourceLabel,
        string? Note)
    {
        public bool Matches(ReviewCaseSnapshot snapshot)
        {
            return snapshot.Reason == Reason
                && snapshot.InvoiceId == InvoiceId
                && snapshot.BankTransactionId == BankTransactionId
                && snapshot.SourceLabel == SourceLabel
                && string.Equals(snapshot.Note, Note, StringComparison.Ordinal);
        }
    }
}