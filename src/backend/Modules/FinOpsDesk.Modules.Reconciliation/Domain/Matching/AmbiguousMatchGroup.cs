using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record AmbiguousMatchGroup
{
    public string NormalizedReference { get; }

    public Money OpenAmount { get; }

    public MatchReasonCode Reason { get; }

    public IReadOnlyList<InvoiceId> InvoiceIds { get; }

    public IReadOnlyList<BankTransactionId> BankTransactionIds { get; }

    public AmbiguousMatchGroup(
        string normalizedReference,
        Money openAmount,
        MatchReasonCode reason,
        IReadOnlyList<InvoiceId> invoiceIds,
        IReadOnlyList<BankTransactionId> bankTransactionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedReference);
        ArgumentNullException.ThrowIfNull(openAmount);
        ArgumentNullException.ThrowIfNull(invoiceIds);
        ArgumentNullException.ThrowIfNull(bankTransactionIds);

        if (openAmount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(openAmount),
                "Ambiguous match amount must be greater than zero.");
        }

        PaymentReferenceNormalizer.EnsureCanonical(normalizedReference, nameof(normalizedReference));

        InvoiceId[] sortedInvoiceIds = SortInvoiceIds(invoiceIds);
        BankTransactionId[] sortedBankTransactionIds = SortBankTransactionIds(bankTransactionIds);

        if (sortedInvoiceIds.Length == 0 || sortedBankTransactionIds.Length == 0)
        {
            throw new ArgumentException(
                "An ambiguous group must contain at least one invoice and one bank transaction.");
        }

        if (sortedInvoiceIds.Length == 1 && sortedBankTransactionIds.Length == 1)
        {
            throw new ArgumentException(
                "A single invoice and bank transaction pair is an exact match, not an ambiguous group.");
        }

        MatchReasonCode expectedReason = ExpectedReason(
            sortedInvoiceIds.Length,
            sortedBankTransactionIds.Length);

        if (reason != expectedReason)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Ambiguous match reason must describe which side has multiple records.");
        }

        NormalizedReference = normalizedReference;
        OpenAmount = openAmount;
        Reason = reason;
        InvoiceIds = Array.AsReadOnly(sortedInvoiceIds);
        BankTransactionIds = Array.AsReadOnly(sortedBankTransactionIds);
    }

    private static MatchReasonCode ExpectedReason(int invoiceCount, int bankTransactionCount)
    {
        if (invoiceCount > 1 && bankTransactionCount > 1)
        {
            return MatchReasonCode.CompetingInvoicesAndTransactions;
        }

        if (invoiceCount > 1)
        {
            return MatchReasonCode.CompetingInvoices;
        }

        return MatchReasonCode.CompetingTransactions;
    }

    private static InvoiceId[] SortInvoiceIds(IReadOnlyList<InvoiceId> invoiceIds)
    {
        InvoiceId[] sorted = new InvoiceId[invoiceIds.Count];

        for (int index = 0; index < invoiceIds.Count; index++)
        {
            InvoiceId invoiceId = invoiceIds[index];
            ArgumentNullException.ThrowIfNull(invoiceId);
            sorted[index] = invoiceId;
        }

        Array.Sort(sorted, CompareInvoiceIds);
        return sorted;
    }

    private static BankTransactionId[] SortBankTransactionIds(
        IReadOnlyList<BankTransactionId> bankTransactionIds)
    {
        BankTransactionId[] sorted = new BankTransactionId[bankTransactionIds.Count];

        for (int index = 0; index < bankTransactionIds.Count; index++)
        {
            BankTransactionId bankTransactionId = bankTransactionIds[index];
            ArgumentNullException.ThrowIfNull(bankTransactionId);
            sorted[index] = bankTransactionId;
        }

        Array.Sort(sorted, CompareBankTransactionIds);
        return sorted;
    }

    private static int CompareInvoiceIds(InvoiceId left, InvoiceId right)
    {
        return string.CompareOrdinal(left.Value.ToString("D"), right.Value.ToString("D"));
    }

    private static int CompareBankTransactionIds(
        BankTransactionId left,
        BankTransactionId right)
    {
        return string.CompareOrdinal(left.Value.ToString("D"), right.Value.ToString("D"));
    }
}