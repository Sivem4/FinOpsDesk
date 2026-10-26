using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class SmartMatchRulesTests
{
    private static readonly OrganizationId Organization =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly OrganizationId OtherOrganization =
        new(new Guid("22222222-2222-2222-2222-222222222222"));

    private static readonly CurrencyCode Pln = new("PLN");

    private static readonly CurrencyCode Eur = new("EUR");

    private static readonly CurrencyCode Usd = new("USD");

    private static readonly CurrencyCode Gbp = new("GBP");

    [Fact]
    public void Evaluate_should_score_an_exact_reference_and_equal_amount_at_95()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("inv-001", 100m, id: 2)]).Suggestions);

        Assert.Equal(95, suggestion.Score);
        Assert.Equal(SmartMatchSuggestion.ScoreScale, "points");
        Assert.Equal(70, Points(suggestion, SmartMatchSignalCode.ReferenceExact));
        Assert.Equal(25, Points(suggestion, SmartMatchSignalCode.AmountExact));
        Assert.Equal(95, suggestion.Signals.Sum(signal => signal.Points));
        Assert.Equal(new Money(100m, Pln), suggestion.SuggestedAmount);
        Assert.False(suggestion.Contested);
        Assert.False(suggestion.ExactMatchAmbiguous);
    }

    [Fact]
    public void Evaluate_should_score_an_exact_reference_and_unequal_amount_at_70()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 40m, id: 2)]).Suggestions);

        Assert.Equal(70, suggestion.Score);
        Assert.Equal(70, Points(suggestion, SmartMatchSignalCode.ReferenceExact));
        Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.AmountDiffers));
        Assert.Equal(new Money(40m, Pln), suggestion.SuggestedAmount);
        Assert.Equal(100m, suggestion.InvoiceOutstanding.Amount);
        Assert.Equal(40m, suggestion.BankTransactionUnapplied.Amount);
    }

    [Fact]
    public void Evaluate_should_score_a_specific_reordered_token_set_and_equal_amount_at_85()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("FV/2026/1042", 400m, id: 1)],
            [Transaction("1042/2026/FV", 400m, id: 2)]).Suggestions);

        Assert.Equal(85, suggestion.Score);
        Assert.Equal(60, Points(suggestion, SmartMatchSignalCode.ReferenceTokenSetEqual));
        Assert.Equal(25, Points(suggestion, SmartMatchSignalCode.AmountExact));
        Assert.DoesNotContain(
            suggestion.Signals,
            signal => signal.Code == SmartMatchSignalCode.ReferenceExact);
        Assert.Equal(new Money(400m, Pln), suggestion.SuggestedAmount);
    }

    [Fact]
    public void Evaluate_should_score_a_qualifying_numeric_match_and_unequal_amount_at_60()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("INV-001234", 1000m, id: 1)],
            [Transaction("Payment INV001234", 400m, id: 2)]).Suggestions);

        Assert.Equal(60, suggestion.Score);
        Assert.Equal(60, Points(suggestion, SmartMatchSignalCode.NumericIdentifierEqual));
        Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.AmountDiffers));
        Assert.Equal(new Money(400m, Pln), suggestion.SuggestedAmount);
        Assert.Equal(1000m, suggestion.InvoiceOutstanding.Amount);
        Assert.Equal(400m, suggestion.BankTransactionUnapplied.Amount);
    }

    [Fact]
    public void Evaluate_should_not_stack_reference_signals()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("INV-001234", 80m, id: 1)],
            [Transaction("inv-001234", 80m, id: 2)]).Suggestions);

        Assert.Equal(95, suggestion.Score);
        Assert.Equal(70, Points(suggestion, SmartMatchSignalCode.ReferenceExact));
        Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.ReferenceTokenSetEqual));
        Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.CompactFormEqual));
        Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.NumericIdentifierEqual));
        Assert.Equal(25, Points(suggestion, SmartMatchSignalCode.AmountExact));
        Assert.Equal(1, suggestion.Signals.Count(signal => signal.Points > 0 && IsReference(signal.Code)));
    }

    [Theory]
    [InlineData("RENT MAY", "MAY RENT")]
    [InlineData("FV/2026", "2026/FV")]
    [InlineData("INV-001", "INV-01")]
    [InlineData("001234", "1001234")]
    [InlineData("INV-12345", "PAY-12345")]
    [InlineData("FV/2026/100", "AB/2026")]
    [InlineData("12-34-56-78", "12345678")]
    [InlineData("000123456", "123456")]
    [InlineData("FV/2025/001234", "FV/2026/001234")]
    [InlineData("FV/2025/2026/001234", "FV/2026/001234")]
    [InlineData("INV-001234", "FV/2026/001234")]
    [InlineData("AAAA", "BBBB")]
    public void Evaluate_should_reject_weak_or_conflicting_references(string invoiceNumber, string reference)
    {
        SmartMatchBatchResult result = Evaluate(
            [Invoice(invoiceNumber, 100m, id: 1)],
            [Transaction(reference, 100m, id: 2)]);

        Assert.Empty(result.Suggestions);
        Assert.False(result.Truncated);
        Assert.False(result.CoverageLimited);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_should_ignore_a_missing_reference(string? reference)
    {
        SmartMatchBatchResult result = Evaluate(
            [Invoice("INV-001234", 100m, id: 1)],
            [Transaction(reference, 100m, id: 2)]);

        Assert.Empty(result.Suggestions);
        Assert.False(result.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_match_the_same_year_with_a_shared_long_identifier()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("FV/2026/001234", 50m, id: 1)],
            [Transaction("PAY/2026/001234", 50m, id: 2)]).Suggestions);

        Assert.Equal(85, suggestion.Score);
        Assert.Equal(60, Points(suggestion, SmartMatchSignalCode.NumericIdentifierEqual));
        Assert.Equal(25, Points(suggestion, SmartMatchSignalCode.AmountExact));
    }

    [Fact]
    public void Evaluate_should_match_separator_differences_without_gluing_short_numbers()
    {
        SmartMatchSuggestion suggestion = Assert.Single(Evaluate(
            [Invoice("INV-001234", 20m, id: 1)],
            [Transaction("INV001234", 20m, id: 2)]).Suggestions);

        Assert.Equal(60, Points(suggestion, SmartMatchSignalCode.CompactFormEqual));
        Assert.Equal(85, suggestion.Score);
    }

    [Fact]
    public void Evaluate_should_reject_a_currency_mismatch()
    {
        SmartMatchBatchResult result = Evaluate(
            [Invoice("INV-001234", 100m, currency: Pln, id: 1)],
            [Transaction("INV-001234", 100m, currency: Eur, id: 2)]);

        Assert.Empty(result.Suggestions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_should_reject_a_non_positive_open_amount(decimal amount)
    {
        Assert.Empty(Evaluate(
            [Invoice("INV-001234", amount, id: 1)],
            [Transaction("INV-001234", 100m, id: 2)]).Suggestions);
        Assert.Empty(Evaluate(
            [Invoice("INV-001234", 100m, id: 1)],
            [Transaction("INV-001234", amount, id: 2)]).Suggestions);
    }

    [Fact]
    public void Evaluate_should_rank_85_ahead_of_a_weaker_total_with_a_stronger_reference_signal()
    {
        SmartMatchBatchResult result = Evaluate(
            [
                Invoice("INV-001", 100m, id: 1),
                Invoice("FV/2026/1042", 400m, id: 2),
            ],
            [
                Transaction("INV-001", 40m, id: 3),
                Transaction("1042/2026/FV", 400m, id: 4),
            ]);

        Assert.Equal(2, result.Suggestions.Count);
        Assert.Equal(85, result.Suggestions[0].Score);
        Assert.Equal(1, result.Suggestions[0].Rank);
        Assert.Equal(GuidOf(2), result.Suggestions[0].InvoiceId.Value);
        Assert.Equal(70, result.Suggestions[1].Score);
    }

    [Fact]
    public void Evaluate_should_keep_the_same_ranking_when_input_order_changes()
    {
        MatchInvoiceSnapshot[] invoices =
        [
            Invoice("INV-001", 100m, id: 1),
            Invoice("FV/2026/1042", 400m, id: 2),
            Invoice("INV-009999", 15m, id: 5),
        ];
        MatchBankTransactionSnapshot[] transactions =
        [
            Transaction("INV-001", 40m, id: 3),
            Transaction("1042/2026/FV", 400m, id: 4),
            Transaction("PAY-009999", 15m, id: 6),
        ];

        SmartMatchBatchResult forward = Evaluate(invoices, transactions);
        Array.Reverse(invoices);
        Array.Reverse(transactions);
        SmartMatchBatchResult reversed = Evaluate(invoices, transactions);

        Assert.Equal(
            forward.Suggestions.Select(Identity),
            reversed.Suggestions.Select(Identity));
    }

    [Fact]
    public void Evaluate_should_emit_one_suggestion_when_a_pair_shares_several_blocking_keys()
    {
        SmartMatchBatchResult result = Evaluate(
            [Invoice("INV-001234", 80m, id: 1)],
            [Transaction("INV-001234", 80m, id: 2)]);

        Assert.Single(result.Suggestions);
        Assert.False(result.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_keep_a_higher_total_score_inside_the_per_invoice_limit()
    {
        MatchInvoiceSnapshot invoice = Invoice("FV/2026/1042", 100m, id: 1);
        List<MatchBankTransactionSnapshot> transactions = new();

        for (int id = 1; id <= SmartMatchRules.MaxCandidatesPerInvoice; id++)
        {
            transactions.Add(Transaction("FV/2026/1042", 40m, id: id + 10));
        }

        transactions.Add(Transaction("1042/2026/FV", 100m, id: 99));

        SmartMatchBatchResult result = Evaluate([invoice], transactions);

        Assert.Equal(SmartMatchRules.MaxCandidatesPerInvoice, result.Suggestions.Count);
        Assert.Contains(result.Suggestions, suggestion => suggestion.BankTransactionId.Value == GuidOf(99));
        Assert.Equal(85, result.Suggestions[0].Score);
        Assert.False(result.Suggestions[0].Contested);
        Assert.True(result.CoverageLimited);
        Assert.False(result.Truncated);
        Assert.DoesNotContain(
            result.Suggestions,
            suggestion => suggestion.BankTransactionId.Value == GuidOf(10 + SmartMatchRules.MaxCandidatesPerInvoice));
    }

    [Fact]
    public void Evaluate_should_mark_a_competitor_removed_by_the_per_invoice_limit()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001234", 100m, id: 1);
        List<MatchBankTransactionSnapshot> transactions = new();

        for (int id = 1; id <= 15; id++)
        {
            transactions.Add(Transaction("INV-001234", 100m, id: id));
        }

        transactions.Add(Transaction("PAY-001234", 50m, id: 16));
        transactions.Add(Transaction("NOTE-001234", 50m, id: 17));

        SmartMatchBatchResult result = Evaluate([invoice], transactions);
        SmartMatchSuggestion keptPartial = Assert.Single(
            result.Suggestions,
            suggestion => suggestion.Score == 60);

        Assert.Equal(16, result.Suggestions.Count);
        Assert.Equal(GuidOf(16), keptPartial.BankTransactionId.Value);
        Assert.True(keptPartial.Contested);
        Assert.Equal(0, Points(keptPartial, SmartMatchSignalCode.ContestedCandidates));
        Assert.True(result.CoverageLimited);
        Assert.DoesNotContain(
            result.Suggestions,
            suggestion => suggestion.BankTransactionId.Value == GuidOf(17));
    }

    [Fact]
    public void Evaluate_should_truncate_output_and_keep_a_contest_from_the_dropped_tail()
    {
        List<MatchInvoiceSnapshot> invoices = new();
        List<MatchBankTransactionSnapshot> transactions = new();

        for (int id = 1; id <= 199; id++)
        {
            string reference = $"EXACT-{id:000000}";
            invoices.Add(Invoice(reference, 10m, id: id));
            transactions.Add(Transaction(reference, 10m, id: 1000 + id));
        }

        invoices.Add(Invoice("INV-001234", 1000m, id: 500));
        transactions.Add(Transaction("PAY-001234", 400m, id: 601));
        transactions.Add(Transaction("NOTE-001234", 400m, id: 602));

        SmartMatchBatchResult result = Evaluate(invoices, transactions);
        SmartMatchSuggestion partial = Assert.Single(
            result.Suggestions,
            suggestion => suggestion.InvoiceId.Value == GuidOf(500));

        Assert.Equal(SmartMatchRules.MaxSuggestions, result.Suggestions.Count);
        Assert.True(result.Truncated);
        Assert.False(result.CoverageLimited);
        Assert.Equal(60, partial.Score);
        Assert.True(partial.Contested);
        Assert.Equal(200, result.Suggestions[^1].Rank);
    }

    [Fact]
    public void Evaluate_should_mark_equal_scores_contested_and_leave_a_wide_gap_uncontested()
    {
        SmartMatchBatchResult close = Evaluate(
            [Invoice("INV-001234", 100m, id: 1)],
            [
                Transaction("INV-001234", 100m, id: 2),
                Transaction("PAY-001234", 100m, id: 3),
            ]);

        Assert.All(close.Suggestions, suggestion => Assert.True(suggestion.Contested));
        Assert.Equal(10, Math.Abs(close.Suggestions[0].Score - close.Suggestions[1].Score));

        SmartMatchBatchResult distant = Evaluate(
            [Invoice("INV-001234", 100m, id: 1)],
            [
                Transaction("INV-001234", 100m, id: 2),
                Transaction("INV-001234", 40m, id: 3),
            ]);

        Assert.Equal([95, 70], distant.Suggestions.Select(suggestion => suggestion.Score));
        Assert.All(distant.Suggestions, suggestion => Assert.False(suggestion.Contested));
    }

    [Fact]
    public void Evaluate_should_flag_exact_1_ambiguity_without_choosing_a_winner()
    {
        SmartMatchBatchResult result = Evaluate(
            [
                Invoice("INV-001234", 100m, id: 2),
                Invoice("inv-001234", 100m, id: 1),
            ],
            [Transaction("INV-001234", 100m, id: 3)]);

        Assert.Equal(2, result.Suggestions.Count);
        Assert.All(result.Suggestions, suggestion =>
        {
            Assert.True(suggestion.ExactMatchAmbiguous);
            Assert.True(suggestion.Contested);
            Assert.Equal(0, Points(suggestion, SmartMatchSignalCode.ExactMatchAmbiguous));
            Assert.Equal(95, suggestion.Score);
        });
        Assert.Equal(GuidOf(1), result.Suggestions[0].InvoiceId.Value);
        Assert.Equal(GuidOf(2), result.Suggestions[1].InvoiceId.Value);
    }

    [Fact]
    public void Evaluate_should_keep_exact_1_ambiguity_when_the_input_is_reversed()
    {
        MatchInvoiceSnapshot[] invoices =
        [
            Invoice("INV-001234", 100m, id: 2),
            Invoice("INV-001234", 100m, id: 1),
        ];
        MatchBankTransactionSnapshot[] transactions = [Transaction("INV-001234", 100m, id: 3)];

        SmartMatchBatchResult forward = Evaluate(invoices, transactions);
        Array.Reverse(invoices);
        SmartMatchBatchResult reversed = Evaluate(invoices, transactions);

        Assert.Equal(
            forward.Suggestions.Select(suggestion => suggestion.InvoiceId),
            reversed.Suggestions.Select(suggestion => suggestion.InvoiceId));
        Assert.All(reversed.Suggestions, suggestion => Assert.True(suggestion.ExactMatchAmbiguous));
    }

    [Fact]
    public void Evaluate_should_allow_thirty_two_rows_on_a_blocking_key_and_stop_at_thirty_three()
    {
        SmartMatchBatchResult withinLimit = Evaluate(
            RepeatedInvoices("123456", 10m, SmartMatchRules.MaxRowsPerBlockingKey, firstId: 10),
            [Transaction("123456", 10m, id: 1)]);

        Assert.Equal(SmartMatchRules.MaxRowsPerBlockingKey, withinLimit.Suggestions.Count);
        Assert.False(withinLimit.CoverageLimited);

        SmartMatchBatchResult saturated = Evaluate(
            RepeatedInvoices("123456", 10m, SmartMatchRules.MaxRowsPerBlockingKey + 1, firstId: 10),
            [Transaction("123456", 10m, id: 1)]);

        Assert.Empty(saturated.Suggestions);
        Assert.True(saturated.CoverageLimited);
        Assert.False(saturated.Truncated);
    }

    [Fact]
    public void Evaluate_should_not_let_another_currency_saturate_a_matching_pair()
    {
        List<MatchInvoiceSnapshot> invoices = [Invoice("INV-001234", 100m, Pln, id: 1)];

        for (int id = 10; id < 10 + SmartMatchRules.MaxRowsPerBlockingKey; id++)
        {
            invoices.Add(Invoice("INV-001234", 100m, Eur, id: id));
        }

        SmartMatchBatchResult result = Evaluate(
            invoices,
            [Transaction("INV-001234", 100m, Pln, id: 2)]);

        SmartMatchSuggestion suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(GuidOf(1), suggestion.InvoiceId.Value);
        Assert.Equal(Pln, suggestion.SuggestedAmount.Currency);
        Assert.Equal(95, suggestion.Score);
        Assert.False(result.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_keep_a_valid_currency_when_another_currency_key_is_saturated()
    {
        List<MatchInvoiceSnapshot> invoices = RepeatedInvoices(
            "INV-001234",
            10m,
            SmartMatchRules.MaxRowsPerBlockingKey + 1,
            firstId: 1,
            currency: Pln);
        invoices.Add(Invoice("INV-001234", 10m, Eur, id: 80));

        SmartMatchBatchResult result = Evaluate(
            invoices,
            [
                Transaction("INV-001234", 10m, Pln, id: 90),
                Transaction("INV-001234", 10m, Eur, id: 91),
            ]);

        SmartMatchSuggestion suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(Eur, suggestion.SuggestedAmount.Currency);
        Assert.Equal(GuidOf(80), suggestion.InvoiceId.Value);
        Assert.True(result.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_match_each_currency_separately_when_references_and_amounts_match()
    {
        MatchInvoiceSnapshot[] invoices =
        [
            Invoice("INV-001234", 25m, Pln, id: 1),
            Invoice("INV-001234", 25m, Eur, id: 2),
            Invoice("INV-001234", 25m, Usd, id: 3),
        ];
        MatchBankTransactionSnapshot[] transactions =
        [
            Transaction("INV-001234", 25m, Pln, id: 4),
            Transaction("INV-001234", 25m, Eur, id: 5),
            Transaction("INV-001234", 25m, Gbp, id: 6),
        ];

        SmartMatchBatchResult forward = Evaluate(invoices, transactions);
        Array.Reverse(invoices);
        Array.Reverse(transactions);
        SmartMatchBatchResult reversed = Evaluate(invoices, transactions);

        Assert.Equal(2, forward.Suggestions.Count);
        Assert.Equal(
            forward.Suggestions.Select(Identity),
            reversed.Suggestions.Select(Identity));
        Assert.Contains(forward.Suggestions, suggestion => suggestion.SuggestedAmount.Currency == Pln);
        Assert.Contains(forward.Suggestions, suggestion => suggestion.SuggestedAmount.Currency == Eur);
        Assert.DoesNotContain(
            forward.Suggestions,
            suggestion => suggestion.SuggestedAmount.Currency == Usd
                || suggestion.SuggestedAmount.Currency == Gbp);
    }

    [Fact]
    public void Evaluate_should_contest_a_retained_score_when_any_discarded_score_is_inside_the_margin()
    {
        SmartMatchBatchResult result = Evaluate(DiscardedContestInvoices(includeNearScore: true), DiscardedContestTransactions());
        SmartMatchSuggestion retained = Assert.Single(
            result.Suggestions,
            suggestion => suggestion.InvoiceId.Value == GuidOf(1));

        Assert.Equal(70, retained.Score);
        Assert.Equal(GuidOf(900), retained.BankTransactionId.Value);
        Assert.True(retained.Contested);
        Assert.True(result.CoverageLimited);
        Assert.Single(result.Suggestions, suggestion => suggestion.BankTransactionId.Value == GuidOf(900));
    }

    [Fact]
    public void Evaluate_should_not_contest_a_retained_score_when_every_discarded_score_is_outside_the_margin()
    {
        SmartMatchBatchResult result = Evaluate(DiscardedContestInvoices(includeNearScore: false), DiscardedContestTransactions());
        SmartMatchSuggestion retained = Assert.Single(
            result.Suggestions,
            suggestion => suggestion.InvoiceId.Value == GuidOf(1));

        Assert.Equal(70, retained.Score);
        Assert.False(retained.Contested);
        Assert.Equal(15, Math.Abs(85 - retained.Score));
    }

    [Fact]
    public void Evaluate_should_emit_each_overlapping_pair_once_and_keep_the_ranking_stable()
    {
        List<MatchInvoiceSnapshot> invoices = new();
        List<MatchBankTransactionSnapshot> transactions = new();

        for (int id = 1; id <= 40; id++)
        {
            string reference = $"INV-{id:000000}";
            invoices.Add(Invoice(reference, 12m, id: id));
            transactions.Add(Transaction(reference, 12m, id: 200 + id));
        }

        invoices.Add(Invoice("FV/2026/100001", 30m, id: 80));
        transactions.Add(Transaction("100001/2026/FV", 30m, id: 81));
        transactions.Add(Transaction("PAY/2026/100001", 30m, id: 82));

        SmartMatchBatchResult forward = Evaluate(invoices, transactions);
        invoices.Reverse();
        transactions.Reverse();
        SmartMatchBatchResult reversed = Evaluate(invoices, transactions);

        Assert.Equal(42, forward.Suggestions.Count);
        Assert.Equal(
            42,
            forward.Suggestions.Select(suggestion => (suggestion.InvoiceId, suggestion.BankTransactionId)).Distinct().Count());
        Assert.Equal(41, forward.Suggestions.Select(suggestion => suggestion.InvoiceId).Distinct().Count());
        Assert.False(forward.Truncated);
        Assert.False(forward.CoverageLimited);
        Assert.Equal(
            forward.Suggestions.Select(Identity),
            reversed.Suggestions.Select(Identity));
    }

    [Fact]
    public void Evaluate_should_not_compare_unrelated_open_rows_as_a_cartesian_product()
    {
        List<MatchInvoiceSnapshot> invoices = new();
        List<MatchBankTransactionSnapshot> transactions = new();

        for (int id = 1; id <= 40; id++)
        {
            invoices.Add(Invoice($"INVOICE{id:00}", 25m, id: id));
            transactions.Add(Transaction($"PAYMENT{id:00}", 25m, id: 100 + id));
        }

        SmartMatchBatchResult result = Evaluate(invoices, transactions);

        Assert.Empty(result.Suggestions);
        Assert.False(result.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_limit_coverage_for_an_oversized_reference_and_accept_the_length_boundary()
    {
        string accepted = new('A', SmartReferenceNormalizer.MaxTrimmedLength);
        string rejected = new('B', SmartReferenceNormalizer.MaxTrimmedLength + 1);

        SmartMatchBatchResult withinLimit = Evaluate(
            [Invoice(accepted, 5m, id: 1)],
            [Transaction(accepted, 5m, id: 2)]);
        SmartMatchBatchResult overLimit = Evaluate(
            [Invoice(rejected, 5m, id: 1)],
            [Transaction(rejected, 5m, id: 2)]);

        Assert.Single(withinLimit.Suggestions);
        Assert.False(withinLimit.CoverageLimited);
        Assert.Empty(overLimit.Suggestions);
        Assert.True(overLimit.CoverageLimited);
    }

    [Fact]
    public void Evaluate_should_reject_a_foreign_invoice_without_returning_its_id()
    {
        MatchInvoiceSnapshot foreign = Invoice(
            "INV-001234",
            100m,
            id: 8,
            organizationId: OtherOrganization);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Evaluate([foreign], [Transaction("INV-001234", 100m, id: 9)]));

        Assert.Equal("invoices", exception.ParamName);
        Assert.DoesNotContain(foreign.InvoiceId.ToString()!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_a_foreign_transaction_without_returning_its_id()
    {
        MatchBankTransactionSnapshot foreign = Transaction(
            "INV-001234",
            100m,
            id: 8,
            organizationId: OtherOrganization);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            Evaluate([Invoice("INV-001234", 100m, id: 9)], [foreign]));

        Assert.Equal("bankTransactions", exception.ParamName);
        Assert.DoesNotContain(
            foreign.BankTransactionId.ToString()!,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_reject_duplicate_snapshot_ids()
    {
        ArgumentException invoiceException = Assert.Throws<ArgumentException>(() =>
            Evaluate(
                [Invoice("INV-001234", 10m, id: 1), Invoice("OTHER-009999", 10m, id: 1)],
                [Transaction("INV-001234", 10m, id: 2)]));
        ArgumentException transactionException = Assert.Throws<ArgumentException>(() =>
            Evaluate(
                [Invoice("INV-001234", 10m, id: 1)],
                [Transaction("INV-001234", 10m, id: 2), Transaction("OTHER-009999", 10m, id: 2)]));

        Assert.Equal("invoices", invoiceException.ParamName);
        Assert.Equal("bankTransactions", transactionException.ParamName);
    }

    [Fact]
    public void Evaluate_should_leave_snapshot_amounts_unchanged()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001234", 100m, id: 1);
        MatchBankTransactionSnapshot bankTransaction = Transaction("Payment INV001234", 40m, id: 2);

        SmartMatchSuggestion suggestion = Assert.Single(Evaluate([invoice], [bankTransaction]).Suggestions);

        Assert.Equal(100m, invoice.OutstandingAmount.Amount);
        Assert.Equal(40m, bankTransaction.UnappliedAmount.Amount);
        Assert.Equal(new Money(40m, Pln), suggestion.SuggestedAmount);
        Assert.Equal("smart-1", SmartMatchRules.AlgorithmVersion);
    }

    [Fact]
    public void Evaluate_should_leave_exact_1_classification_unchanged()
    {
        MatchInvoiceSnapshot invoice = Invoice("INV-001", 100m, id: 1);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV001", 100m, id: 2);

        ExactMatchBatchResult exact = ExactMatchRules.Evaluate(Organization, [invoice], [bankTransaction]);
        SmartMatchBatchResult smart = Evaluate([invoice], [bankTransaction]);

        Assert.Equal("exact-1", exact.AlgorithmVersion);
        Assert.Empty(exact.ExactMatches);
        Assert.Empty(smart.Suggestions);
    }

    private static string Identity(SmartMatchSuggestion suggestion)
    {
        return $"{suggestion.Rank}:{suggestion.Score}:{suggestion.InvoiceId.Value:D}:{suggestion.BankTransactionId.Value:D}:{suggestion.Contested}:{suggestion.ExactMatchAmbiguous}";
    }

    private static int Points(SmartMatchSuggestion suggestion, SmartMatchSignalCode code)
    {
        return Assert.Single(suggestion.Signals, signal => signal.Code == code).Points;
    }

    private static bool IsReference(SmartMatchSignalCode code)
    {
        return code is SmartMatchSignalCode.ReferenceExact
            or SmartMatchSignalCode.ReferenceTokenSetEqual
            or SmartMatchSignalCode.CompactFormEqual
            or SmartMatchSignalCode.NumericIdentifierEqual;
    }

    private static SmartMatchBatchResult Evaluate(
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        return SmartMatchRules.Evaluate(Organization, invoices, bankTransactions);
    }

    private static List<MatchInvoiceSnapshot> DiscardedContestInvoices(bool includeNearScore)
    {
        List<MatchInvoiceSnapshot> invoices =
        [
            Invoice("PAY-001234-009999", 40m, id: 1),
            Invoice("GRP-009999-777777", 100m, id: 3),
        ];

        if (includeNearScore)
        {
            invoices.Add(Invoice("GRP-001234-555555", 50m, id: 2));
        }

        return invoices;
    }

    private static List<MatchBankTransactionSnapshot> DiscardedContestTransactions()
    {
        List<MatchBankTransactionSnapshot> transactions =
        [
            Transaction("PAY-001234-009999", 100m, id: 900),
        ];

        for (int offset = 0; offset < SmartMatchRules.MaxCandidatesPerInvoice; offset++)
        {
            transactions.Add(Transaction("ALT-555555", 50m, id: 100 + offset));
            transactions.Add(Transaction("ALT-777777", 100m, id: 200 + offset));
        }

        return transactions;
    }

    private static List<MatchInvoiceSnapshot> RepeatedInvoices(
        string invoiceNumber,
        decimal amount,
        int count,
        int firstId,
        CurrencyCode? currency = null)
    {
        List<MatchInvoiceSnapshot> invoices = new(count);

        for (int offset = 0; offset < count; offset++)
        {
            invoices.Add(Invoice(invoiceNumber, amount, currency, id: firstId + offset));
        }

        return invoices;
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