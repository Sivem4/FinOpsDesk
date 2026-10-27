using System.Security.Cryptography;
using System.Text;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.BatchExecutions;

public sealed class BatchExecutionReceiptTests
{
    private static readonly OrganizationId Organization =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly CurrencyCode Pln = new("PLN");

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_creates_a_running_receipt_without_a_result()
    {
        BatchExecution execution = Start();

        Assert.Equal(BatchExecutionStatus.Running, execution.Status);
        Assert.Null(execution.ResultVersion);
        Assert.Null(execution.ResultPayload);
        Assert.Null(execution.CompletedAt);
        Assert.Equal(CreatedAt, execution.CreatedAt);
    }

    [Fact]
    public void Complete_stores_a_stable_version_1_payload()
    {
        BatchExecution execution = Start();
        BatchExecutionResult result = BatchExecutionResult.FromItems([ConfirmedItem(1, 2)]);
        DateTimeOffset completedAt = CreatedAt.AddMinutes(1);

        execution.Complete(result, completedAt);
        string payload = execution.ResultPayload!;
        BatchExecutionResult read = BatchExecutionResultDocument.Read(payload);

        Assert.Equal(BatchExecutionStatus.Completed, execution.Status);
        Assert.Equal(1, execution.ResultVersion);
        Assert.Equal(completedAt, execution.CompletedAt);
        Assert.Equal(payload, BatchExecutionResultDocument.Write(result));
        Assert.Equal(payload, BatchExecutionResultDocument.Write(read));
        Assert.Equal(1, read.RequestedCount);
        Assert.Equal("confirmed", PayloadOutcome(payload));
    }

    [Fact]
    public void Complete_rejects_a_second_completion_without_changing_the_stored_result()
    {
        BatchExecution execution = Start();
        DateTimeOffset completedAt = CreatedAt.AddMinutes(1);
        execution.Complete(BatchExecutionResult.FromItems([ConfirmedItem(1, 2)]), completedAt);
        string payload = execution.ResultPayload!;

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(
                BatchExecutionResult.FromItems([RejectedItem(3, 4, BatchExecutionItemOutcome.Stale)]),
                completedAt.AddMinutes(5)));

