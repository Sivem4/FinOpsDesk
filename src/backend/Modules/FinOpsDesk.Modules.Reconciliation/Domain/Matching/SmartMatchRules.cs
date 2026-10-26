using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class SmartMatchRules
{
    public const string AlgorithmVersion = "smart-1";

    public const int ReferenceExactPoints = 70;

    public const int QualifiedReferencePoints = 60;

    public const int AmountExactPoints = 25;

    public const int MaxRowsPerBlockingKey = 32;

    public const int MaxCandidatesPerInvoice = 16;

    public const int MaxSuggestions = 200;

    public const int ContestMargin = 10;

    public static SmartMatchBatchResult Evaluate(
        OrganizationId organizationId,
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bankTransactions);

        PreparedInvoice[] preparedInvoices = PrepareInvoices(organizationId, invoices);
        PreparedTransaction[] preparedTransactions = PrepareTransactions(
            organizationId,
            bankTransactions);

        bool coverageLimited = HasOversizedOpenReference(preparedInvoices, preparedTransactions);
        HashSet<ExactAmountKey> ambiguousExactKeys = FindAmbiguousExactKeys(
            preparedInvoices,
            preparedTransactions);
        Dictionary<BlockingKey, BlockingBucket> buckets = BuildBuckets(
            preparedInvoices,
            preparedTransactions);

        foreach (BlockingBucket bucket in buckets.Values)
        {
            if (bucket.Saturated)
            {
                coverageLimited = true;
            }
        }

        HashSet<int>[] candidates = GatherCandidates(preparedInvoices.Length, buckets);
        List<WorkingSuggestion> kept = new();
        Dictionary<int, DroppedScoreSet> droppedInvoiceScores = new();
        Dictionary<int, DroppedScoreSet> droppedTransactionScores = new();

        for (int invoiceIndex = 0; invoiceIndex < preparedInvoices.Length; invoiceIndex++)
        {
            HashSet<int> transactionIndexes = candidates[invoiceIndex];
            if (transactionIndexes.Count == 0)
            {
                continue;
            }

            PreparedInvoice invoice = preparedInvoices[invoiceIndex];
            List<WorkingSuggestion> scored = new();

            foreach (int transactionIndex in transactionIndexes)
            {
                WorkingSuggestion? suggestion = TryScore(
                    invoice,
                    preparedTransactions[transactionIndex],
                    invoiceIndex,
                    transactionIndex,
                    ambiguousExactKeys);

                if (suggestion is not null)
                {
                    scored.Add(suggestion);
                }
            }

            if (scored.Count == 0)
            {
                continue;
            }

            scored.Sort(CompareForInvoice);
            int keep = Math.Min(MaxCandidatesPerInvoice, scored.Count);

            if (scored.Count > MaxCandidatesPerInvoice)
            {
                coverageLimited = true;
            }

            for (int index = 0; index < keep; index++)
            {
                kept.Add(scored[index]);
            }

            for (int index = keep; index < scored.Count; index++)
            {
                WorkingSuggestion dropped = scored[index];
                Remember(droppedInvoiceScores, invoiceIndex, dropped.Score);
                Remember(droppedTransactionScores, dropped.TransactionIndex, dropped.Score);
            }
        }

        MarkKnownContests(kept);
        ApplyDroppedContests(kept, droppedInvoiceScores, droppedTransactionScores);
        kept.Sort(CompareForOutput);

        bool truncated = kept.Count > MaxSuggestions;
        if (truncated)
        {
            kept.RemoveRange(MaxSuggestions, kept.Count - MaxSuggestions);
        }

        SmartMatchSuggestion[] suggestions = new SmartMatchSuggestion[kept.Count];

        for (int index = 0; index < kept.Count; index++)
        {
            suggestions[index] = kept[index].ToSuggestion(index + 1);
        }

        return new SmartMatchBatchResult(
            AlgorithmVersion,
            truncated,
            coverageLimited,
            suggestions);
    }

    private static PreparedInvoice[] PrepareInvoices(
        OrganizationId organizationId,
        IReadOnlyList<MatchInvoiceSnapshot> invoices)
    {
        HashSet<InvoiceId> seen = new();
        PreparedInvoice[] prepared = new PreparedInvoice[invoices.Count];

        for (int index = 0; index < invoices.Count; index++)
        {
            MatchInvoiceSnapshot invoice = invoices[index];
            ArgumentNullException.ThrowIfNull(invoice);

            if (invoice.OrganizationId != organizationId)
            {
                throw new ArgumentException(
                    "Every invoice snapshot must belong to the organization supplied for smart matching.",
                    nameof(invoices));
            }

            if (!seen.Add(invoice.InvoiceId))
            {
                throw new ArgumentException(
                    "Each invoice snapshot must have a distinct invoice id.",
                    nameof(invoices));
            }

            prepared[index] = new PreparedInvoice(
                invoice,
                SmartReferenceNormalizer.Analyze(invoice.InvoiceNumber));
        }

        return prepared;
    }

    private static PreparedTransaction[] PrepareTransactions(
        OrganizationId organizationId,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        HashSet<BankTransactionId> seen = new();
        PreparedTransaction[] prepared = new PreparedTransaction[bankTransactions.Count];

        for (int index = 0; index < bankTransactions.Count; index++)
        {
            MatchBankTransactionSnapshot bankTransaction = bankTransactions[index];
            ArgumentNullException.ThrowIfNull(bankTransaction);

            if (bankTransaction.OrganizationId != organizationId)
            {
                throw new ArgumentException(
                    "Every bank transaction snapshot must belong to the organization supplied for smart matching.",
                    nameof(bankTransactions));
            }

            if (!seen.Add(bankTransaction.BankTransactionId))
            {
                throw new ArgumentException(
                    "Each bank transaction snapshot must have a distinct bank transaction id.",
                    nameof(bankTransactions));
            }

            prepared[index] = new PreparedTransaction(
                bankTransaction,
                SmartReferenceNormalizer.Analyze(bankTransaction.Reference));
        }

        return prepared;
    }

    private static bool HasOversizedOpenReference(
        PreparedInvoice[] invoices,
        PreparedTransaction[] bankTransactions)
    {
        foreach (PreparedInvoice invoice in invoices)
        {
            if (invoice.Snapshot.OutstandingAmount.Amount > 0 && invoice.Reference.IsOversized)
            {
                return true;
            }
        }

        foreach (PreparedTransaction bankTransaction in bankTransactions)
        {
            if (bankTransaction.Snapshot.UnappliedAmount.Amount > 0
                && bankTransaction.Reference.IsOversized)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<ExactAmountKey> FindAmbiguousExactKeys(
        PreparedInvoice[] invoices,
        PreparedTransaction[] bankTransactions)
    {
        Dictionary<ExactAmountKey, SideCount> counts = new();

        foreach (PreparedInvoice invoice in invoices)
        {
            if (!TryGetExactKey(
                    invoice.Reference,
                    invoice.Snapshot.OutstandingAmount,
                    out ExactAmountKey key))
            {
                continue;
            }

            AddSide(counts, key, invoice: true);
        }

        foreach (PreparedTransaction bankTransaction in bankTransactions)
        {
            if (!TryGetExactKey(
                    bankTransaction.Reference,
                    bankTransaction.Snapshot.UnappliedAmount,
                    out ExactAmountKey key))
            {
                continue;
            }

            AddSide(counts, key, invoice: false);
        }

        HashSet<ExactAmountKey> ambiguous = new();

        foreach (KeyValuePair<ExactAmountKey, SideCount> entry in counts)
        {
            SideCount count = entry.Value;
            if (count.Invoices > 0
                && count.Transactions > 0
                && (count.Invoices > 1 || count.Transactions > 1))
            {
                ambiguous.Add(entry.Key);
            }
        }

        return ambiguous;
    }

    private static bool TryGetExactKey(
        SmartReferenceAnalysis reference,
        Money openAmount,
        out ExactAmountKey key)
    {
        if (openAmount.Amount <= 0 || reference.ExactNormalized is null)
        {
            key = default;
            return false;
        }

        key = new ExactAmountKey(
            reference.ExactNormalized,
            openAmount.Currency.Value,
            openAmount.Amount);
        return true;
    }

    private static void AddSide(
        Dictionary<ExactAmountKey, SideCount> counts,
        ExactAmountKey key,
        bool invoice)
    {
        if (!counts.TryGetValue(key, out SideCount current))
        {
            current = default;
        }

        counts[key] = invoice
            ? current with { Invoices = current.Invoices + 1 }
            : current with { Transactions = current.Transactions + 1 };
    }

    private static Dictionary<BlockingKey, BlockingBucket> BuildBuckets(
        PreparedInvoice[] invoices,
        PreparedTransaction[] bankTransactions)
    {
        Dictionary<BlockingKey, BlockingBucket> buckets = new();

        for (int index = 0; index < invoices.Length; index++)
        {
            Money outstanding = invoices[index].Snapshot.OutstandingAmount;
            if (outstanding.Amount <= 0)
            {
                continue;
            }

            foreach (BlockingKey key in BlockingKeys(invoices[index].Reference, outstanding.Currency.Value))
            {
                GetBucket(buckets, key).AddInvoice(index);
            }
        }

        for (int index = 0; index < bankTransactions.Length; index++)
        {
            Money unapplied = bankTransactions[index].Snapshot.UnappliedAmount;
            if (unapplied.Amount <= 0)
            {
                continue;
            }

            foreach (BlockingKey key in BlockingKeys(
                bankTransactions[index].Reference,
                unapplied.Currency.Value))
            {
                GetBucket(buckets, key).AddTransaction(index);
            }
        }

        return buckets;
    }

    private static BlockingBucket GetBucket(
        Dictionary<BlockingKey, BlockingBucket> buckets,
        BlockingKey key)
    {
        if (!buckets.TryGetValue(key, out BlockingBucket? bucket))
        {
            bucket = new BlockingBucket();
            buckets.Add(key, bucket);
        }

        return bucket;
    }

    private static List<BlockingKey> BlockingKeys(SmartReferenceAnalysis reference, string currency)
    {
        List<BlockingKey> keys = new();

        if (reference.IsOversized)
        {
            return keys;
        }

        if (reference.ExactNormalized is not null)
        {
            keys.Add(new BlockingKey(BlockingSignalKind.Exact, currency, reference.ExactNormalized));
        }

        if (reference.TokenSetKey is not null)
        {
            keys.Add(new BlockingKey(BlockingSignalKind.TokenSet, currency, reference.TokenSetKey));
        }

        if (reference.HasLongDigitRun && reference.CompactForm is not null)
        {
            keys.Add(new BlockingKey(BlockingSignalKind.Compact, currency, reference.CompactForm));
        }

        foreach (string run in reference.LongDigitRuns)
        {
            keys.Add(new BlockingKey(BlockingSignalKind.Numeric, currency, run));
        }

        return keys;
    }

    private static HashSet<int>[] GatherCandidates(
        int invoiceCount,
        Dictionary<BlockingKey, BlockingBucket> buckets)
    {
        HashSet<int>[] candidates = new HashSet<int>[invoiceCount];

        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = new HashSet<int>();
        }

        foreach (BlockingBucket bucket in buckets.Values)
        {
            if (bucket.Saturated || bucket.Invoices.Count == 0 || bucket.Transactions.Count == 0)
            {
                continue;
            }

            foreach (int invoiceIndex in bucket.Invoices)
            {
                HashSet<int> invoiceCandidates = candidates[invoiceIndex];

                foreach (int transactionIndex in bucket.Transactions)
                {
                    invoiceCandidates.Add(transactionIndex);
                }
            }
        }

        return candidates;
    }

    private static WorkingSuggestion? TryScore(
        PreparedInvoice invoice,
        PreparedTransaction bankTransaction,
        int invoiceIndex,
        int transactionIndex,
        HashSet<ExactAmountKey> ambiguousExactKeys)
    {
        Money outstanding = invoice.Snapshot.OutstandingAmount;
        Money unapplied = bankTransaction.Snapshot.UnappliedAmount;

        if (outstanding.Amount <= 0
            || unapplied.Amount <= 0
            || !outstanding.HasSameCurrency(unapplied))
        {
            return null;
        }

        SmartReferenceAnalysis left = invoice.Reference;
        SmartReferenceAnalysis right = bankTransaction.Reference;
        List<SmartMatchSignal> signals = new();
        int referencePoints = 0;

        bool exact = ReferencesAreExact(left, right);
        bool tokenSet = TokenSetsMatch(left, right);
        bool yearsMatch = YearsMatch(left, right);
        bool compact = yearsMatch && CompactFormsMatch(left, right);
        bool numeric = yearsMatch && NumericIdentifiersMatch(left, right);

        if (exact)
        {
            signals.Add(new SmartMatchSignal(
                SmartMatchSignalCode.ReferenceExact,
                ReferenceExactPoints));
            referencePoints = ReferenceExactPoints;
            AddSupporting(signals, tokenSet, compact, numeric);
        }
        else if (tokenSet)
        {
            signals.Add(new SmartMatchSignal(
                SmartMatchSignalCode.ReferenceTokenSetEqual,
                QualifiedReferencePoints));
            referencePoints = QualifiedReferencePoints;
            AddSupporting(signals, tokenSet: false, compact, numeric);
        }
        else if (compact)
        {
            signals.Add(new SmartMatchSignal(
                SmartMatchSignalCode.CompactFormEqual,
                QualifiedReferencePoints));
            referencePoints = QualifiedReferencePoints;
            AddSupporting(signals, tokenSet: false, compact: false, numeric);
        }
        else if (numeric)
        {
            signals.Add(new SmartMatchSignal(
                SmartMatchSignalCode.NumericIdentifierEqual,
                QualifiedReferencePoints));
            referencePoints = QualifiedReferencePoints;
        }
        else
        {
            return null;
        }

        bool amountExact = outstanding.Amount == unapplied.Amount;
        int amountPoints = amountExact ? AmountExactPoints : 0;
        signals.Add(new SmartMatchSignal(
            amountExact ? SmartMatchSignalCode.AmountExact : SmartMatchSignalCode.AmountDiffers,
            amountPoints));

        decimal suggested = outstanding.Amount <= unapplied.Amount
            ? outstanding.Amount
            : unapplied.Amount;
        bool exactAmbiguous = SharesAmbiguousExactKey(
            left,
            right,
            outstanding,
            unapplied,
            ambiguousExactKeys);

        return new WorkingSuggestion(
            invoice.Snapshot.InvoiceId,
            bankTransaction.Snapshot.BankTransactionId,
            invoiceIndex,
            transactionIndex,
            referencePoints + amountPoints,
            outstanding,
            unapplied,
            new Money(suggested, outstanding.Currency),
            exactAmbiguous,
            signals);
    }

    private static void AddSupporting(
        List<SmartMatchSignal> signals,
        bool tokenSet,
        bool compact,
        bool numeric)
    {
        if (tokenSet)
        {
            signals.Add(new SmartMatchSignal(SmartMatchSignalCode.ReferenceTokenSetEqual, 0));
        }

        if (compact)
        {
            signals.Add(new SmartMatchSignal(SmartMatchSignalCode.CompactFormEqual, 0));
        }

        if (numeric)
        {
            signals.Add(new SmartMatchSignal(SmartMatchSignalCode.NumericIdentifierEqual, 0));
        }
    }

    private static bool ReferencesAreExact(SmartReferenceAnalysis left, SmartReferenceAnalysis right)
    {
        return !left.IsOversized
            && !right.IsOversized
            && left.ExactNormalized is not null
            && string.Equals(left.ExactNormalized, right.ExactNormalized, StringComparison.Ordinal);
    }

    private static bool TokenSetsMatch(SmartReferenceAnalysis left, SmartReferenceAnalysis right)
    {
        return left.TokenSetKey is not null
            && string.Equals(left.TokenSetKey, right.TokenSetKey, StringComparison.Ordinal);
    }

    private static bool CompactFormsMatch(SmartReferenceAnalysis left, SmartReferenceAnalysis right)
    {
        return left.HasLongDigitRun
            && right.HasLongDigitRun
            && left.CompactForm is not null
            && string.Equals(left.CompactForm, right.CompactForm, StringComparison.Ordinal);
    }

    private static bool YearsMatch(SmartReferenceAnalysis left, SmartReferenceAnalysis right)
    {
        if (left.YearTokens.Count != right.YearTokens.Count)
        {
            return false;
        }

        for (int index = 0; index < left.YearTokens.Count; index++)
        {
            if (!string.Equals(left.YearTokens[index], right.YearTokens[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NumericIdentifiersMatch(
        SmartReferenceAnalysis left,
        SmartReferenceAnalysis right)
    {
        if (left.LongDigitRuns.Count == 0 || right.LongDigitRuns.Count == 0)
        {
            return false;
        }

        HashSet<string> runs = new(left.LongDigitRuns, StringComparer.Ordinal);

        foreach (string run in right.LongDigitRuns)
        {
            if (runs.Contains(run))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesAmbiguousExactKey(
        SmartReferenceAnalysis left,
        SmartReferenceAnalysis right,
        Money outstanding,
        Money unapplied,
        HashSet<ExactAmountKey> ambiguousExactKeys)
    {
        if (outstanding.Amount != unapplied.Amount
            || left.ExactNormalized is null
            || !string.Equals(left.ExactNormalized, right.ExactNormalized, StringComparison.Ordinal))
        {
            return false;
        }

        ExactAmountKey key = new(
            left.ExactNormalized,
            outstanding.Currency.Value,
            outstanding.Amount);
        return ambiguousExactKeys.Contains(key);
    }

    private static void MarkKnownContests(List<WorkingSuggestion> suggestions)
    {
        MarkGroupContests(suggestions, static suggestion => suggestion.InvoiceIndex);
        MarkGroupContests(suggestions, static suggestion => suggestion.TransactionIndex);
    }

    private static void MarkGroupContests(
        List<WorkingSuggestion> suggestions,
        Func<WorkingSuggestion, int> groupKey)
    {
        Dictionary<int, List<WorkingSuggestion>> groups = new();

        foreach (WorkingSuggestion suggestion in suggestions)
        {
            int key = groupKey(suggestion);
            if (!groups.TryGetValue(key, out List<WorkingSuggestion>? group))
            {
                group = new List<WorkingSuggestion>();
                groups.Add(key, group);
            }

            group.Add(suggestion);
        }

        foreach (List<WorkingSuggestion> group in groups.Values)
        {
            for (int left = 0; left < group.Count; left++)
            {
                for (int right = left + 1; right < group.Count; right++)
                {
                    if (Math.Abs(group[left].Score - group[right].Score) <= ContestMargin)
                    {
                        group[left].Contested = true;
                        group[right].Contested = true;
                    }
                }
            }
        }
    }

    private static void ApplyDroppedContests(
        List<WorkingSuggestion> suggestions,
        Dictionary<int, DroppedScoreSet> droppedInvoiceScores,
        Dictionary<int, DroppedScoreSet> droppedTransactionScores)
    {
        foreach (WorkingSuggestion suggestion in suggestions)
        {
            if (Contests(droppedInvoiceScores, suggestion.InvoiceIndex, suggestion.Score)
                || Contests(droppedTransactionScores, suggestion.TransactionIndex, suggestion.Score))
            {
                suggestion.Contested = true;
            }
        }
    }

    private static bool Contests(Dictionary<int, DroppedScoreSet> droppedScores, int key, int score)
    {
        return droppedScores.TryGetValue(key, out DroppedScoreSet? dropped) && dropped.Contests(score);
    }

    private static void Remember(Dictionary<int, DroppedScoreSet> droppedScores, int key, int score)
    {
        if (!droppedScores.TryGetValue(key, out DroppedScoreSet? dropped))
        {
            dropped = new DroppedScoreSet();
            droppedScores.Add(key, dropped);
        }

        dropped.Add(score);
    }

    private static int CompareForInvoice(WorkingSuggestion left, WorkingSuggestion right)
    {
        int score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        return CompareGuid(left.BankTransactionId.Value, right.BankTransactionId.Value);
    }

    private static int CompareForOutput(WorkingSuggestion left, WorkingSuggestion right)
    {
        int score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        int invoice = CompareGuid(left.InvoiceId.Value, right.InvoiceId.Value);
        if (invoice != 0)
        {
            return invoice;
        }

        return CompareGuid(left.BankTransactionId.Value, right.BankTransactionId.Value);
    }

    private static int CompareGuid(Guid left, Guid right)
    {
        return string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
    }

    private enum BlockingSignalKind
    {
        Exact = 1,
        TokenSet = 2,
        Compact = 3,
        Numeric = 4,
    }

    private readonly record struct BlockingKey(BlockingSignalKind Kind, string Currency, string Value);

    private readonly record struct SideCount(int Invoices, int Transactions);

    private readonly record struct ExactAmountKey(string Reference, string Currency, decimal Amount);

    private readonly record struct PreparedInvoice(
        MatchInvoiceSnapshot Snapshot,
        SmartReferenceAnalysis Reference);

    private readonly record struct PreparedTransaction(
        MatchBankTransactionSnapshot Snapshot,
        SmartReferenceAnalysis Reference);

    private sealed class BlockingBucket
    {
        private readonly HashSet<int> _invoices = new();

        private readonly HashSet<int> _transactions = new();

        public List<int> Invoices { get; } = new();

        public List<int> Transactions { get; } = new();

        public bool Saturated { get; private set; }

        public void AddInvoice(int index)
        {
            Add(_invoices, Invoices, index);
        }

        public void AddTransaction(int index)
        {
            Add(_transactions, Transactions, index);
        }

        private void Add(HashSet<int> seen, List<int> side, int index)
        {
            if (Saturated || !seen.Add(index))
            {
                return;
            }

            side.Add(index);

            if (Invoices.Count > MaxRowsPerBlockingKey || Transactions.Count > MaxRowsPerBlockingKey)
            {
                Saturated = true;
                Invoices.Clear();
                Transactions.Clear();
                _invoices.Clear();
                _transactions.Clear();
            }
        }
    }

    private sealed class DroppedScoreSet
    {
        private readonly HashSet<int> _scores = new();

        public void Add(int score)
        {
            _scores.Add(score);
        }

        public bool Contests(int score)
        {
            foreach (int dropped in _scores)
            {
                if (Math.Abs(score - dropped) <= ContestMargin)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class WorkingSuggestion
    {
        public WorkingSuggestion(
            InvoiceId invoiceId,
            BankTransactionId bankTransactionId,
            int invoiceIndex,
            int transactionIndex,
            int score,
            Money invoiceOutstanding,
            Money bankTransactionUnapplied,
            Money suggestedAmount,
            bool exactMatchAmbiguous,
            List<SmartMatchSignal> signals)
        {
            InvoiceId = invoiceId;
            BankTransactionId = bankTransactionId;
            InvoiceIndex = invoiceIndex;
            TransactionIndex = transactionIndex;
            Score = score;
            InvoiceOutstanding = invoiceOutstanding;
            BankTransactionUnapplied = bankTransactionUnapplied;
            SuggestedAmount = suggestedAmount;
            ExactMatchAmbiguous = exactMatchAmbiguous;
            Signals = signals;
        }

        public InvoiceId InvoiceId { get; }

        public BankTransactionId BankTransactionId { get; }

        public int InvoiceIndex { get; }

        public int TransactionIndex { get; }

        public int Score { get; }

        public Money InvoiceOutstanding { get; }

        public Money BankTransactionUnapplied { get; }

        public Money SuggestedAmount { get; }

        public bool ExactMatchAmbiguous { get; }

        public bool Contested { get; set; }

        public List<SmartMatchSignal> Signals { get; }

        public SmartMatchSuggestion ToSuggestion(int rank)
        {
            if (Contested)
            {
                Signals.Add(new SmartMatchSignal(SmartMatchSignalCode.ContestedCandidates, 0));
            }

            if (ExactMatchAmbiguous)
            {
                Signals.Add(new SmartMatchSignal(SmartMatchSignalCode.ExactMatchAmbiguous, 0));
            }

            return new SmartMatchSuggestion(
                rank,
                Score,
                InvoiceId,
                BankTransactionId,
                InvoiceOutstanding,
                BankTransactionUnapplied,
                SuggestedAmount,
                Contested,
                ExactMatchAmbiguous,
                Signals);
        }
    }
}