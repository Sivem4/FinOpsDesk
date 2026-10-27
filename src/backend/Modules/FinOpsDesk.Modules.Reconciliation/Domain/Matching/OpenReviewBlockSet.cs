using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed class OpenReviewBlockSet
{
    public IReadOnlySet<InvoiceId> InvoiceIds { get; }

    public IReadOnlySet<BankTransactionId> BankTransactionIds { get; }

    public OpenReviewBlockSet(
        IEnumerable<InvoiceId> invoiceIds,
        IEnumerable<BankTransactionId> bankTransactionIds)
    {
        ArgumentNullException.ThrowIfNull(invoiceIds);
        ArgumentNullException.ThrowIfNull(bankTransactionIds);

        HashSet<InvoiceId> invoices = [];

        foreach (InvoiceId invoiceId in invoiceIds)
        {
            ArgumentNullException.ThrowIfNull(invoiceId);
            invoices.Add(invoiceId);
        }

        HashSet<BankTransactionId> bankTransactions = [];

        foreach (BankTransactionId bankTransactionId in bankTransactionIds)
        {
            ArgumentNullException.ThrowIfNull(bankTransactionId);
            bankTransactions.Add(bankTransactionId);
        }

        InvoiceIds = invoices;
        BankTransactionIds = bankTransactions;
    }
}