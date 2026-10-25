using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record ClassifiedMatchRecord
{
    public MatchClassification Classification { get; }

    public MatchReasonCode Reason { get; }

    public InvoiceId? InvoiceId { get; }

    public BankTransactionId? BankTransactionId { get; }

    public string? NormalizedReference { get; }

    public Money OpenAmount { get; }

    public ClassifiedMatchRecord(
        MatchClassification classification,
        MatchReasonCode reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        string? normalizedReference,
        Money openAmount)
    {
        ArgumentNullException.ThrowIfNull(openAmount);

        if (classification is not (MatchClassification.NoMatch or MatchClassification.Ineligible))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classification),
                "A classified record is either unmatched or ineligible.");
        }

        bool identifiesInvoice = invoiceId is not null;
        bool identifiesBankTransaction = bankTransactionId is not null;

        if (identifiesInvoice == identifiesBankTransaction)
        {
            throw new ArgumentException(
                "A classified match record must identify exactly one invoice or bank transaction.");
        }

        if (classification == MatchClassification.NoMatch &&
            reason != MatchReasonCode.NoCounterpart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "An unmatched record has no counterpart for its match key.");
        }

        if (classification == MatchClassification.Ineligible &&
            reason is not (
                MatchReasonCode.MissingReference or MatchReasonCode.NonPositiveOpenAmount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "An ineligible record is missing a reference or has no positive open amount.");
        }

        if (classification == MatchClassification.NoMatch)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(normalizedReference);
        }

        PaymentReferenceNormalizer.EnsureCanonical(
            normalizedReference,
            nameof(normalizedReference));

        Classification = classification;
        Reason = reason;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        NormalizedReference = normalizedReference;
        OpenAmount = openAmount;
    }
}