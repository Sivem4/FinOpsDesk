using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.Modules.Reconciliation.Application.Matching;

public sealed class ExactMatchCandidateSet
{
    public ExactMatchCandidateSet(
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions,
        bool invoiceLimitExceeded,
        bool bankTransactionLimitExceeded,
        bool inconsistentMoney)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bankTransactions);

        Invoices = Array.AsReadOnly(Copy(invoices));
        BankTransactions = Array.AsReadOnly(Copy(bankTransactions));
        InvoiceLimitExceeded = invoiceLimitExceeded;
        BankTransactionLimitExceeded = bankTransactionLimitExceeded;
        InconsistentMoney = inconsistentMoney;
    }

    public IReadOnlyList<MatchInvoiceSnapshot> Invoices { get; }

    public IReadOnlyList<MatchBankTransactionSnapshot> BankTransactions { get; }

    public bool InvoiceLimitExceeded { get; }

    public bool BankTransactionLimitExceeded { get; }

    public bool InconsistentMoney { get; }

    private static T[] Copy<T>(IReadOnlyList<T> values)
    {
        T[] copy = new T[values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            T value = values[index];
            ArgumentNullException.ThrowIfNull(value);
            copy[index] = value;
        }

        return copy;
    }
}