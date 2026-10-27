namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed class BatchExecutionResult
{
    public const int Version = 1;

    private BatchExecutionResult(
        int requestedCount,
        int confirmedCount,
        int rejectedCount,
        int interruptedCount,
        bool allConfirmed,
        bool hasRejections,
        bool partial,
        IReadOnlyList<BatchExecutionItem> items)
    {
        RequestedCount = requestedCount;
        ConfirmedCount = confirmedCount;
        RejectedCount = rejectedCount;
        InterruptedCount = interruptedCount;
        AllConfirmed = allConfirmed;
        HasRejections = hasRejections;
        Partial = partial;
        Items = items;
    }

    public int ResultVersion => Version;

    public int RequestedCount { get; }

    public int ConfirmedCount { get; }

    public int RejectedCount { get; }

    public int InterruptedCount { get; }

    public bool AllConfirmed { get; }

    public bool HasRejections { get; }

    public bool Partial { get; }

    public IReadOnlyList<BatchExecutionItem> Items { get; }

    public static BatchExecutionResult FromItems(IReadOnlyList<BatchExecutionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Counts counts = Count(items);

        return new BatchExecutionResult(
            counts.RequestedCount,
            counts.ConfirmedCount,
            counts.RejectedCount,
            counts.InterruptedCount,
            counts.AllConfirmed,
            counts.HasRejections,
            counts.Partial,
            Copy(items));
    }

    public static BatchExecutionResult FromDocument(
        int resultVersion,
        int requestedCount,
        int confirmedCount,
        int rejectedCount,
        int interruptedCount,
        bool allConfirmed,
        bool hasRejections,
        bool partial,
        IReadOnlyList<BatchExecutionItem> items)
    {
        if (resultVersion != Version)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultVersion),
                "Batch execution result version is not supported.");
        }

        ArgumentNullException.ThrowIfNull(items);
        Counts counts = Count(items);

        if (requestedCount != counts.RequestedCount
            || confirmedCount != counts.ConfirmedCount
            || rejectedCount != counts.RejectedCount
            || interruptedCount != counts.InterruptedCount
            || allConfirmed != counts.AllConfirmed
            || hasRejections != counts.HasRejections
            || partial != counts.Partial)
        {
            throw new ArgumentException(
                "Batch execution result counts do not match the items.");
        }

        return new BatchExecutionResult(
            counts.RequestedCount,
            counts.ConfirmedCount,
            counts.RejectedCount,
            counts.InterruptedCount,
            counts.AllConfirmed,
            counts.HasRejections,
            counts.Partial,
            Copy(items));
    }

    private static BatchExecutionItem[] Copy(IReadOnlyList<BatchExecutionItem> items)
    {
        BatchExecutionItem[] copy = new BatchExecutionItem[items.Count];

        for (int index = 0; index < items.Count; index++)
        {
            BatchExecutionItem item = items[index];
            ArgumentNullException.ThrowIfNull(item);
            copy[index] = item;
        }

        return copy;
    }

    private static Counts Count(IReadOnlyList<BatchExecutionItem> items)
    {
        if (items.Count < BatchExactMatchIdentity.MinimumPairCount
            || items.Count > BatchExactMatchIdentity.MaximumPairCount)
        {
            throw new ArgumentException(
                "A batch execution result must contain 1 to 25 items.",
                nameof(items));
        }

        int confirmedCount = 0;
        int rejectedCount = 0;
        int interruptedCount = 0;
        HashSet<BatchExactMatchPair> seen = [];

        for (int index = 0; index < items.Count; index++)
        {
            BatchExecutionItem item = items[index];
            ArgumentNullException.ThrowIfNull(item);
            BatchExactMatchPair pair = new(item.InvoiceId, item.BankTransactionId);

            if (!seen.Add(pair))
            {
                throw new ArgumentException(
                    "A batch execution result cannot contain a duplicate pair.",
                    nameof(items));
            }

            if (index > 0)
            {
                BatchExecutionItem previous = items[index - 1];
                BatchExactMatchPair previousPair = new(previous.InvoiceId, previous.BankTransactionId);

                if (BatchExactMatchIdentity.ComparePairs(previousPair, pair) >= 0)
                {
                    throw new ArgumentException(
                        "Batch execution items must be in canonical pair order.",
                        nameof(items));
                }
            }

            switch (item.Outcome)
            {
                case BatchExecutionItemOutcome.Confirmed:
                    confirmedCount++;
                    break;
                case BatchExecutionItemOutcome.Interrupted:
                    interruptedCount++;
                    break;
                case BatchExecutionItemOutcome.SkippedAmbiguous:
                case BatchExecutionItemOutcome.SkippedCoverage:
                case BatchExecutionItemOutcome.SkippedReview:
                case BatchExecutionItemOutcome.Stale:
                case BatchExecutionItemOutcome.Conflict:
                case BatchExecutionItemOutcome.NotFound:
                    rejectedCount++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(items),
                        "Batch execution item outcome is not defined.");
            }
        }

        bool hasRejections = rejectedCount > 0 || interruptedCount > 0;
        bool allConfirmed = confirmedCount == items.Count && !hasRejections;
        bool partial = confirmedCount > 0 && hasRejections;

        return new Counts(
            items.Count,
            confirmedCount,
            rejectedCount,
            interruptedCount,
            allConfirmed,
            hasRejections,
            partial);
    }

    private readonly record struct Counts(
        int RequestedCount,
        int ConfirmedCount,
        int RejectedCount,
        int InterruptedCount,
        bool AllConfirmed,
        bool HasRejections,
        bool Partial);
}