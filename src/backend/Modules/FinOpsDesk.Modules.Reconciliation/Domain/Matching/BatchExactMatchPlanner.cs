using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class BatchExactMatchPlanner
{
    public static BatchExactMatchPlan Plan(
        ExactMatchBatchResult classification,
        IReadOnlySet<InvoiceId> blockedInvoiceIds,
        IReadOnlySet<BankTransactionId> blockedBankTransactionIds,
        BatchMatchCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(blockedInvoiceIds);
        ArgumentNullException.ThrowIfNull(blockedBankTransactionIds);

        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coverage),
                "Batch match coverage is not a defined value.");
        }

        if (!string.Equals(
                classification.AlgorithmVersion,
                ExactMatchRules.AlgorithmVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Batch planning requires an exact-1 classification.",
                nameof(classification));
        }

        // Census figures from a partial book are not a complete result. Return an
        // empty plan rather than a subset that could be read as the eligible set.
        if (coverage == BatchMatchCoverage.Incomplete)
        {
            return new BatchExactMatchPlan(
                ExactMatchRules.AlgorithmVersion,
                inputComplete: false,
                [],
                excludedByReviewCount: 0,
                ambiguousGroupCount: 0,
                unmatchedCount: 0,
                ineligibleCount: 0);
        }

        // Blocked ids apply only to pairs exact-1 already classified. This method
        // does not call Evaluate and does not turn an ambiguous group into an exact pair.
        List<ExactMatch> eligible = new(classification.ExactMatches.Count);
        int excludedByReviewCount = 0;

        foreach (ExactMatch match in classification.ExactMatches)
        {
            ArgumentNullException.ThrowIfNull(match);

            if (blockedInvoiceIds.Contains(match.InvoiceId)
                || blockedBankTransactionIds.Contains(match.BankTransactionId))
            {
                excludedByReviewCount++;
                continue;
            }

            eligible.Add(match);
        }

        eligible.Sort(ComparePairs);

        return new BatchExactMatchPlan(
            ExactMatchRules.AlgorithmVersion,
            inputComplete: true,
            eligible,
            excludedByReviewCount,
            classification.AmbiguousGroups.Count,
            classification.Unmatched.Count,
            classification.Ineligible.Count);
    }

    private static int ComparePairs(ExactMatch left, ExactMatch right)
    {
        int referenceComparison = string.CompareOrdinal(
            left.NormalizedReference,
            right.NormalizedReference);

        if (referenceComparison != 0)
        {
            return referenceComparison;
        }

        int currencyComparison = string.CompareOrdinal(
            left.Amount.Currency.Value,
            right.Amount.Currency.Value);

        if (currencyComparison != 0)
        {
            return currencyComparison;
        }

        int amountComparison = left.Amount.Amount.CompareTo(right.Amount.Amount);

        if (amountComparison != 0)
        {
            return amountComparison;
        }

        int invoiceComparison = string.CompareOrdinal(
            left.InvoiceId.Value.ToString("D"),
            right.InvoiceId.Value.ToString("D"));

        if (invoiceComparison != 0)
        {
            return invoiceComparison;
        }

        return string.CompareOrdinal(
            left.BankTransactionId.Value.ToString("D"),
            right.BankTransactionId.Value.ToString("D"));
    }
}