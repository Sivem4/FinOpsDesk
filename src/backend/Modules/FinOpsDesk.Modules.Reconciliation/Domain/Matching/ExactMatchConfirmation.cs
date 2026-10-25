using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class ExactMatchConfirmation
{
    public static Result<Money> TryDeriveAmount(
        string invoiceNumber,
        string? bankReference,
        Money outstanding,
        Money unapplied)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentNullException.ThrowIfNull(outstanding);
        ArgumentNullException.ThrowIfNull(unapplied);

        string? normalizedInvoiceReference = PaymentReferenceNormalizer.Normalize(invoiceNumber);
        string? normalizedBankReference = PaymentReferenceNormalizer.Normalize(bankReference);

        if (normalizedInvoiceReference is null
            || normalizedBankReference is null
            || !string.Equals(
                normalizedInvoiceReference,
                normalizedBankReference,
                StringComparison.Ordinal))
        {
            return Result<Money>.Failure(ReconciliationErrors.MatchReferenceMismatch);
        }

        if (!outstanding.HasSameCurrency(unapplied))
        {
            return Result<Money>.Failure(ReconciliationErrors.MatchCurrencyMismatch);
        }

        if (outstanding.Amount <= 0
            || unapplied.Amount <= 0
            || outstanding != unapplied)
        {
            return Result<Money>.Failure(ReconciliationErrors.MatchStale);
        }

        return Result<Money>.Success(outstanding);
    }
}