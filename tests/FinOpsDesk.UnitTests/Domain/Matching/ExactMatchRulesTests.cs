using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class ExactMatchRulesTests
{
    private static readonly OrganizationId Organization =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly OrganizationId OtherOrganization =
        new(new Guid("22222222-2222-2222-2222-222222222222"));

    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public void Evaluate_should_return_one_exact_match_for_the_same_open_amount_and_reference()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 1);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 2);

        ExactMatchBatchResult result = Match([invoice], [bankTransaction]);

        ExactMatch match = Assert.Single(result.ExactMatches);
        Assert.Equal(ExactMatchRules.AlgorithmVersion, result.AlgorithmVersion);
        Assert.Equal("exact-1", result.AlgorithmVersion);
        Assert.Equal(invoice.InvoiceId, match.InvoiceId);
        Assert.Equal(bankTransaction.BankTransactionId, match.BankTransactionId);
        Assert.Equal(new Money(100m, Pln), match.Amount);
        Assert.Equal("INV-001", match.NormalizedReference);
        Assert.Equal(MatchReasonCode.ExactReferenceCurrencyAndOpenAmount, match.Reason);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Empty(result.Unmatched);
        Assert.Empty(result.Ineligible);
    }

    [Theory]
    [InlineData("INV-001", "inv-001")]
    [InlineData(" INV-001 ", "INV-001")]
    [InlineData("INV 001", "inv 001")]
    public void Evaluate_should_match_references_that_differ_only_by_case_or_outer_whitespace(
        string invoiceNumber,
        string reference)
    {
        ExactMatchBatchResult result = Match(
            [Invoice(invoiceNumber, 80m)],
            [Transaction(reference, 80m)]);

        ExactMatch match = Assert.Single(result.ExactMatches);
        Assert.Equal(invoiceNumber.Trim().ToUpperInvariant(), match.NormalizedReference);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Empty(result.Unmatched);
        Assert.Empty(result.Ineligible);
    }

    [Theory]
    [InlineData("INV 001", "INV001")]
    [InlineData("INV-001", "INV001")]
    [InlineData("INV/001", "INV-001")]
    [InlineData("INV-001", "INV-002")]
    public void Evaluate_should_not_match_different_references_with_equal_amounts(
        string invoiceNumber,
        string reference)
    {
        ExactMatchBatchResult result = Match(
            [Invoice(invoiceNumber, 100m)],
            [Transaction(reference, 100m)]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Equal(2, result.Unmatched.Count);
        Assert.All(
            result.Unmatched,
            record => Assert.Equal(MatchReasonCode.NoCounterpart, record.Reason));
        Assert.Empty(result.Ineligible);
    }

    [Fact]
    public void Evaluate_should_not_match_different_currencies()
    {
        ExactMatchBatchResult result = Match(
            [Invoice("INV-001", 100m, Pln)],
            [Transaction("INV-001", 100m, Eur)]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Equal(2, result.Unmatched.Count);
        Assert.Contains(result.Unmatched, record => record.OpenAmount.Currency == Pln);
        Assert.Contains(result.Unmatched, record => record.OpenAmount.Currency == Eur);
    }

    [Fact]
    public void Evaluate_should_not_match_different_open_amounts()
    {
        ExactMatchBatchResult result = Match(
            [Invoice("INV-001", 100m)],
            [Transaction("INV-001", 80m)]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Equal(2, result.Unmatched.Count);
        Assert.Contains(result.Unmatched, record => record.OpenAmount.Amount == 100m);
        Assert.Contains(result.Unmatched, record => record.OpenAmount.Amount == 80m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_should_treat_a_missing_invoice_number_as_ineligible(string? invoiceNumber)
    {
        MatchInvoiceSnapshot invoice = Invoice(invoiceNumber, 100m, id: 11);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 12);

        ExactMatchBatchResult result = Match([invoice], [bankTransaction]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        ClassifiedMatchRecord ineligible = Assert.Single(result.Ineligible);
        Assert.Equal(MatchClassification.Ineligible, ineligible.Classification);
        Assert.Equal(MatchReasonCode.MissingReference, ineligible.Reason);
        Assert.Equal(invoice.InvoiceId, ineligible.InvoiceId);
        Assert.Null(ineligible.NormalizedReference);
        ClassifiedMatchRecord unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(bankTransaction.BankTransactionId, unmatched.BankTransactionId);
        Assert.Equal(MatchReasonCode.NoCounterpart, unmatched.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t")]
    public void Evaluate_should_treat_a_missing_transaction_reference_as_ineligible(string? reference)
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 40m, id: 13);
        MatchBankTransactionSnapshot bankTransaction = Transaction(reference, 40m, id: 14);

        ExactMatchBatchResult result = Match([invoice], [bankTransaction]);

        Assert.Empty(result.ExactMatches);
        ClassifiedMatchRecord ineligible = Assert.Single(result.Ineligible);
        Assert.Equal(MatchReasonCode.MissingReference, ineligible.Reason);
        Assert.Equal(bankTransaction.BankTransactionId, ineligible.BankTransactionId);
        Assert.Null(ineligible.InvoiceId);
        ClassifiedMatchRecord unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(invoice.InvoiceId, unmatched.InvoiceId);
    }

    [Fact]
    public void Evaluate_should_treat_a_fully_allocated_invoice_as_ineligible()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 0m, id: 15);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 16);

        ExactMatchBatchResult result = Match([invoice], [bankTransaction]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        ClassifiedMatchRecord ineligible = Assert.Single(result.Ineligible);
        Assert.Equal(invoice.InvoiceId, ineligible.InvoiceId);
        Assert.Equal(MatchReasonCode.NonPositiveOpenAmount, ineligible.Reason);
        Assert.Equal("INV-001", ineligible.NormalizedReference);
        ClassifiedMatchRecord unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(bankTransaction.BankTransactionId, unmatched.BankTransactionId);
    }

    [Fact]
    public void Evaluate_should_prefer_non_positive_amount_when_the_reference_is_also_missing()
    {
        ExactMatchBatchResult result = Match(
            [Invoice(null, 0m, id: 71)],
            [Transaction("   ", -10m, id: 72)]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.Unmatched);
        Assert.Equal(2, result.Ineligible.Count);
        Assert.All(
            result.Ineligible,
            record => Assert.Equal(MatchReasonCode.NonPositiveOpenAmount, record.Reason));
    }

    [Fact]
    public void Evaluate_should_treat_a_fully_allocated_transaction_as_ineligible()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 17);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 0m, id: 18);

        ExactMatchBatchResult result = Match([invoice], [bankTransaction]);

        Assert.Empty(result.ExactMatches);
        ClassifiedMatchRecord ineligible = Assert.Single(result.Ineligible);
        Assert.Equal(bankTransaction.BankTransactionId, ineligible.BankTransactionId);
        Assert.Equal(MatchReasonCode.NonPositiveOpenAmount, ineligible.Reason);
        ClassifiedMatchRecord unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(invoice.InvoiceId, unmatched.InvoiceId);
    }

    [Fact]
    public void Evaluate_should_match_equal_remaining_balances()
    {
        ExactMatchBatchResult result = Match(
            [Invoice("INV-440", 400m, id: 19)],
            [Transaction("inv-440", 400m, id: 20)]);

        ExactMatch match = Assert.Single(result.ExactMatches);
        Assert.Equal(new Money(400m, Pln), match.Amount);
        Assert.Equal("INV-440", match.NormalizedReference);
    }

    [Fact]
    public void Evaluate_should_mark_two_invoices_competing_for_one_transaction_as_ambiguous()
    {
        MatchInvoiceSnapshot first = Invoice("INV-001", 100m, id: 22);
        MatchInvoiceSnapshot second = Invoice("INV-001", 100m, id: 21);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 23);

        ExactMatchBatchResult result = Match([first, second], [bankTransaction]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.Unmatched);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal(MatchReasonCode.CompetingInvoices, group.Reason);
        Assert.Equal(new Money(100m, Pln), group.OpenAmount);
        Assert.Equal(
            [second.InvoiceId, first.InvoiceId],
            group.InvoiceIds);
        Assert.Equal([bankTransaction.BankTransactionId], group.BankTransactionIds);
    }

    [Fact]
    public void Evaluate_should_mark_two_transactions_competing_for_one_invoice_as_ambiguous()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 24);
        MatchBankTransactionSnapshot first = Transaction("INV-001", 100m, id: 26);
        MatchBankTransactionSnapshot second = Transaction("INV-001", 100m, id: 25);

        ExactMatchBatchResult result = Match([invoice], [first, second]);

        Assert.Empty(result.ExactMatches);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal(MatchReasonCode.CompetingTransactions, group.Reason);
        Assert.Equal([invoice.InvoiceId], group.InvoiceIds);
        Assert.Equal(
            [second.BankTransactionId, first.BankTransactionId],
            group.BankTransactionIds);
    }

    [Fact]
    public void Evaluate_should_mark_multiple_invoices_and_transactions_on_one_key_as_ambiguous()
    {
        ExactMatchBatchResult result = Match(
            [
                Invoice("INV-001", 75m, id: 28),
                Invoice("inv-001", 75m, id: 27)
            ],
            [
                Transaction("INV-001", 75m, id: 30),
                Transaction(" INV-001 ", 75m, id: 29)
            ]);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.Unmatched);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal(MatchReasonCode.CompetingInvoicesAndTransactions, group.Reason);
        Assert.Equal(2, group.InvoiceIds.Count);
        Assert.Equal(2, group.BankTransactionIds.Count);
        Assert.Equal("INV-001", group.NormalizedReference);
    }

    [Fact]
    public void Evaluate_should_keep_an_exact_match_when_another_open_amount_does_not_compete()
    {
        MatchInvoiceSnapshot full = Invoice("INV-001", 100m, id: 31);
        MatchInvoiceSnapshot partial = Invoice("INV-001", 50m, id: 32);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 33);

        ExactMatchBatchResult result = Match(
            [partial, full],
            [bankTransaction]);

        ExactMatch match = Assert.Single(result.ExactMatches);
        Assert.Equal(full.InvoiceId, match.InvoiceId);
        Assert.Equal(bankTransaction.BankTransactionId, match.BankTransactionId);
        Assert.Equal(new Money(100m, Pln), match.Amount);
        Assert.Empty(result.AmbiguousGroups);
        ClassifiedMatchRecord unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(partial.InvoiceId, unmatched.InvoiceId);
        Assert.Equal(MatchReasonCode.NoCounterpart, unmatched.Reason);
        Assert.Equal(new Money(50m, Pln), unmatched.OpenAmount);
    }

    [Fact]
    public void Evaluate_should_not_treat_same_key_invoices_as_ambiguous_without_a_transaction()
    {
        ExactMatchBatchResult result = Match(
            [
                Invoice("INV-001", 20m, id: 34),
                Invoice("INV-001", 20m, id: 35)
            ],
            []);

        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Equal(2, result.Unmatched.Count);
        Assert.All(result.Unmatched, record => Assert.NotNull(record.InvoiceId));
    }

    [Fact]
    public void Evaluate_should_reject_a_foreign_organization_invoice_without_returning_it()
    {
        MatchInvoiceSnapshot foreign = Invoice(
            "INV-001",
            100m,
            id: 41,
            organizationId: OtherOrganization);
        MatchInvoiceSnapshot local = Invoice("INV-001", 100m, id: 42);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 43);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match([local, foreign], [bankTransaction]));

        Assert.Equal("invoices", exception.ParamName);
        Assert.DoesNotContain(foreign.InvoiceId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_a_foreign_organization_transaction_without_returning_it()
    {
        MatchBankTransactionSnapshot foreign = Transaction(
            "INV-001",
            100m,
            id: 44,
            organizationId: OtherOrganization);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match(
                [Invoice("INV-001", 100m)],
                [foreign]));

        Assert.Equal("bankTransactions", exception.ParamName);
        Assert.DoesNotContain(
            foreign.BankTransactionId.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_order_results_independently_of_input_order()
    {
        MatchInvoiceSnapshot invoiceBbb = Invoice("bbb-9", 30m, id: 2);
        MatchInvoiceSnapshot invoiceAaa = Invoice("aaa-1", 10m, id: 8);
        MatchInvoiceSnapshot invoiceLow = Invoice("mmm", 5m, id: 4);
        MatchInvoiceSnapshot invoiceHigh = Invoice("mmm", 15m, id: 3);
        MatchInvoiceSnapshot ambiguousHigh = Invoice("zzz", 50m, id: 90);
        MatchInvoiceSnapshot ambiguousLow = Invoice("zzz", 50m, id: 10);
        MatchInvoiceSnapshot unmatchedInvoice = Invoice("qqq", 7m, id: 6);
        MatchInvoiceSnapshot closedInvoice = Invoice("aaa-0", 0m, id: 5);

        MatchBankTransactionSnapshot transactionBbb = Transaction("BBB-9", 30m, id: 9);
        MatchBankTransactionSnapshot transactionAaa = Transaction(" AAA-1 ", 10m, id: 1);
        MatchBankTransactionSnapshot transactionLow = Transaction("mmm", 5m, id: 12);
        MatchBankTransactionSnapshot transactionHigh = Transaction("MMM", 15m, id: 11);
        MatchBankTransactionSnapshot ambiguousTransactionHigh = Transaction("zzz", 50m, id: 80);
        MatchBankTransactionSnapshot ambiguousTransactionLow = Transaction("ZZZ", 50m, id: 7);
        MatchBankTransactionSnapshot unmatchedTransaction = Transaction("lll", 4m, id: 13);
        MatchBankTransactionSnapshot missingReference = Transaction(null, 3m, id: 14);

        MatchInvoiceSnapshot[] invoices =
        [
            closedInvoice,
            unmatchedInvoice,
            ambiguousHigh,
            ambiguousLow,
            invoiceBbb,
            invoiceAaa,
            invoiceHigh,
            invoiceLow
        ];
        MatchBankTransactionSnapshot[] bankTransactions =
        [
            missingReference,
            unmatchedTransaction,
            ambiguousTransactionHigh,
            ambiguousTransactionLow,
            transactionBbb,
            transactionAaa,
            transactionHigh,
            transactionLow
        ];

        ExactMatchBatchResult forward = Match(invoices, bankTransactions);
        ExactMatchBatchResult reversed = Match(
            invoices.Reverse().ToArray(),
            bankTransactions.Reverse().ToArray());

        Guid[] expectedExactInvoices =
        [
            invoiceAaa.InvoiceId.Value,
            invoiceBbb.InvoiceId.Value,
            invoiceLow.InvoiceId.Value,
            invoiceHigh.InvoiceId.Value
        ];

        Assert.Equal(expectedExactInvoices, forward.ExactMatches.Select(match => match.InvoiceId.Value));
        Assert.Equal(expectedExactInvoices, reversed.ExactMatches.Select(match => match.InvoiceId.Value));
        Assert.Equal(
            forward.ExactMatches.Select(match => match.BankTransactionId),
            reversed.ExactMatches.Select(match => match.BankTransactionId));
        Assert.Equal(4, forward.ExactMatches.Select(match => match.InvoiceId).Distinct().Count());

        AmbiguousMatchGroup group = Assert.Single(forward.AmbiguousGroups);
        AmbiguousMatchGroup reversedGroup = Assert.Single(reversed.AmbiguousGroups);
        Assert.Equal(
            [ambiguousLow.InvoiceId, ambiguousHigh.InvoiceId],
            group.InvoiceIds);
        Assert.Equal(group.InvoiceIds, reversedGroup.InvoiceIds);
        Assert.Equal(
            [ambiguousTransactionLow.BankTransactionId, ambiguousTransactionHigh.BankTransactionId],
            group.BankTransactionIds);

        Assert.Equal(unmatchedTransaction.BankTransactionId, forward.Unmatched[0].BankTransactionId);
        Assert.Equal(unmatchedInvoice.InvoiceId, forward.Unmatched[1].InvoiceId);
        Assert.Equal(
            forward.Unmatched.Select(record => record.NormalizedReference),
            reversed.Unmatched.Select(record => record.NormalizedReference));

        Assert.Equal(missingReference.BankTransactionId, forward.Ineligible[0].BankTransactionId);
        Assert.Equal(closedInvoice.InvoiceId, forward.Ineligible[1].InvoiceId);
        Assert.Equal(MatchReasonCode.MissingReference, forward.Ineligible[0].Reason);
        Assert.Equal(MatchReasonCode.NonPositiveOpenAmount, forward.Ineligible[1].Reason);
        Assert.Equal(
            forward.Ineligible.Select(record => record.Reason),
            reversed.Ineligible.Select(record => record.Reason));
    }

    [Fact]
    public void Evaluate_should_return_an_empty_result_for_empty_inputs()
    {
        ExactMatchBatchResult result = ExactMatchRules.Evaluate(
            Organization,
            Array.Empty<MatchInvoiceSnapshot>(),
            Array.Empty<MatchBankTransactionSnapshot>());

        Assert.Equal("exact-1", result.AlgorithmVersion);
        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Empty(result.Unmatched);
        Assert.Empty(result.Ineligible);
    }

    [Fact]
    public void Evaluate_should_emit_each_exact_pair_once()
    {
        MatchInvoiceSnapshot firstInvoice = Invoice("INV-001", 10m, id: 51);
        MatchInvoiceSnapshot secondInvoice = Invoice("INV-002", 20m, id: 52);
        MatchBankTransactionSnapshot firstTransaction = Transaction("INV-001", 10m, id: 61);
        MatchBankTransactionSnapshot secondTransaction = Transaction("INV-002", 20m, id: 62);

        ExactMatchBatchResult result = Match(
            [secondInvoice, firstInvoice],
            [secondTransaction, firstTransaction]);

        Assert.Equal(2, result.ExactMatches.Count);
        Assert.Equal(
            [firstInvoice.InvoiceId, secondInvoice.InvoiceId],
            result.ExactMatches.Select(match => match.InvoiceId));
        Assert.Equal(
            [firstTransaction.BankTransactionId, secondTransaction.BankTransactionId],
            result.ExactMatches.Select(match => match.BankTransactionId));
        Assert.Empty(result.AmbiguousGroups);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Evaluate_should_reject_a_duplicate_invoice_id_with_identical_values()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 1);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match(
                [invoice, Invoice("INV-001", 100m, id: 1)],
                [Transaction("INV-001", 100m, id: 2)]));

        Assert.Equal("invoices", exception.ParamName);
        Assert.DoesNotContain(invoice.InvoiceId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_a_duplicate_invoice_id_with_different_values()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 1);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match(
                [invoice, Invoice("INV-002", 40m, id: 1)],
                [
                    Transaction("INV-001", 100m, id: 2),
                    Transaction("INV-002", 40m, id: 3)
                ]));

        Assert.Equal("invoices", exception.ParamName);
        Assert.DoesNotContain(invoice.InvoiceId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_a_duplicate_bank_transaction_id_with_identical_values()
    {
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 2);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match(
                [Invoice("INV-001", 100m, id: 1)],
                [bankTransaction, Transaction("INV-001", 100m, id: 2)]));

        Assert.Equal("bankTransactions", exception.ParamName);
        Assert.DoesNotContain(
            bankTransaction.BankTransactionId.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_a_duplicate_bank_transaction_id_with_different_values()
    {
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 2);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Match(
                [
                    Invoice("INV-001", 100m, id: 1),
                    Invoice("INV-009", 25m, id: 4)
                ],
                [bankTransaction, Transaction("INV-009", 25m, id: 2)]));

        Assert.Equal("bankTransactions", exception.ParamName);
        Assert.DoesNotContain(
            bankTransaction.BankTransactionId.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_treat_distinct_ids_with_the_same_key_as_ambiguous()
    {
        ExactMatchBatchResult result = Match(
            [
                Invoice("INV-001", 100m, id: 1),
                Invoice("inv-001", 100m, id: 2)
            ],
            [Transaction("INV-001", 100m, id: 3)]);

        Assert.Empty(result.ExactMatches);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal(MatchReasonCode.CompetingInvoices, group.Reason);
        Assert.Equal(2, group.InvoiceIds.Count);
    }

    [Fact]
    public void Result_collections_should_copy_inputs_and_reject_mutation()
    {
        List<InvoiceId> invoiceIds =
        [
            new InvoiceId(GuidOf(20)),
            new InvoiceId(GuidOf(10))
        ];
        List<BankTransactionId> bankTransactionIds =
        [
            new BankTransactionId(GuidOf(30))
        ];
        List<ExactMatch> exactMatches = [CreateExactMatch()];
        List<AmbiguousMatchGroup> ambiguousGroups =
        [
            CreateAmbiguousGroup(invoiceIds, bankTransactionIds)
        ];
        List<ClassifiedMatchRecord> unmatched = [CreateUnmatched()];
        List<ClassifiedMatchRecord> ineligible = [CreateIneligible()];

        ExactMatchBatchResult result = new(
            ExactMatchRules.AlgorithmVersion,
            exactMatches,
            ambiguousGroups,
            unmatched,
            ineligible);

        exactMatches.Clear();
        ambiguousGroups.Clear();
        unmatched.Clear();
        ineligible.Clear();
        invoiceIds.Clear();
        bankTransactionIds.Clear();

        Assert.Single(result.ExactMatches);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal(
            [new InvoiceId(GuidOf(10)), new InvoiceId(GuidOf(20))],
            group.InvoiceIds);
        Assert.Equal([new BankTransactionId(GuidOf(30))], group.BankTransactionIds);
        Assert.Single(result.Unmatched);
        Assert.Single(result.Ineligible);

        Assert.Null(result.ExactMatches as ExactMatch[]);
        Assert.Null(result.AmbiguousGroups as AmbiguousMatchGroup[]);
        Assert.Null(result.Unmatched as ClassifiedMatchRecord[]);
        Assert.Null(result.Ineligible as ClassifiedMatchRecord[]);
        Assert.Null(group.InvoiceIds as InvoiceId[]);
        Assert.Null(group.BankTransactionIds as BankTransactionId[]);

        AssertReadOnly(result.ExactMatches);
        AssertReadOnly(result.AmbiguousGroups);
        AssertReadOnly(result.Unmatched);
        AssertReadOnly(result.Ineligible);
        AssertReadOnly(group.InvoiceIds);
        AssertReadOnly(group.BankTransactionIds);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        ICollection<T> collection = Assert.IsAssignableFrom<ICollection<T>>(values);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(collection.Clear);
    }

    private static ExactMatch CreateExactMatch()
    {
        return new ExactMatch(
            new InvoiceId(GuidOf(1)),
            new BankTransactionId(GuidOf(2)),
            new Money(10m, Pln),
            "INV-001",
            MatchReasonCode.ExactReferenceCurrencyAndOpenAmount);
    }

    private static AmbiguousMatchGroup CreateAmbiguousGroup(
        IReadOnlyList<InvoiceId> invoiceIds,
        IReadOnlyList<BankTransactionId> bankTransactionIds)
    {
        return new AmbiguousMatchGroup(
            "INV-001",
            new Money(100m, Pln),
            MatchReasonCode.CompetingInvoices,
            invoiceIds,
            bankTransactionIds);
    }

    private static ClassifiedMatchRecord CreateUnmatched()
    {
        return new ClassifiedMatchRecord(
            MatchClassification.NoMatch,
            MatchReasonCode.NoCounterpart,
            new InvoiceId(GuidOf(3)),
            bankTransactionId: null,
            "INV-009",
            new Money(9m, Pln));
    }

    private static ClassifiedMatchRecord CreateIneligible()
    {
        return new ClassifiedMatchRecord(
            MatchClassification.Ineligible,
            MatchReasonCode.MissingReference,
            invoiceId: null,
            new BankTransactionId(GuidOf(4)),
            normalizedReference: null,
            new Money(4m, Pln));
    }

    private static ExactMatchBatchResult Match(
        MatchInvoiceSnapshot[] invoices,
        MatchBankTransactionSnapshot[] bankTransactions)
    {
        return ExactMatchRules.Evaluate(Organization, invoices, bankTransactions);
    }

    private static MatchInvoiceSnapshot Invoice(
        string? invoiceNumber,
        decimal outstanding,
        CurrencyCode? currency = null,
        int? id = null,
        OrganizationId? organizationId = null)
    {
        return new MatchInvoiceSnapshot(
            organizationId ?? Organization,
            new InvoiceId(id.HasValue ? GuidOf(id.Value) : Guid.NewGuid()),
            invoiceNumber,
            new Money(outstanding, currency ?? Pln));
    }

    private static MatchBankTransactionSnapshot Transaction(
        string? reference,
        decimal unapplied,
        CurrencyCode? currency = null,
        int? id = null,
        OrganizationId? organizationId = null)
    {
        return new MatchBankTransactionSnapshot(
            organizationId ?? Organization,
            new BankTransactionId(id.HasValue ? GuidOf(id.Value) : Guid.NewGuid()),
            reference,
            new Money(unapplied, currency ?? Pln));
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }
}