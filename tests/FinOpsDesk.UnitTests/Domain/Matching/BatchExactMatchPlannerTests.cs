using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class BatchExactMatchPlannerTests
{
    private static readonly OrganizationId Organization =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly CurrencyCode Usd = new("USD");

    [Fact]
    public void Plan_should_keep_one_unique_exact_pair()
    {
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("inv-001", 100m, id: 2)]);

        BatchExactMatchPlan plan = Plan(classification);

        ExactMatch pair = Assert.Single(plan.EligiblePairs);
        Assert.Equal(ExactMatchRules.AlgorithmVersion, plan.AlgorithmVersion);
        Assert.True(plan.InputComplete);
        Assert.Equal(1, plan.EligibleCount);
        Assert.Equal(0, plan.ExcludedByReviewCount);
        Assert.Equal(0, plan.AmbiguousGroupCount);
        Assert.Equal(0, plan.UnmatchedCount);
        Assert.Equal(0, plan.IneligibleCount);
        Assert.Equal(new InvoiceId(GuidOf(1)), pair.InvoiceId);
        Assert.Equal(new BankTransactionId(GuidOf(2)), pair.BankTransactionId);
        Assert.Equal(new Money(100m, Pln), pair.Amount);
        Assert.Equal("INV-001", pair.NormalizedReference);
    }

    [Fact]
    public void Plan_should_keep_independent_exact_pairs_in_exact_1_order()
    {
        ExactMatchBatchResult classification = Match(
            [
                Invoice("ref-b", 10m, id: 3),
                Invoice("ref-a", 5m, Usd, id: 2),
                Invoice("ref-a", 50m, Eur, id: 1)
            ],
            [
                Transaction("REF-B", 10m, id: 30),
                Transaction("REF-A", 5m, Usd, id: 20),
                Transaction("REF-A", 50m, Eur, id: 10)
            ]);

        BatchExactMatchPlan plan = Plan(classification);

        Assert.Equal(3, plan.EligibleCount);
        Assert.Equal(
            classification.ExactMatches.Select(match => match.InvoiceId),
            plan.EligiblePairs.Select(match => match.InvoiceId));
        Assert.Equal(
            ["REF-A", "REF-A", "REF-B"],
            plan.EligiblePairs.Select(match => match.NormalizedReference));
        Assert.Equal(
            [Eur, Usd, Pln],
            plan.EligiblePairs.Select(match => match.Amount.Currency));
    }

    [Fact]
    public void Plan_should_keep_a_pair_whose_remaining_balances_match()
    {
        Invoice invoice = new(
            new InvoiceId(GuidOf(1)),
            Organization,
            "INV-040",
            new Money(100m, Pln));
        BankTransaction bankTransaction = new(
            new BankTransactionId(GuidOf(2)),
            Organization,
            new Money(40m, Pln),
            new DateOnly(2026, 10, 27),
            "INV-040");
        Assert.True(invoice.Allocate(new Money(60m, Pln)).IsSuccess);

        ExactMatchBatchResult classification = Match(
            [
                new MatchInvoiceSnapshot(
                    Organization,
                    invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.OutstandingAmount)
            ],
            [
                new MatchBankTransactionSnapshot(
                    Organization,
                    bankTransaction.Id,
                    bankTransaction.Reference,
                    bankTransaction.UnappliedAmount)
            ]);

        BatchExactMatchPlan plan = Plan(classification);

        ExactMatch pair = Assert.Single(plan.EligiblePairs);
        Assert.Equal(new Money(40m, Pln), pair.Amount);
        Assert.Equal(new Money(60m, Pln), invoice.AllocatedAmount);
        Assert.Equal(new Money(0m, Pln), bankTransaction.AllocatedAmount);
    }

    [Fact]
    public void Plan_should_exclude_a_currency_mismatch()
    {
        BatchExactMatchPlan plan = Plan(Match(
            [Invoice("INV-001", 100m, Pln, id: 1)],
            [Transaction("INV-001", 100m, Eur, id: 2)]));

        Assert.True(plan.InputComplete);
        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.AmbiguousGroupCount);
        Assert.Equal(2, plan.UnmatchedCount);
        Assert.Equal(0, plan.IneligibleCount);
    }

    [Fact]
    public void Plan_should_exclude_a_reference_mismatch()
    {
        BatchExactMatchPlan plan = Plan(Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-002", 100m, id: 2)]));

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.AmbiguousGroupCount);
        Assert.Equal(2, plan.UnmatchedCount);
        Assert.Equal(0, plan.IneligibleCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_should_exclude_a_non_positive_remaining_amount(int outstanding)
    {
        BatchExactMatchPlan plan = Plan(Match(
            [Invoice("INV-001", outstanding, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]));

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.AmbiguousGroupCount);
        Assert.Equal(1, plan.UnmatchedCount);
        Assert.Equal(1, plan.IneligibleCount);
    }

    [Fact]
    public void Plan_should_exclude_competing_invoices()
    {
        ExactMatchBatchResult classification = Match(
            [
                Invoice("INV-001", 100m, id: 1),
                Invoice("inv-001", 100m, id: 2)
            ],
            [Transaction("INV-001", 100m, id: 3)]);

        BatchExactMatchPlan plan = Plan(classification);

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.ExcludedByReviewCount);
        Assert.Equal(1, plan.AmbiguousGroupCount);
        Assert.Equal(
            MatchReasonCode.CompetingInvoices,
            Assert.Single(classification.AmbiguousGroups).Reason);
    }

    [Fact]
    public void Plan_should_exclude_competing_bank_transactions()
    {
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 80m, id: 1)],
            [
                Transaction("INV-001", 80m, id: 2),
                Transaction("inv-001", 80m, id: 3)
            ]);

        BatchExactMatchPlan plan = Plan(classification);

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(1, plan.AmbiguousGroupCount);
        Assert.Equal(
            MatchReasonCode.CompetingTransactions,
            Assert.Single(classification.AmbiguousGroups).Reason);
    }

    [Fact]
    public void Plan_should_exclude_ambiguity_on_both_sides()
    {
        ExactMatchBatchResult classification = Match(
            [
                Invoice("INV-001", 25m, id: 1),
                Invoice("INV-001", 25m, id: 2)
            ],
            [
                Transaction("INV-001", 25m, id: 3),
                Transaction("INV-001", 25m, id: 4)
            ]);

        BatchExactMatchPlan plan = Plan(classification);

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(1, plan.AmbiguousGroupCount);
        Assert.Equal(
            MatchReasonCode.CompetingInvoicesAndTransactions,
            Assert.Single(classification.AmbiguousGroups).Reason);
    }

    [Fact]
    public void Plan_should_return_the_same_pairs_when_input_order_changes()
    {
        MatchInvoiceSnapshot[] invoices =
        [
            Invoice("INV-002", 20m, id: 2),
            Invoice("INV-001", 10m, id: 1)
        ];
        MatchBankTransactionSnapshot[] bankTransactions =
        [
            Transaction("INV-002", 20m, id: 20),
            Transaction("INV-001", 10m, id: 10)
        ];

        BatchExactMatchPlan forward = Plan(Match(invoices, bankTransactions));
        BatchExactMatchPlan reversed = Plan(Match(
            [invoices[1], invoices[0]],
            [bankTransactions[1], bankTransactions[0]]));

        Assert.Equal(
            forward.EligiblePairs.Select(Identity),
            reversed.EligiblePairs.Select(Identity));
        Assert.Equal(new InvoiceId(GuidOf(1)), forward.EligiblePairs[0].InvoiceId);
        Assert.Equal(new InvoiceId(GuidOf(2)), forward.EligiblePairs[1].InvoiceId);
    }

    [Fact]
    public void Plan_should_order_equal_reference_and_amount_by_guid_d_format()
    {
        ExactMatch laterInvoice = ExactPair(
            invoiceSuffix: 0x0B,
            bankSuffix: 0x02,
            reference: "INV-001",
            amount: 15m);
        ExactMatch earlierInvoice = ExactPair(
            invoiceSuffix: 0x0A,
            bankSuffix: 0x09,
            reference: "INV-001",
            amount: 15m);
        ExactMatchBatchResult byInvoice = new(
            ExactMatchRules.AlgorithmVersion,
            [laterInvoice, earlierInvoice],
            [],
            [],
            []);

        BatchExactMatchPlan invoiceOrder = Plan(byInvoice);

        Assert.Equal(
            [earlierInvoice.InvoiceId, laterInvoice.InvoiceId],
            invoiceOrder.EligiblePairs.Select(match => match.InvoiceId));

        ExactMatch laterBank = ExactPair(
            invoiceSuffix: 0x0A,
            bankSuffix: 0x0B,
            reference: "INV-001",
            amount: 15m);
        ExactMatch earlierBank = ExactPair(
            invoiceSuffix: 0x0A,
            bankSuffix: 0x0A,
            reference: "INV-001",
            amount: 15m);
        ExactMatchBatchResult byBank = new(
            ExactMatchRules.AlgorithmVersion,
            [laterBank, earlierBank],
            [],
            [],
            []);

        BatchExactMatchPlan bankOrder = Plan(byBank);

        Assert.Equal(
            [earlierBank.BankTransactionId, laterBank.BankTransactionId],
            bankOrder.EligiblePairs.Select(match => match.BankTransactionId));
    }

    [Fact]
    public void Plan_should_block_a_pair_named_by_an_open_invoice_review()
    {
        InvoiceId invoiceId = new(GuidOf(1));
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                BankTransactionId: null)
        ]);

        BatchExactMatchPlan plan = Plan(classification, blocks);

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(1, plan.ExcludedByReviewCount);
        Assert.Equal(0, plan.AmbiguousGroupCount);
    }

    [Fact]
    public void Plan_should_block_a_pair_named_by_an_open_bank_review()
    {
        BankTransactionId bankTransactionId = new(GuidOf(2));
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId: null,
                bankTransactionId)
        ]);

        BatchExactMatchPlan plan = Plan(classification, blocks);

        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(1, plan.ExcludedByReviewCount);
    }

    [Fact]
    public void Plan_should_block_either_id_named_by_an_open_competing_match()
    {
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);
        OpenReviewBlockSet invoiceSide = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.CompetingMatch,
                new InvoiceId(GuidOf(1)),
                new BankTransactionId(GuidOf(9)))
        ]);
        OpenReviewBlockSet bankSide = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.CompetingMatch,
                new InvoiceId(GuidOf(9)),
                new BankTransactionId(GuidOf(2)))
        ]);

        Assert.Empty(Plan(classification, invoiceSide).EligiblePairs);
        Assert.Equal(1, Plan(classification, invoiceSide).ExcludedByReviewCount);
        Assert.Empty(Plan(classification, bankSide).EligiblePairs);
        Assert.Equal(1, Plan(classification, bankSide).ExcludedByReviewCount);
    }

    [Fact]
    public void Plan_should_count_overlapping_reviews_as_one_exclusion()
    {
        InvoiceId invoiceId = new(GuidOf(1));
        BankTransactionId bankTransactionId = new(GuidOf(2));
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                BankTransactionId: null),
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.CompetingMatch,
                invoiceId,
                bankTransactionId),
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId: null,
                bankTransactionId)
        ]);
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);

        BatchExactMatchPlan plan = Plan(classification, blocks);

        Assert.Single(blocks.InvoiceIds);
        Assert.Single(blocks.BankTransactionIds);
        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(1, plan.ExcludedByReviewCount);
    }

    [Theory]
    [InlineData(ReviewCaseStatus.Resolved)]
    [InlineData(ReviewCaseStatus.Dismissed)]
    public void Plan_should_ignore_a_terminal_review_case(ReviewCaseStatus status)
    {
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                status,
                ReviewCaseReason.CompetingMatch,
                new InvoiceId(GuidOf(1)),
                new BankTransactionId(GuidOf(2))),
            new ReviewBlockSubject(
                status,
                ReviewCaseReason.NeedsInvestigation,
                new InvoiceId(GuidOf(1)),
                BankTransactionId: null)
        ]);
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);

        BatchExactMatchPlan plan = Plan(classification, blocks);

        Assert.Empty(blocks.InvoiceIds);
        Assert.Empty(blocks.BankTransactionIds);
        Assert.Single(plan.EligiblePairs);
        Assert.Equal(0, plan.ExcludedByReviewCount);
    }

    [Fact]
    public void Plan_should_not_create_an_exact_pair_by_blocking_one_side_of_an_ambiguous_group()
    {
        MatchInvoiceSnapshot invoiceA = Invoice("INV-001", 100m, id: 1);
        MatchInvoiceSnapshot invoiceB = Invoice("inv-001", 100m, id: 2);
        MatchBankTransactionSnapshot bankTransaction = Transaction("INV-001", 100m, id: 3);
        ExactMatchBatchResult withoutBlockedInvoice = Match([invoiceA], [bankTransaction]);
        ExactMatchBatchResult completeBook = Match(
            [invoiceA, invoiceB],
            [bankTransaction]);
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(
        [
            new ReviewBlockSubject(
                ReviewCaseStatus.Open,
                ReviewCaseReason.NeedsInvestigation,
                invoiceB.InvoiceId,
                BankTransactionId: null)
        ]);

        BatchExactMatchPlan plan = Plan(completeBook, blocks);

        Assert.Single(withoutBlockedInvoice.ExactMatches);
        Assert.Empty(completeBook.ExactMatches);
        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.ExcludedByReviewCount);
        Assert.Equal(1, plan.AmbiguousGroupCount);
        Assert.Equal(
            MatchReasonCode.CompetingInvoices,
            Assert.Single(completeBook.AmbiguousGroups).Reason);
    }

    [Fact]
    public void Plan_should_keep_exact_1_duplicate_identity_failure()
    {
        InvoiceId invoiceId = new(GuidOf(1));
        MatchInvoiceSnapshot[] invoices =
        [
            new(Organization, invoiceId, "INV-001", new Money(10m, Pln)),
            new(Organization, invoiceId, "INV-001", new Money(10m, Pln))
        ];

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            ExactMatchRules.Evaluate(
                Organization,
                invoices,
                [Transaction("INV-001", 10m, id: 2)]));

        Assert.DoesNotContain(invoiceId.Value.ToString("D"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_should_return_no_eligible_pairs_when_coverage_is_incomplete()
    {
        ExactMatchBatchResult classification = Match(
            [Invoice("INV-001", 100m, id: 1)],
            [Transaction("INV-001", 100m, id: 2)]);

        BatchExactMatchPlan plan = BatchExactMatchPlanner.Plan(
            classification,
            new HashSet<InvoiceId>(),
            new HashSet<BankTransactionId>(),
            BatchMatchCoverage.Incomplete);

        Assert.NotEmpty(classification.ExactMatches);
        Assert.False(plan.InputComplete);
        Assert.Empty(plan.EligiblePairs);
        Assert.Equal(0, plan.EligibleCount);
        Assert.Equal(0, plan.ExcludedByReviewCount);
        Assert.Equal(0, plan.AmbiguousGroupCount);
        Assert.Equal(0, plan.UnmatchedCount);
        Assert.Equal(0, plan.IneligibleCount);
    }

    [Fact]
    public void Plan_should_not_mutate_financial_aggregates()
    {
        Invoice invoice = new(
            new InvoiceId(GuidOf(1)),
            Organization,
            "INV-001",
            new Money(100m, Pln));
        BankTransaction bankTransaction = new(
            new BankTransactionId(GuidOf(2)),
            Organization,
            new Money(100m, Pln),
            new DateOnly(2026, 10, 27),
            "INV-001");
        Money invoiceAllocated = invoice.AllocatedAmount;
        Money bankAllocated = bankTransaction.AllocatedAmount;
        ExactMatchBatchResult classification = Match(
            [
                new MatchInvoiceSnapshot(
                    Organization,
                    invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.OutstandingAmount)
            ],
            [
                new MatchBankTransactionSnapshot(
                    Organization,
                    bankTransaction.Id,
                    bankTransaction.Reference,
                    bankTransaction.UnappliedAmount)
            ]);

        BatchExactMatchPlan plan = Plan(classification);
        CompetitorProbeOutcome probe = CompetitorProbeRules.Evaluate(
            invoice.InvoiceNumber,
            [new CompetitorProbeRow("OTHER")],
            truncated: false);

        Assert.Equal(CompetitorProbeOutcome.Unique, probe);
        Assert.Single(plan.EligiblePairs);
        Assert.Equal(invoiceAllocated, invoice.AllocatedAmount);
        Assert.Equal(bankAllocated, bankTransaction.AllocatedAmount);
        Assert.Equal(new Money(100m, Pln), invoice.OutstandingAmount);
        Assert.Equal(new Money(100m, Pln), bankTransaction.UnappliedAmount);
    }

    [Fact]
    public void Collect_should_reject_an_open_competing_match_that_names_one_side()
    {
        ReviewBlockSubject[] subjects =
        [
            new(
                ReviewCaseStatus.Open,
                ReviewCaseReason.CompetingMatch,
                new InvoiceId(GuidOf(1)),
                BankTransactionId: null)
        ];

        Assert.Throws<ArgumentException>(() => OpenReviewBlocks.Collect(subjects));
    }

    private static string Identity(ExactMatch match)
    {
        return match.InvoiceId.Value.ToString("D")
            + "|"
            + match.BankTransactionId.Value.ToString("D");
    }

    private static BatchExactMatchPlan Plan(ExactMatchBatchResult classification)
    {
        return Plan(classification, OpenReviewBlocks.Collect([]));
    }

    private static BatchExactMatchPlan Plan(
        ExactMatchBatchResult classification,
        OpenReviewBlockSet blocks)
    {
        return BatchExactMatchPlanner.Plan(
            classification,
            blocks.InvoiceIds,
            blocks.BankTransactionIds,
            BatchMatchCoverage.Complete);
    }

    private static ExactMatch ExactPair(
        int invoiceSuffix,
        int bankSuffix,
        string reference,
        decimal amount)
    {
        return new ExactMatch(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)),
            new Money(amount, Pln),
            reference,
            MatchReasonCode.ExactReferenceCurrencyAndOpenAmount);
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
        int? id = null)
    {
        return new MatchInvoiceSnapshot(
            Organization,
            new InvoiceId(id.HasValue ? GuidOf(id.Value) : Guid.NewGuid()),
            invoiceNumber,
            new Money(outstanding, currency ?? Pln));
    }

    private static MatchBankTransactionSnapshot Transaction(
        string? reference,
        decimal unapplied,
        CurrencyCode? currency = null,
        int? id = null)
    {
        return new MatchBankTransactionSnapshot(
            Organization,
            new BankTransactionId(id.HasValue ? GuidOf(id.Value) : Guid.NewGuid()),
            reference,
            new Money(unapplied, currency ?? Pln));
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }
}