        Assert.Equal(payload, execution.ResultPayload);
        Assert.Equal(completedAt, execution.CompletedAt);
        Assert.Equal(1, execution.ResultVersion);
    }

    [Fact]
    public void Complete_rejects_a_completion_time_before_creation()
    {
        BatchExecution execution = Start();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execution.Complete(
                BatchExecutionResult.FromItems([ConfirmedItem(1, 2)]),
                CreatedAt.AddMinutes(-1)));

        Assert.Equal(BatchExecutionStatus.Running, execution.Status);
        Assert.Null(execution.ResultPayload);
        Assert.Null(execution.CompletedAt);
    }

    [Fact]
    public void Undefined_status_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BatchExecutionStatusCodec.ToStatus(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BatchExecutionStatusCodec.ToStatus(9));
        Assert.Equal(1, BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Running));
        Assert.Equal(2, BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Completed));
    }

    [Fact]
    public void Result_rejects_counts_that_do_not_match_the_items()
    {
        BatchExecutionItem[] items = [ConfirmedItem(1, 2)];

        Assert.Throws<ArgumentException>(() =>
            BatchExecutionResult.FromDocument(
                1,
                requestedCount: 1,
                confirmedCount: 0,
                rejectedCount: 1,
                interruptedCount: 0,
                allConfirmed: false,
                hasRejections: true,
                partial: false,
                items));
    }

    [Fact]
    public void Result_accepts_an_all_confirmed_response()
    {
        BatchExecutionResult result = BatchExecutionResult.FromItems(
            [ConfirmedItem(1, 2), ConfirmedItem(3, 4)]);

        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.ConfirmedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(0, result.InterruptedCount);
        Assert.True(result.AllConfirmed);
        Assert.False(result.HasRejections);
        Assert.False(result.Partial);
    }

    [Fact]
    public void Result_accepts_a_mixed_confirmed_and_rejected_response()
    {
        BatchExecutionResult result = BatchExecutionResult.FromItems(
        [
            ConfirmedItem(1, 2),
            RejectedItem(3, 4, BatchExecutionItemOutcome.SkippedReview)
        ]);

        Assert.Equal(1, result.ConfirmedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(0, result.InterruptedCount);
        Assert.False(result.AllConfirmed);
        Assert.True(result.HasRejections);
        Assert.True(result.Partial);
    }

    [Fact]
    public void Result_accepts_twenty_five_rejected_items()
    {
        BatchExecutionItem[] items = new BatchExecutionItem[25];

        for (int index = 0; index < items.Length; index++)
        {
            items[index] = RejectedItem(index + 1, index + 100, BatchExecutionItemOutcome.SkippedAmbiguous);
        }

        BatchExecutionResult result = BatchExecutionResult.FromItems(items);

        Assert.Equal(25, result.RequestedCount);
        Assert.Equal(0, result.ConfirmedCount);
        Assert.Equal(25, result.RejectedCount);
        Assert.False(result.AllConfirmed);
        Assert.True(result.HasRejections);
        Assert.False(result.Partial);
    }

    [Fact]
    public void Result_counts_interrupted_items_as_rejections_without_a_partial_success()
    {
        BatchExecutionResult onlyInterrupted = BatchExecutionResult.FromItems(
            [RejectedItem(1, 2, BatchExecutionItemOutcome.Interrupted)]);
        BatchExecutionResult mixed = BatchExecutionResult.FromItems(
        [
            ConfirmedItem(1, 2),
            RejectedItem(3, 4, BatchExecutionItemOutcome.Interrupted)
        ]);

        Assert.Equal(1, onlyInterrupted.InterruptedCount);
        Assert.Equal(0, onlyInterrupted.RejectedCount);
        Assert.True(onlyInterrupted.HasRejections);
        Assert.False(onlyInterrupted.Partial);
        Assert.False(onlyInterrupted.AllConfirmed);
        Assert.True(mixed.Partial);
        Assert.Equal(1, mixed.InterruptedCount);
    }

    [Fact]
    public void Result_rejects_duplicate_pairs_and_pairs_outside_canonical_order()
    {
        BatchExecutionItem first = RejectedItem(1, 2, BatchExecutionItemOutcome.NotFound);

        Assert.Throws<ArgumentException>(() =>
            BatchExecutionResult.FromItems([first, first]));
        Assert.Throws<ArgumentException>(() =>
            BatchExecutionResult.FromItems(
            [
                RejectedItem(3, 4, BatchExecutionItemOutcome.Conflict),
                first
            ]));
    }

    [Fact]
    public void Identity_accepts_one_pair_and_twenty_five_pairs()
    {
        BatchExactMatchIdentity one = BatchExactMatchIdentity.Create([Pair(1, 2)]);
        BatchExactMatchPair[] pairs = new BatchExactMatchPair[25];

        for (int index = 0; index < pairs.Length; index++)
        {
            pairs[index] = Pair(index + 1, index + 50);
        }

        BatchExactMatchIdentity full = BatchExactMatchIdentity.Create(pairs);

        Assert.Single(one.Pairs);
        Assert.Equal(25, full.Pairs.Count);
        Assert.Equal(32, one.Fingerprint.ToArray().Length);
        Assert.Equal(32, full.Fingerprint.ToArray().Length);
    }

    [Fact]
    public void Identity_rejects_an_empty_set_and_twenty_six_pairs()
    {
        Assert.Throws<ArgumentException>(() => BatchExactMatchIdentity.Create([]));

        BatchExactMatchPair[] pairs = new BatchExactMatchPair[26];

        for (int index = 0; index < pairs.Length; index++)
        {
            pairs[index] = Pair(index + 1, index + 50);
        }

        Assert.Throws<ArgumentException>(() => BatchExactMatchIdentity.Create(pairs));
    }

    [Fact]
    public void Empty_identifiers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new InvoiceId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new BankTransactionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new BatchExecutionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() =>
            BatchExactMatchItemKey.Derive(
                Organization,
                Guid.Empty,
                new InvoiceId(GuidOf(1)),
                new BankTransactionId(GuidOf(2))));
    }

    [Fact]
    public void Identity_orders_pairs_by_invoice_then_bank_guid()
    {
        BatchExactMatchPair later = Pair(11, 1);
        BatchExactMatchPair earlier = Pair(2, 9);
        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create([later, earlier]);

        Assert.Equal(
            [earlier.InvoiceId, later.InvoiceId],
            identity.Pairs.Select(pair => pair.InvoiceId));
    }

    [Fact]
    public void Reordered_requests_have_the_same_fingerprint()
    {
        BatchExactMatchPair first = Pair(1, 2);
        BatchExactMatchPair second = Pair(3, 4);
        BatchExactMatchIdentity forward = BatchExactMatchIdentity.Create([first, second]);
        BatchExactMatchIdentity reversed = BatchExactMatchIdentity.Create([second, first]);

        Assert.Equal(forward.Fingerprint.ToArray(), reversed.Fingerprint.ToArray());
        Assert.Equal(
            forward.Pairs.Select(IdentityText),
            reversed.Pairs.Select(IdentityText));
    }

    [Fact]
    public void Different_pair_sets_have_different_fingerprints()
    {
        byte[] first = BatchExactMatchIdentity.Create([Pair(1, 2)]).Fingerprint.ToArray();
        byte[] second = BatchExactMatchIdentity.Create([Pair(1, 3)]).Fingerprint.ToArray();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fingerprint_matches_the_approved_sha256_vectors()
    {
        const string onePair =
            "batch-exact-match-pairs/v1\n"
            + "11111111-1111-1111-1111-111111111111|aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\n";
        const string twoPairs =
            "batch-exact-match-pairs/v1\n"
            + "11111111-1111-1111-1111-111111111111|aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\n"
            + "22222222-2222-2222-2222-222222222222|bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\n";
        BatchExactMatchIdentity one = BatchExactMatchIdentity.Create(
        [
            new BatchExactMatchPair(
                new InvoiceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new BankTransactionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")))
        ]);
        BatchExactMatchIdentity reversed = BatchExactMatchIdentity.Create(
        [
            new BatchExactMatchPair(
                new InvoiceId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                new BankTransactionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            new BatchExactMatchPair(
                new InvoiceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new BankTransactionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")))
        ]);

        Assert.Equal(
            "da552ad4369dc577ec9da70db2f97e7211dcf6a2462b0f118677f2822b11dfee",
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(onePair))));
        Assert.Equal(
            "dd4ba5c114d7f99035e6a94c0361958d1fd9e0a77ec3b789e95ee4800e6c7ebb",
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(twoPairs))));
        Assert.Equal(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(onePair))), Hex(one.Fingerprint.ToArray()));
        Assert.Equal(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(twoPairs))), Hex(reversed.Fingerprint.ToArray()));
    }

    [Fact]
    public void Item_key_is_deterministic_and_changes_with_the_pair()
    {
        const string canonical =
            "batch-exact-match-item/v1\n"
            + "11111111-1111-1111-1111-111111111111\n"
            + "22222222-2222-2222-2222-222222222222\n"
            + "33333333-3333-3333-3333-333333333333\n"
            + "44444444-4444-4444-4444-444444444444\n";
        Guid expected = Guid.Parse("57462eb7-048f-8509-8294-e7d76069e79a");
        OrganizationId organization = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Guid batchKey = Guid.Parse("22222222-2222-2222-2222-222222222222");
        InvoiceId invoiceId = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        BankTransactionId bankTransactionId = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        string hex = Hex(hash.AsSpan(0, 16).ToArray());
        Guid independent = Guid.ParseExact(
            hex[..8] + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex[20..],
            "D");

        Guid derived = BatchExactMatchItemKey.Derive(
            organization,
            batchKey,
            invoiceId,
            bankTransactionId);
        Guid repeated = BatchExactMatchItemKey.Derive(
            organization,
            batchKey,
            invoiceId,
            bankTransactionId);
        Guid otherBank = BatchExactMatchItemKey.Derive(
            organization,
            batchKey,
            invoiceId,
            new BankTransactionId(Guid.Parse("55555555-5555-5555-5555-555555555555")));

        Assert.Equal(expected, independent);
        Assert.Equal(expected, derived);
        Assert.Equal(derived, repeated);
        Assert.NotEqual(derived, otherBank);
    }

    [Fact]
    public void Receipt_operations_do_not_mutate_financial_aggregates()
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

        BatchExactMatchIdentity identity = BatchExactMatchIdentity.Create(
            [new BatchExactMatchPair(invoice.Id, bankTransaction.Id)]);
        _ = BatchExactMatchItemKey.Derive(
            Organization,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            invoice.Id,
            bankTransaction.Id);
        BatchExecution execution = BatchExecution.Start(
            BatchExecutionId.New(),
            Organization,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            identity.Fingerprint,
            CreatedAt);
        execution.Complete(
            BatchExecutionResult.FromItems([ConfirmedItem(1, 2)]),
            CreatedAt.AddMinutes(1));

        Assert.Equal(invoiceAllocated, invoice.AllocatedAmount);
        Assert.Equal(bankAllocated, bankTransaction.AllocatedAmount);
        Assert.Equal(new Money(100m, Pln), invoice.OutstandingAmount);
        Assert.Equal(new Money(100m, Pln), bankTransaction.UnappliedAmount);
    }

    [Fact]
    public void Complete_accepts_the_same_single_pair_that_started_the_receipt()
    {
        BatchExecution execution = StartFor(Pair(1, 2));

        execution.Complete(Result(ConfirmedItem(1, 2)), CreatedAt.AddMinutes(1));

        Assert.Equal(BatchExecutionStatus.Completed, execution.Status);
        Assert.Equal(1, execution.ResultVersion);
        Assert.NotNull(execution.ResultPayload);
    }

    [Fact]
    public void Complete_accepts_the_original_pairs_in_canonical_order()
    {
        BatchExecution execution = StartFor(Pair(1, 2), Pair(3, 4));
        BatchExecutionResult result = Result(
            ConfirmedItem(1, 2),
            ConfirmedItem(3, 4));

        execution.Complete(result, CreatedAt.AddMinutes(1));

        Assert.Equal(BatchExecutionStatus.Completed, execution.Status);
        Assert.Equal(
            ["00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000003"],
            result.Items.Select(item => item.InvoiceId.Value.ToString("D")));
    }

    [Fact]
    public void Complete_rejects_a_substituted_pair()
    {
        BatchExecution execution = StartFor(Pair(1, 2), Pair(3, 4));

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(
                Result(ConfirmedItem(1, 2), ConfirmedItem(5, 6)),
                CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Complete_rejects_a_missing_pair()
    {
        BatchExecution execution = StartFor(Pair(1, 2), Pair(3, 4));

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(Result(ConfirmedItem(1, 2)), CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Complete_rejects_an_additional_pair()
    {
        BatchExecution execution = StartFor(Pair(1, 2));

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(
                Result(ConfirmedItem(1, 2), ConfirmedItem(3, 4)),
                CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Different_outcomes_for_the_same_pairs_keep_the_request_identity()
    {
        BatchExactMatchPair[] pairs = [Pair(1, 2), Pair(3, 4)];
        BatchExecutionFingerprint fingerprint = BatchExactMatchIdentity.Create(pairs).Fingerprint;
        BatchExecution confirmed = StartFor(pairs);
        BatchExecution rejected = StartFor(pairs);
        BatchExecutionResult confirmedResult = Result(ConfirmedItem(1, 2), ConfirmedItem(3, 4));
        BatchExecutionResult rejectedResult = Result(
            RejectedItem(1, 2, BatchExecutionItemOutcome.SkippedReview),
            RejectedItem(3, 4, BatchExecutionItemOutcome.Interrupted));

        confirmed.Complete(confirmedResult, CreatedAt.AddMinutes(1));
        rejected.Complete(rejectedResult, CreatedAt.AddMinutes(2));

        Assert.Equal(fingerprint.ToArray(), confirmed.Fingerprint.ToArray());
        Assert.Equal(fingerprint.ToArray(), rejected.Fingerprint.ToArray());
        Assert.NotEqual(confirmed.ResultPayload, rejected.ResultPayload);
        Assert.Equal(BatchExecutionStatus.Completed, confirmed.Status);
        Assert.Equal(BatchExecutionStatus.Completed, rejected.Status);
    }

    [Fact]
    public void Rejected_completion_leaves_the_running_receipt_unchanged()
    {
        BatchExecution execution = StartFor(Pair(1, 2), Pair(3, 4));

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(
                Result(RejectedItem(1, 2, BatchExecutionItemOutcome.Stale), RejectedItem(5, 6, BatchExecutionItemOutcome.NotFound)),
                CreatedAt.AddMinutes(1)));

        Assert.Equal(BatchExecutionStatus.Running, execution.Status);
        Assert.Null(execution.ResultVersion);
        Assert.Null(execution.ResultPayload);
        Assert.Null(execution.CompletedAt);
    }

    [Fact]
    public void Completed_receipt_rejects_a_second_result_for_the_same_pairs()
    {
        BatchExecution execution = StartFor(Pair(1, 2), Pair(3, 4));
        DateTimeOffset completedAt = CreatedAt.AddMinutes(1);
        execution.Complete(
            Result(
                RejectedItem(1, 2, BatchExecutionItemOutcome.SkippedCoverage),
                RejectedItem(3, 4, BatchExecutionItemOutcome.SkippedAmbiguous)),
            completedAt);
        string payload = execution.ResultPayload!;

        Assert.Throws<InvalidOperationException>(() =>
            execution.Complete(
                Result(ConfirmedItem(1, 2), ConfirmedItem(3, 4)),
                completedAt.AddMinutes(4)));

        Assert.Equal(BatchExecutionStatus.Completed, execution.Status);
        Assert.Equal(payload, execution.ResultPayload);
        Assert.Equal(completedAt, execution.CompletedAt);
        Assert.Equal(1, execution.ResultVersion);
    }

    [Fact]
    public void Non_confirmed_items_cannot_carry_an_allocation()
    {
        Assert.Throws<ArgumentException>(() =>
            new BatchExecutionItem(
                new InvoiceId(GuidOf(1)),
                new BankTransactionId(GuidOf(2)),
                BatchExecutionItemOutcome.Stale,
                new AllocationId(GuidOf(3)),
                new Money(10m, Pln)));
        Assert.Throws<ArgumentException>(() =>
            new BatchExecutionItem(
                new InvoiceId(GuidOf(1)),
                new BankTransactionId(GuidOf(2)),
                BatchExecutionItemOutcome.Confirmed,
                allocationId: null,
                amount: null));
    }

    private static BatchExecution Start()
    {
        return StartFor(Pair(1, 2));
    }

    private static BatchExecution StartFor(params BatchExactMatchPair[] pairs)
    {
        return BatchExecution.Start(
            BatchExecutionId.New(),
            Organization,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BatchExactMatchIdentity.Create(pairs).Fingerprint,
            CreatedAt);
    }

    private static BatchExecutionResult Result(params BatchExecutionItem[] items)
    {
        return BatchExecutionResult.FromItems(items);
    }

    private static BatchExecutionItem ConfirmedItem(int invoiceSuffix, int bankSuffix)
    {
        return new BatchExecutionItem(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)),
            BatchExecutionItemOutcome.Confirmed,
            new AllocationId(GuidOf(invoiceSuffix + 500)),
            new Money(40m, Pln));
    }

    private static BatchExecutionItem RejectedItem(
        int invoiceSuffix,
        int bankSuffix,
        BatchExecutionItemOutcome outcome)
    {
        return new BatchExecutionItem(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)),
            outcome,
            allocationId: null,
            amount: null);
    }

    private static BatchExactMatchPair Pair(int invoiceSuffix, int bankSuffix)
    {
        return new BatchExactMatchPair(
            new InvoiceId(GuidOf(invoiceSuffix)),
            new BankTransactionId(GuidOf(bankSuffix)));
    }

    private static string IdentityText(BatchExactMatchPair pair)
    {
        return pair.InvoiceId.Value.ToString("D") + "|" + pair.BankTransactionId.Value.ToString("D");
    }

    private static string PayloadOutcome(string payload)
    {
        int start = payload.IndexOf("\"outcome\":\"", StringComparison.Ordinal) + "\"outcome\":\"".Length;
        int end = payload.IndexOf('"', start);
        return payload[start..end];
    }

    private static string Hex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }
}