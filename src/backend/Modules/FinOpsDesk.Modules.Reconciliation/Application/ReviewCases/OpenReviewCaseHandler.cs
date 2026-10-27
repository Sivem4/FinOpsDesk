using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class OpenReviewCaseHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IReviewCaseRepository _reviewCaseRepository;
    private readonly IReviewCaseSnapshotLookup _snapshotLookup;
    private readonly IReviewCaseOpenConflict _openConflict;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public OpenReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IReviewCaseRepository reviewCaseRepository,
        IReviewCaseSnapshotLookup snapshotLookup,
        IReviewCaseOpenConflict openConflict,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(reviewCaseRepository);
        ArgumentNullException.ThrowIfNull(snapshotLookup);
        ArgumentNullException.ThrowIfNull(openConflict);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _reviewCaseRepository = reviewCaseRepository;
        _snapshotLookup = snapshotLookup;
        _openConflict = openConflict;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<OpenReviewCaseResponse>> Handle(
        OpenReviewCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!TryNormalize(command, out ReviewCaseSource? sourceLabel, out string? note))
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        if (command.InvoiceId is not null
            && await _invoiceRepository.GetByIdAsync(
                organizationId,
                command.InvoiceId,
                cancellationToken) is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        if (command.BankTransactionId is not null
            && await _bankTransactionRepository.GetByIdAsync(
                organizationId,
                command.BankTransactionId,
                cancellationToken) is null)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        ReviewCase reviewCase;
        try
        {
            reviewCase = ReviewCase.Open(
                ReviewCaseId.New(),
                organizationId,
                command.Reason,
                command.InvoiceId,
                command.BankTransactionId,
                sourceLabel,
                note,
                _timeProvider.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return Result<OpenReviewCaseResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        ReviewCaseSnapshot? existing = await _snapshotLookup.FindOpenBySubjectAsync(
            organizationId,
            reviewCase.SubjectKey,
            cancellationToken);
        if (existing is not null)
        {
            return Result<OpenReviewCaseResponse>.Success(
                new OpenReviewCaseResponse(ReviewCaseResponses.From(existing), Created: false));
        }

        _reviewCaseRepository.Add(reviewCase);
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

            return Result<OpenReviewCaseResponse>.Success(
                new OpenReviewCaseResponse(ReviewCaseResponses.From(winner), Created: false));
        }

        return Result<OpenReviewCaseResponse>.Success(
            new OpenReviewCaseResponse(
                ReviewCaseResponses.From(reviewCase, _reviewCaseRepository.GetRowVersion(reviewCase)),
                Created: true));
    }

    private static bool TryNormalize(
        OpenReviewCaseCommand command,
        out ReviewCaseSource? sourceLabel,
        out string? note)
    {
        sourceLabel = null;
        note = null;
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

        try
        {
            note = ReviewCase.NormalizeNote(command.Note);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }
}