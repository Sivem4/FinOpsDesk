using System.Security.Cryptography;
using System.Text;

using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed class BatchExactMatchIdentity
{
    public const int MinimumPairCount = 1;

    public const int MaximumPairCount = 25;

    public const string Version = "batch-exact-match-pairs/v1";

    private BatchExactMatchIdentity(
        IReadOnlyList<BatchExactMatchPair> pairs,
        BatchExecutionFingerprint fingerprint)
    {
        Pairs = pairs;
        Fingerprint = fingerprint;
    }

    public IReadOnlyList<BatchExactMatchPair> Pairs { get; }

    public BatchExecutionFingerprint Fingerprint { get; }

    public static BatchExactMatchIdentity Create(IReadOnlyList<BatchExactMatchPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        if (pairs.Count < MinimumPairCount || pairs.Count > MaximumPairCount)
        {
            throw new ArgumentException(
                "A batch exact-match request must contain 1 to 25 pairs.",
                nameof(pairs));
        }

        BatchExactMatchPair[] ordered = new BatchExactMatchPair[pairs.Count];

        for (int index = 0; index < pairs.Count; index++)
        {
            BatchExactMatchPair pair = pairs[index];
            EnsurePair(pair);
            ordered[index] = pair;
        }

        Array.Sort(ordered, ComparePairs);

        for (int index = 1; index < ordered.Length; index++)
        {
            if (ComparePairs(ordered[index - 1], ordered[index]) == 0)
            {
                throw new ArgumentException(
                    "A batch exact-match request cannot contain a duplicate pair.",
                    nameof(pairs));
            }
        }

        string canonical = WriteCanonical(ordered);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return new BatchExactMatchIdentity(
            Array.AsReadOnly(ordered),
            new BatchExecutionFingerprint(hash));
    }

    public static int ComparePairs(BatchExactMatchPair left, BatchExactMatchPair right)
    {
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

    private static void EnsurePair(BatchExactMatchPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair.InvoiceId);
        ArgumentNullException.ThrowIfNull(pair.BankTransactionId);

        if (pair.InvoiceId.Value == Guid.Empty || pair.BankTransactionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Batch exact-match pair identifiers cannot be empty.");
        }
    }

    private static string WriteCanonical(BatchExactMatchPair[] ordered)
    {
        StringBuilder builder = new();
        builder.Append(Version);
        builder.Append('\n');

        foreach (BatchExactMatchPair pair in ordered)
        {
            builder.Append(pair.InvoiceId.Value.ToString("D"));
            builder.Append('|');
            builder.Append(pair.BankTransactionId.Value.ToString("D"));
            builder.Append('\n');
        }

        return builder.ToString();
    }
}