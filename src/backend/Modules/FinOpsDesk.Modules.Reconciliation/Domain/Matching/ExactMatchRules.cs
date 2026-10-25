using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class ExactMatchRules
{
    public const string AlgorithmVersion = "exact-1";

    public static ExactMatchBatchResult Evaluate(
        OrganizationId organizationId,
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bankTransactions);

        EnsureOrganization(organizationId, invoices, bankTransactions);
        EnsureDistinctInvoiceIds(invoices);
        EnsureDistinctBankTransactionIds(bankTransactions);

        Dictionary<MatchKey, List<OpenInvoice>> invoicesByKey = new();
        Dictionary<MatchKey, List<OpenTransaction>> transactionsByKey = new();
        List<ClassifiedMatchRecord> unmatched = new();
        List<ClassifiedMatchRecord> ineligible = new();

        foreach (MatchInvoiceSnapshot invoice in invoices)
        {
            string? normalizedReference = PaymentReferenceNormalizer.Normalize(invoice.InvoiceNumber);

            if (invoice.OutstandingAmount.Amount <= 0)
            {
                ineligible.Add(IneligibleInvoice(
                    invoice,
                    MatchReasonCode.NonPositiveOpenAmount,
                    normalizedReference));
                continue;
            }

            if (normalizedReference is null)
            {
                ineligible.Add(IneligibleInvoice(
                    invoice,
                    MatchReasonCode.MissingReference,
                    normalizedReference));
                continue;
            }

            AddInvoice(
                invoicesByKey,
                new MatchKey(
                    normalizedReference,
                    invoice.OutstandingAmount.Currency.Value,
                    invoice.OutstandingAmount.Amount),
                new OpenInvoice(invoice.InvoiceId, invoice.OutstandingAmount));
        }

        foreach (MatchBankTransactionSnapshot bankTransaction in bankTransactions)
        {
            string? normalizedReference = PaymentReferenceNormalizer.Normalize(
                bankTransaction.Reference);

            if (bankTransaction.UnappliedAmount.Amount <= 0)
            {
                ineligible.Add(IneligibleTransaction(
                    bankTransaction,
                    MatchReasonCode.NonPositiveOpenAmount,
                    normalizedReference));
                continue;
            }

            if (normalizedReference is null)
            {
                ineligible.Add(IneligibleTransaction(
                    bankTransaction,
                    MatchReasonCode.MissingReference,
                    normalizedReference));
                continue;
            }

            AddTransaction(
                transactionsByKey,
                new MatchKey(
                    normalizedReference,
                    bankTransaction.UnappliedAmount.Currency.Value,
                    bankTransaction.UnappliedAmount.Amount),
                new OpenTransaction(
                    bankTransaction.BankTransactionId,
                    bankTransaction.UnappliedAmount));
        }

        List<MatchKey> keys = new(invoicesByKey.Count + transactionsByKey.Count);

        foreach (MatchKey key in invoicesByKey.Keys)
        {
            keys.Add(key);
        }

        foreach (MatchKey key in transactionsByKey.Keys)
        {
            if (!invoicesByKey.ContainsKey(key))
            {
                keys.Add(key);
            }
        }

        keys.Sort();

        List<ExactMatch> exactMatches = new();
        List<AmbiguousMatchGroup> ambiguousGroups = new();

        foreach (MatchKey key in keys)
        {
            if (!invoicesByKey.TryGetValue(key, out List<OpenInvoice>? invoiceGroup))
            {
                invoiceGroup = new List<OpenInvoice>();
            }

            if (!transactionsByKey.TryGetValue(key, out List<OpenTransaction>? transactionGroup))
            {
                transactionGroup = new List<OpenTransaction>();
            }

            if (invoiceGroup.Count == 0)
            {
                AddUnmatchedTransactions(unmatched, key, transactionGroup);
                continue;
            }

            if (transactionGroup.Count == 0)
            {
                AddUnmatchedInvoices(unmatched, key, invoiceGroup);
                continue;
            }

            if (invoiceGroup.Count == 1 && transactionGroup.Count == 1)
            {
                OpenInvoice invoice = invoiceGroup[0];
                OpenTransaction bankTransaction = transactionGroup[0];
                exactMatches.Add(new ExactMatch(
                    invoice.Id,
                    bankTransaction.Id,
                    invoice.OpenAmount,
                    key.Reference,
                    MatchReasonCode.ExactReferenceCurrencyAndOpenAmount));
                continue;
            }

            ambiguousGroups.Add(new AmbiguousMatchGroup(
                key.Reference,
                invoiceGroup[0].OpenAmount,
                CompetingReason(invoiceGroup.Count, transactionGroup.Count),
                invoiceGroup.Select(invoice => invoice.Id).ToArray(),
                transactionGroup.Select(bankTransaction => bankTransaction.Id).ToArray()));
        }

        exactMatches.Sort(CompareExactMatches);
        ambiguousGroups.Sort(CompareAmbiguousGroups);
        unmatched.Sort(CompareClassifiedRecords);
        ineligible.Sort(CompareClassifiedRecords);

        return new ExactMatchBatchResult(
            AlgorithmVersion,
            exactMatches,
            ambiguousGroups,
            unmatched,
            ineligible);
    }

    private static void EnsureOrganization(
        OrganizationId organizationId,
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        foreach (MatchInvoiceSnapshot invoice in invoices)
        {
            ArgumentNullException.ThrowIfNull(invoice);

            if (invoice.OrganizationId != organizationId)
            {
                throw new ArgumentException(
                    "Every invoice snapshot must belong to the organization supplied for exact matching.",
                    nameof(invoices));
            }
        }

        foreach (MatchBankTransactionSnapshot bankTransaction in bankTransactions)
        {
            ArgumentNullException.ThrowIfNull(bankTransaction);

            if (bankTransaction.OrganizationId != organizationId)
            {
                throw new ArgumentException(
                    "Every bank transaction snapshot must belong to the organization supplied for exact matching.",
                    nameof(bankTransactions));
            }
        }
    }

    private static void EnsureDistinctInvoiceIds(IReadOnlyList<MatchInvoiceSnapshot> invoices)
    {
        HashSet<InvoiceId> seen = new();

        foreach (MatchInvoiceSnapshot invoice in invoices)
        {
            if (!seen.Add(invoice.InvoiceId))
            {
                throw new ArgumentException(
                    "Each invoice snapshot must have a distinct invoice id.",
                    nameof(invoices));
            }
        }
    }

    private static void EnsureDistinctBankTransactionIds(
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        HashSet<BankTransactionId> seen = new();

        foreach (MatchBankTransactionSnapshot bankTransaction in bankTransactions)
        {
            if (!seen.Add(bankTransaction.BankTransactionId))
            {
                throw new ArgumentException(
                    "Each bank transaction snapshot must have a distinct bank transaction id.",
                    nameof(bankTransactions));
            }
        }
    }

    private static void AddInvoice(
        Dictionary<MatchKey, List<OpenInvoice>> invoicesByKey,
        MatchKey key,
        OpenInvoice invoice)
    {
        if (!invoicesByKey.TryGetValue(key, out List<OpenInvoice>? group))
        {
            group = new List<OpenInvoice>();
            invoicesByKey.Add(key, group);
        }

        group.Add(invoice);
    }

    private static void AddTransaction(
        Dictionary<MatchKey, List<OpenTransaction>> transactionsByKey,
        MatchKey key,
        OpenTransaction bankTransaction)
    {
        if (!transactionsByKey.TryGetValue(key, out List<OpenTransaction>? group))
        {
            group = new List<OpenTransaction>();
            transactionsByKey.Add(key, group);
        }

        group.Add(bankTransaction);
    }

    private static ClassifiedMatchRecord IneligibleInvoice(
        MatchInvoiceSnapshot invoice,
        MatchReasonCode reason,
        string? normalizedReference)
    {
        return new ClassifiedMatchRecord(
            MatchClassification.Ineligible,
            reason,
            invoice.InvoiceId,
            bankTransactionId: null,
            normalizedReference,
            invoice.OutstandingAmount);
    }

    private static ClassifiedMatchRecord IneligibleTransaction(
        MatchBankTransactionSnapshot bankTransaction,
        MatchReasonCode reason,
        string? normalizedReference)
    {
        return new ClassifiedMatchRecord(
            MatchClassification.Ineligible,
            reason,
            invoiceId: null,
            bankTransaction.BankTransactionId,
            normalizedReference,
            bankTransaction.UnappliedAmount);
    }

    private static void AddUnmatchedInvoices(
        List<ClassifiedMatchRecord> unmatched,
        MatchKey key,
        List<OpenInvoice> invoices)
    {
        foreach (OpenInvoice invoice in invoices)
        {
            unmatched.Add(new ClassifiedMatchRecord(
                MatchClassification.NoMatch,
                MatchReasonCode.NoCounterpart,
                invoice.Id,
                bankTransactionId: null,
                key.Reference,
                invoice.OpenAmount));
        }
    }

    private static void AddUnmatchedTransactions(
        List<ClassifiedMatchRecord> unmatched,
        MatchKey key,
        List<OpenTransaction> bankTransactions)
    {
        foreach (OpenTransaction bankTransaction in bankTransactions)
        {
            unmatched.Add(new ClassifiedMatchRecord(
                MatchClassification.NoMatch,
                MatchReasonCode.NoCounterpart,
                invoiceId: null,
                bankTransaction.Id,
                key.Reference,
                bankTransaction.OpenAmount));
        }
    }

    private static MatchReasonCode CompetingReason(int invoiceCount, int bankTransactionCount)
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

    private static int CompareExactMatches(ExactMatch left, ExactMatch right)
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

    private static int CompareAmbiguousGroups(
        AmbiguousMatchGroup left,
        AmbiguousMatchGroup right)
    {
        int referenceComparison = string.CompareOrdinal(
            left.NormalizedReference,
            right.NormalizedReference);

        if (referenceComparison != 0)
        {
            return referenceComparison;
        }

        int currencyComparison = string.CompareOrdinal(
            left.OpenAmount.Currency.Value,
            right.OpenAmount.Currency.Value);

        if (currencyComparison != 0)
        {
            return currencyComparison;
        }

        return left.OpenAmount.Amount.CompareTo(right.OpenAmount.Amount);
    }

    private static int CompareClassifiedRecords(
        ClassifiedMatchRecord left,
        ClassifiedMatchRecord right)
    {
        int referenceComparison = string.CompareOrdinal(
            left.NormalizedReference,
            right.NormalizedReference);

        if (referenceComparison != 0)
        {
            return referenceComparison;
        }

        int currencyComparison = string.CompareOrdinal(
            left.OpenAmount.Currency.Value,
            right.OpenAmount.Currency.Value);

        if (currencyComparison != 0)
        {
            return currencyComparison;
        }

        int amountComparison = left.OpenAmount.Amount.CompareTo(right.OpenAmount.Amount);

        if (amountComparison != 0)
        {
            return amountComparison;
        }

        int sideComparison = Side(left).CompareTo(Side(right));

        if (sideComparison != 0)
        {
            return sideComparison;
        }

        return string.CompareOrdinal(Identity(left), Identity(right));
    }

    private static int Side(ClassifiedMatchRecord record)
    {
        return record.InvoiceId is null ? 1 : 0;
    }

    private static string Identity(ClassifiedMatchRecord record)
    {
        if (record.InvoiceId is not null)
        {
            return record.InvoiceId.Value.ToString("D");
        }

        if (record.BankTransactionId is not null)
        {
            return record.BankTransactionId.Value.ToString("D");
        }

        throw new InvalidOperationException(
            "A classified match record must identify an invoice or a bank transaction.");
    }

    private readonly record struct MatchKey(string Reference, string Currency, decimal Amount)
        : IComparable<MatchKey>
    {
        public int CompareTo(MatchKey other)
        {
            int referenceComparison = string.CompareOrdinal(Reference, other.Reference);

            if (referenceComparison != 0)
            {
                return referenceComparison;
            }

            int currencyComparison = string.CompareOrdinal(Currency, other.Currency);

            if (currencyComparison != 0)
            {
                return currencyComparison;
            }

            return Amount.CompareTo(other.Amount);
        }
    }

    private readonly record struct OpenInvoice(InvoiceId Id, Money OpenAmount);

    private readonly record struct OpenTransaction(BankTransactionId Id, Money OpenAmount);
}