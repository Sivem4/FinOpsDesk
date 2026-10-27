using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class OpenReviewBlocks
{
    public static OpenReviewBlockSet Collect(IReadOnlyList<ReviewBlockSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        HashSet<InvoiceId> invoiceIds = [];
        HashSet<BankTransactionId> bankTransactionIds = [];

        foreach (ReviewBlockSubject subject in subjects)
        {
            if (!Enum.IsDefined(subject.Status))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subjects),
                    "Review block status is not a defined review case status.");
            }

            if (!Enum.IsDefined(subject.Reason))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subjects),
                    "Review block reason is not a defined review case reason.");
            }

            if (subject.Status != ReviewCaseStatus.Open)
            {
                continue;
            }

            if (subject.Reason == ReviewCaseReason.CompetingMatch)
            {
                if (subject.InvoiceId is null || subject.BankTransactionId is null)
                {
                    throw new ArgumentException(
                        "An open competing-match review must name an invoice and a bank transaction.",
                        nameof(subjects));
                }

                invoiceIds.Add(subject.InvoiceId);
                bankTransactionIds.Add(subject.BankTransactionId);
                continue;
            }

            if (subject.Reason != ReviewCaseReason.NeedsInvestigation)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subjects),
                    "Review block reason is not a defined review case reason.");
            }

            bool namesInvoice = subject.InvoiceId is not null;
            bool namesBankTransaction = subject.BankTransactionId is not null;

            if (namesInvoice == namesBankTransaction)
            {
                throw new ArgumentException(
                    "An open needs-investigation review must name exactly one financial record.",
                    nameof(subjects));
            }

            if (subject.InvoiceId is not null)
            {
                invoiceIds.Add(subject.InvoiceId);
                continue;
            }

            if (subject.BankTransactionId is null)
            {
                throw new ArgumentException(
                    "An open needs-investigation review must name exactly one financial record.",
                    nameof(subjects));
            }

            bankTransactionIds.Add(subject.BankTransactionId);
        }

        return new OpenReviewBlockSet(invoiceIds, bankTransactionIds);
    }
}