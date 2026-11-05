using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseOpenAttempt
{
    public OrganizationId OrganizationId { get; }

    public Guid IdempotencyKey { get; }

    public ReviewCaseId ReviewCaseId { get; }

    public ReviewCaseReason Reason { get; }

    public InvoiceId? InvoiceId { get; }

    public BankTransactionId? BankTransactionId { get; }

    public ReviewCaseSource? SourceLabel { get; }

    public string? Note { get; }

#pragma warning disable CS8618
    private ReviewCaseOpenAttempt()
    {
    }
#pragma warning restore CS8618

    private ReviewCaseOpenAttempt(
        OrganizationId organizationId,
        Guid idempotencyKey,
        ReviewCaseId reviewCaseId,
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        ReviewCaseSource? sourceLabel,
        string? note)
    {
        OrganizationId = organizationId;
        IdempotencyKey = idempotencyKey;
        ReviewCaseId = reviewCaseId;
        Reason = reason;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        SourceLabel = sourceLabel;
        Note = note;
    }

    public static ReviewCaseOpenAttempt Create(
        OrganizationId organizationId,
        Guid idempotencyKey,
        ReviewCaseId reviewCaseId,
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        ReviewCaseSource? sourceLabel,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Review reason is not defined.");
        }

        ValidateReasonShape(reason, invoiceId, bankTransactionId);
        return new ReviewCaseOpenAttempt(
            organizationId,
            idempotencyKey,
            reviewCaseId,
            reason,
            invoiceId,
            bankTransactionId,
            sourceLabel,
            ReviewCase.NormalizeNote(note));
    }

    public bool Matches(
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        ReviewCaseSource? sourceLabel,
        string? note)
    {
        return Reason == reason
            && InvoiceId == invoiceId
            && BankTransactionId == bankTransactionId
            && SourceLabel == sourceLabel
            && string.Equals(Note, note, StringComparison.Ordinal);
    }

    private static void ValidateReasonShape(
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId)
    {
        bool hasInvoice = invoiceId is not null;
        bool hasBankTransaction = bankTransactionId is not null;
        if (reason == ReviewCaseReason.CompetingMatch)
        {
            if (!hasInvoice || !hasBankTransaction)
            {
                throw new ArgumentException(
                    "A competing match requires an invoice id and a bank transaction id.");
            }

            return;
        }

        if (hasInvoice == hasBankTransaction)
        {
            throw new ArgumentException(
                "An investigation requires exactly one financial record.");
        }
    }
}