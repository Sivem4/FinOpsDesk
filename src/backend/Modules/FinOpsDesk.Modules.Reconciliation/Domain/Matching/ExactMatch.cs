using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record ExactMatch
{
    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money Amount { get; }

    public string NormalizedReference { get; }

    public MatchReasonCode Reason { get; }

    public ExactMatch(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        string normalizedReference,
        MatchReasonCode reason)
    {
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedReference);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Exact match amount must be greater than zero.");
        }

        if (reason != MatchReasonCode.ExactReferenceCurrencyAndOpenAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "An exact match must use the exact reference, currency, and open amount reason.");
        }

        PaymentReferenceNormalizer.EnsureCanonical(normalizedReference, nameof(normalizedReference));

        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Amount = amount;
        NormalizedReference = normalizedReference;
        Reason = reason;
    }
}