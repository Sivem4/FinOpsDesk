using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.BatchExecutions;

public sealed class BatchExactMatchApplicationTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Preview_uses_the_authenticated_organization_and_does_not_write()
    {
        RecordingCandidateQueries candidates = new(Book(Pair("AAA", 1, 2, 10m)));
        RecordingReviewQueries reviews = new();
        await using ServiceProvider provider = Provider(candidates, reviews, owner: null, store: null, executor: null);
        PreviewBatchExactMatchesHandler handler = provider.GetRequiredService<PreviewBatchExactMatchesHandler>();

        Result<PreviewBatchExactMatchesResponse> result = await handler.Handle(
            new PreviewBatchExactMatchesQuery(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Organization, candidates.OrganizationId);
        Assert.Equal(1, candidates.Calls);
        Assert.Equal(1, reviews.Calls);
        Assert.Equal("exact-1", result.Value.AlgorithmVersion);
        Assert.True(result.Value.InputComplete);
        Assert.Equal(1, result.Value.EligibleCount);
    }

    [Fact]
    public async Task Preview_orders_eligible_pairs_and_pages_after_classification()
    {
        RecordingCandidateQueries candidates = new(Book(
            Pair("BBB", 2, 4, 20m),
            Pair("AAA", 1, 3, 10m)));
        await using ServiceProvider provider = Provider(candidates, new RecordingReviewQueries(), null, null, null);
        PreviewBatchExactMatchesHandler handler = provider.GetRequiredService<PreviewBatchExactMatchesHandler>();

        Result<PreviewBatchExactMatchesResponse> result = await handler.Handle(
            new PreviewBatchExactMatchesQuery(Page: 1, PageSize: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PreviewBatchExactMatchesResponse preview = result.Value!;
        Assert.Equal(2, preview.EligibleCount);
        PreviewEligiblePairResponse only = Assert.Single(preview.EligiblePairs);
        Assert.Equal("AAA", only.NormalizedReference);
        Assert.Equal(30m, Assert.Single(preview.CurrencyTotals).Amount);
        Assert.Equal(2, Assert.Single(preview.CurrencyTotals).PairCount);
    }

    [Fact]
    public async Task Preview_rejects_an_incomplete_book_instead_of_reporting_zero_counts()
    {
        RecordingCandidateQueries candidates = new(
            new ExactMatchCandidateSet([], [], true, false, false));
        RecordingReviewQueries reviews = new();
        await using ServiceProvider provider = Provider(candidates, reviews, null, null, null);
        PreviewBatchExactMatchesHandler handler = provider.GetRequiredService<PreviewBatchExactMatchesHandler>();

        Result<PreviewBatchExactMatchesResponse> result = await handler.Handle(
            new PreviewBatchExactMatchesQuery(),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.BatchCandidateLimitExceeded, result.Error);
        Assert.Equal(0, reviews.Calls);
    }

    [Fact]
    public async Task Execute_rejects_duplicate_and_oversized_requests_before_persistence()
    {
        RecordingStore store = new();
        await using ServiceProvider provider = Provider(
            new RecordingCandidateQueries(Book(Pair("AAA", 1, 2, 10m))),
            new RecordingReviewQueries(),
            new RecordingOwner(store),
            store,
            new RecordingExecutor());
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();
        BatchExactMatchPair pair = RequestPair(1, 2);
        BatchExactMatchPair[] tooMany = new BatchExactMatchPair[26];

        for (int index = 0; index < tooMany.Length; index++)
        {
            tooMany[index] = RequestPair(index + 1, index + 100);
        }

        Result<ExecuteBatchExactMatchesResponse> duplicate = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(Guid.NewGuid(), [pair, pair]),
            CancellationToken.None);
        Result<ExecuteBatchExactMatchesResponse> empty = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(Guid.NewGuid(), []),
            CancellationToken.None);
        Result<ExecuteBatchExactMatchesResponse> oversized = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(Guid.NewGuid(), tooMany),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.BatchInvalidRequest, duplicate.Error);
        Assert.Equal(ReconciliationApplicationErrors.BatchInvalidRequest, empty.Error);
        Assert.Equal(ReconciliationApplicationErrors.BatchInvalidRequest, oversized.Error);
        Assert.Equal(0, store.Inserts);
    }

    [Fact]
    public async Task Execute_uses_the_authenticated_organization_and_canonical_identity()
    {
        RecordingStore store = new();
        RecordingOwner owner = new(store);
        RecordingExecutor executor = new(
            new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.Stale),
            new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.Stale));
        await using ServiceProvider provider = Provider(
            new RecordingCandidateQueries(EmptyBook()),
            new RecordingReviewQueries(),
            owner,
            store,
            executor);
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();
        Guid key = Guid.NewGuid();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(3, 4), RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Organization, store.InsertedOrganization);
        Assert.Equal(
            BatchExactMatchIdentity.Create([RequestPair(1, 2), RequestPair(3, 4)]).Fingerprint.ToArray(),
            store.InsertedFingerprint);
        Assert.Equal(RequestPair(1, 2).InvoiceId, executor.Invoices[0]);
        Assert.Equal(RequestPair(3, 4).InvoiceId, executor.Invoices[1]);
        Assert.True(owner.Completed);
    }

    [Fact]
    public async Task Completed_replay_returns_the_stored_result_without_financial_work()
    {
        BatchExecutionResult storedResult = BatchExecutionResult.FromItems(
        [
            new BatchExecutionItem(
                RequestPair(1, 2).InvoiceId,
                RequestPair(1, 2).BankTransactionId,
                BatchExecutionItemOutcome.Confirmed,
                new AllocationId(Guid.NewGuid()),
                new Money(10m, Pln))
        ]);
        RecordingStore store = new();
        store.SeedCompleted(
            Organization,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            BatchExactMatchIdentity.Create([RequestPair(1, 2)]).Fingerprint,
            BatchExecutionResultDocument.Write(storedResult));
        RecordingCandidateQueries candidates = new(EmptyBook());
        RecordingExecutor executor = new();
        RecordingLookup lookup = new();
        RecordingOwner owner = new(store);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            owner,
            store,
            executor,
            lookup);
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                [RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AllConfirmed);
        Assert.Equal(storedResult.Items[0].AllocationId!.Value, result.Value.Items[0].AllocationId);
        Assert.Equal(0, candidates.Calls);
        Assert.Equal(0, lookup.Calls);
        Assert.Equal(0, owner.Acquired);
        Assert.Empty(executor.Invoices);
    }

    [Fact]
    public async Task Expected_rejection_continues_and_infrastructure_failure_does_not_complete()
    {
        RecordingStore store = new();
        RecordingOwner owner = new(store);
        RecordingExecutor executor = new(
            new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.Stale),
            new BatchItemAttempt.Confirmed(new AllocationId(Guid.NewGuid()), new Money(10m, Pln)));
        await using ServiceProvider provider = Provider(
            new RecordingCandidateQueries(EmptyBook()),
            new RecordingReviewQueries(),
            owner,
            store,
            executor);
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();
        Guid key = Guid.NewGuid();

        Result<ExecuteBatchExactMatchesResponse> mixed = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(1, 2), RequestPair(3, 4)]),
            CancellationToken.None);

        Assert.True(mixed.IsSuccess);
        ExecuteBatchExactMatchesResponse mixedValue = mixed.Value!;
        Assert.True(mixedValue.Partial);
        Assert.False(mixedValue.AllConfirmed);
        Assert.Equal("stale", mixedValue.Items[0].Outcome);
        Assert.Equal("confirmed", mixedValue.Items[1].Outcome);
        Assert.True(owner.Completed);

        RecordingStore stoppedStore = new();
        RecordingOwner stoppedOwner = new(stoppedStore);
        RecordingExecutor failing = new();
        failing.ThrowOnCall = 1;
        await using ServiceProvider stoppedProvider = Provider(
            new RecordingCandidateQueries(EmptyBook()),
            new RecordingReviewQueries(),
            stoppedOwner,
            stoppedStore,
            failing);
        ExecuteBatchExactMatchesHandler stopped = stoppedProvider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> failure = await stopped.Handle(
            new ExecuteBatchExactMatchesCommand(Guid.NewGuid(), [RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.BatchInfrastructureFailure, failure.Error);
        Assert.False(stoppedOwner.Completed);
    }

    [Fact]
    public async Task Recovered_item_does_not_execute_again_and_cancellation_propagates()
    {
        AllocationId allocationId = new(Guid.NewGuid());
        RecordingLookup lookup = new();
        lookup.Seed(
            BatchExactMatchItemKey.Derive(Organization, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), RequestPair(1, 2).InvoiceId, RequestPair(1, 2).BankTransactionId),
            new ConfirmedAllocationRecord(
                allocationId,
                RequestPair(1, 2).InvoiceId,
                RequestPair(1, 2).BankTransactionId,
                new Money(10m, Pln),
                AllocationIdempotencyOperation.BatchExactMatch));
        RecordingExecutor executor = new();
        RecordingStore store = new();
        RecordingCandidateQueries candidates = new(InconsistentBook());
        store.SeedRunning(
            Organization,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            BatchExactMatchIdentity.Create([RequestPair(1, 2)]).Fingerprint);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            new RecordingOwner(store),
            store,
            executor,
            lookup);
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> recovered = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                [RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(allocationId.Value, recovered.Value!.Items[0].AllocationId);
        Assert.Equal(0, candidates.Calls);
        Assert.Empty(executor.Invoices);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.Handle(
                new ExecuteBatchExactMatchesCommand(Guid.NewGuid(), [RequestPair(5, 6)]),
                cancellation.Token));
    }

    [Fact]
    public async Task Recovered_items_are_classified_before_the_candidate_gate()
    {
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        RecordingLookup lookup = new();
        SeedAllocation(lookup, key, RequestPair(1, 2), AllocationIdempotencyOperation.BatchExactMatch);
        RecordingCandidateQueries candidates = new(InconsistentBook());
        candidates.BeforeQuery = () => Assert.Equal(2, lookup.Calls);
        RecordingExecutor executor = new();
        RecordingStore store = new();
        store.SeedRunning(
            Organization,
            key,
            BatchExactMatchIdentity.Create([RequestPair(1, 2), RequestPair(3, 4)]).Fingerprint,
            createdAt);
        RecordingOwner owner = new(store);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            owner,
            store,
            executor,
            lookup,
            new FixedTime(createdAt.AddMinutes(1)));
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(3, 4), RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.InconsistentMatchMoney, result.Error);
        Assert.Equal(1, candidates.Calls);
        Assert.Empty(executor.Invoices);
        Assert.False(owner.Completed);
    }

    [Fact]
    public async Task All_recovered_batch_completes_without_reading_the_candidate_book()
    {
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        RecordingLookup lookup = new();
        AllocationId first = SeedAllocation(
            lookup,
            key,
            RequestPair(1, 2),
            AllocationIdempotencyOperation.BatchExactMatch);
        AllocationId second = SeedAllocation(
            lookup,
            key,
            RequestPair(3, 4),
            AllocationIdempotencyOperation.BatchExactMatch);
        RecordingCandidateQueries candidates = new(InconsistentBook());
        RecordingExecutor executor = new();
        RecordingStore store = new();
        store.SeedRunning(
            Organization,
            key,
            BatchExactMatchIdentity.Create([RequestPair(1, 2), RequestPair(3, 4)]).Fingerprint,
            createdAt);
        RecordingOwner owner = new(store);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            owner,
            store,
            executor,
            lookup,
            new FixedTime(createdAt.AddMinutes(15)));
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(3, 4), RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ExecuteBatchExactMatchesResponse value = result.Value!;
        Assert.True(value.AllConfirmed);
        Assert.Equal(first.Value, value.Items[0].AllocationId);
        Assert.Equal(10m, value.Items[0].Amount);
        Assert.Equal(second.Value, value.Items[1].AllocationId);
        Assert.Equal(0, candidates.Calls);
        Assert.Empty(executor.Invoices);
        Assert.True(owner.Completed);
    }

    [Fact]
    public async Task Expired_batch_interrupts_uncommitted_items_without_reading_the_candidate_book()
    {
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        RecordingLookup lookup = new();
        AllocationId allocationId = SeedAllocation(
            lookup,
            key,
            RequestPair(1, 2),
            AllocationIdempotencyOperation.BatchExactMatch);
        RecordingCandidateQueries candidates = new(InconsistentBook());
        RecordingExecutor executor = new();
        RecordingStore store = new();
        store.SeedRunning(
            Organization,
            key,
            BatchExactMatchIdentity.Create([RequestPair(1, 2), RequestPair(3, 4)]).Fingerprint,
            createdAt);
        RecordingOwner owner = new(store);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            owner,
            store,
            executor,
            lookup,
            new FixedTime(createdAt.Add(BatchExecutionLimits.MaxResumeAge)));
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(1, 2), RequestPair(3, 4)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ExecuteBatchExactMatchesResponse value = result.Value!;
        Assert.Equal("confirmed", value.Items[0].Outcome);
        Assert.Equal(allocationId.Value, value.Items[0].AllocationId);
        Assert.Equal("interrupted", value.Items[1].Outcome);
        Assert.Equal(0, candidates.Calls);
        Assert.Empty(executor.Invoices);
        Assert.True(owner.Completed);
    }

    [Fact]
    public async Task Expired_identity_mismatch_stops_before_interruption_or_completion()
    {
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        RecordingLookup lookup = new();
        SeedAllocation(lookup, key, RequestPair(1, 2), AllocationIdempotencyOperation.ExactMatch);
        RecordingCandidateQueries candidates = new(InconsistentBook());
        RecordingExecutor executor = new();
        RecordingStore store = new();
        store.SeedRunning(
            Organization,
            key,
            BatchExactMatchIdentity.Create([RequestPair(1, 2)]).Fingerprint,
            createdAt);
        RecordingOwner owner = new(store);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            owner,
            store,
            executor,
            lookup,
            new FixedTime(createdAt.Add(BatchExecutionLimits.MaxResumeAge)));
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(1, 2)]),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.BatchAllocationIntegrityConflict, result.Error);
        Assert.Equal(0, candidates.Calls);
        Assert.Empty(executor.Invoices);
        Assert.False(owner.Completed);
    }

    [Fact]
    public async Task Uncommitted_items_remain_subject_to_current_eligibility()
    {
        Guid key = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        RecordingLookup lookup = new();
        AllocationId allocationId = SeedAllocation(
            lookup,
            key,
            RequestPair(1, 2),
            AllocationIdempotencyOperation.BatchExactMatch);
        RecordingCandidateQueries candidates = new(EmptyBook());
        RecordingExecutor executor = new(new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.Stale));
        RecordingStore store = new();
        store.SeedRunning(
            Organization,
            key,
            BatchExactMatchIdentity.Create([RequestPair(1, 2), RequestPair(3, 4)]).Fingerprint,
            createdAt);
        await using ServiceProvider provider = Provider(
            candidates,
            new RecordingReviewQueries(),
            new RecordingOwner(store),
            store,
            executor,
            lookup,
            new FixedTime(createdAt.AddMinutes(1)));
        ExecuteBatchExactMatchesHandler handler = provider.GetRequiredService<ExecuteBatchExactMatchesHandler>();

        Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
            new ExecuteBatchExactMatchesCommand(key, [RequestPair(1, 2), RequestPair(3, 4)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ExecuteBatchExactMatchesResponse value = result.Value!;
        Assert.Equal("confirmed", value.Items[0].Outcome);
        Assert.Equal(allocationId.Value, value.Items[0].AllocationId);
        Assert.Equal("stale", value.Items[1].Outcome);
        Assert.Equal(1, candidates.Calls);
        Assert.Equal(RequestPair(3, 4).InvoiceId, Assert.Single(executor.Invoices));
    }

    [Fact]
    public void Resume_window_is_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), BatchExecutionLimits.MaxResumeAge);
    }

    private static ServiceProvider Provider(
        RecordingCandidateQueries candidates,
        RecordingReviewQueries reviews,
        RecordingOwner? owner,
        RecordingStore? store,
        RecordingExecutor? executor,
        RecordingLookup? lookup = null,
        TimeProvider? time = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<ICurrentOrganizationContext>(new FixedOrganization(Organization));
        services.AddSingleton<IExactMatchCandidateQueries>(candidates);
        services.AddSingleton<IOpenReviewBlockQueries>(reviews);
        services.AddSingleton<IAllocationIdempotencyLookup>(lookup ?? new RecordingLookup());
        services.AddSingleton<IBatchExecutionOwner>(owner ?? new RecordingOwner(store ?? new RecordingStore()));
        services.AddSingleton<IBatchExecutionStore>(store ?? new RecordingStore());
        services.AddSingleton<IBatchExactMatchItemExecutor>(executor ?? new RecordingExecutor());
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSingleton<PreviewBatchExactMatchesHandler>();
        services.AddSingleton<ExecuteBatchExactMatchesHandler>();
        return services.BuildServiceProvider();
    }

    private static AllocationId SeedAllocation(
        RecordingLookup lookup,
        Guid key,
        BatchExactMatchPair pair,
        AllocationIdempotencyOperation operation)
    {
        AllocationId allocationId = new(Guid.NewGuid());
        lookup.Seed(
            BatchExactMatchItemKey.Derive(Organization, key, pair.InvoiceId, pair.BankTransactionId),
            new ConfirmedAllocationRecord(
                allocationId,
                pair.InvoiceId,
                pair.BankTransactionId,
                new Money(10m, Pln),
                operation));
        return allocationId;
    }

    private static ExactMatchCandidateSet Book(params (MatchInvoiceSnapshot Invoice, MatchBankTransactionSnapshot Bank)[] pairs)
    {
        return new ExactMatchCandidateSet(
            pairs.Select(pair => pair.Invoice).ToArray(),
            pairs.Select(pair => pair.Bank).ToArray(),
            false,
            false,
            false);
    }

    private static ExactMatchCandidateSet EmptyBook()
    {
        return new ExactMatchCandidateSet([], [], false, false, false);
    }

    private static ExactMatchCandidateSet InconsistentBook()
    {
        return new ExactMatchCandidateSet([], [], false, false, true);
    }

    private static (MatchInvoiceSnapshot Invoice, MatchBankTransactionSnapshot Bank) Pair(
        string reference,
        int invoice,
        int bank,
        decimal amount)
    {
        return (
            new MatchInvoiceSnapshot(Organization, new InvoiceId(GuidOf(invoice)), reference, new Money(amount, Pln)),
            new MatchBankTransactionSnapshot(Organization, new BankTransactionId(GuidOf(bank)), reference, new Money(amount, Pln)));
    }

    private static BatchExactMatchPair RequestPair(int invoice, int bank)
    {
        return new BatchExactMatchPair(new InvoiceId(GuidOf(invoice)), new BankTransactionId(GuidOf(bank)));
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }

    private sealed class FixedOrganization(OrganizationId organizationId) : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId { get; } = organizationId;
    }

    private sealed class RecordingCandidateQueries(ExactMatchCandidateSet set) : IExactMatchCandidateQueries
    {
        public int Calls { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public Action? BeforeQuery { get; set; }

        public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            Calls++;
            OrganizationId = organizationId;
            BeforeQuery?.Invoke();
            return Task.FromResult(set);
        }
    }

    private sealed class RecordingReviewQueries : IOpenReviewBlockQueries
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReviewBlockSubject>> ListOpenAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ReviewBlockSubject>>([]);
        }

        public Task<IReadOnlyList<ReviewBlockSubject>> ListOpenForPairAsync(
            OrganizationId organizationId,
            InvoiceId invoiceId,
            BankTransactionId bankTransactionId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ReviewBlockSubject>>([]);
        }
    }

    private sealed class RecordingLookup : IAllocationIdempotencyLookup
    {
        private readonly Dictionary<Guid, ConfirmedAllocationRecord> _records = [];

        public int Calls { get; private set; }

        public void Seed(Guid key, ConfirmedAllocationRecord record)
        {
            _records[key] = record;
        }

        public Task<ConfirmedAllocationRecord?> FindAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            _records.TryGetValue(idempotencyKey, out ConfirmedAllocationRecord? record);
            return Task.FromResult(record);
        }
    }

    private sealed class RecordingStore : IBatchExecutionStore
    {
        private readonly Dictionary<(Guid Organization, Guid Key), BatchExecutionSnapshot> _rows = [];

        public int Inserts { get; private set; }

        public OrganizationId? InsertedOrganization { get; private set; }

        public byte[]? InsertedFingerprint { get; private set; }

        public void SeedCompleted(
            OrganizationId organizationId,
            Guid key,
            BatchExecutionFingerprint fingerprint,
            string payload)
        {
            _rows[(organizationId.Value, key)] = Snapshot(
                organizationId,
                key,
                fingerprint,
                BatchExecutionStatus.Completed,
                payload,
                Now);
        }

        public void SeedRunning(
            OrganizationId organizationId,
            Guid key,
            BatchExecutionFingerprint fingerprint,
            DateTimeOffset? createdAt = null)
        {
            _rows[(organizationId.Value, key)] = Snapshot(
                organizationId,
                key,
                fingerprint,
                BatchExecutionStatus.Running,
                null,
                createdAt ?? Now);
        }

        public Task<BatchExecutionSnapshot?> FindAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            _rows.TryGetValue((organizationId.Value, idempotencyKey), out BatchExecutionSnapshot? snapshot);
            return Task.FromResult(snapshot);
        }

        public Task AddRunningAsync(BatchExecution execution, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void Complete(Guid organizationId, Guid key, string payload, DateTimeOffset completedAt)
        {
            (Guid, Guid) rowKey = (organizationId, key);
            BatchExecutionSnapshot current = _rows[rowKey];
            _rows[rowKey] = new BatchExecutionSnapshot(
                current.Id,
                current.OrganizationId,
                current.IdempotencyKey,
                current.Fingerprint,
                BatchExecutionStatus.Completed,
                1,
                payload,
                current.CreatedAt,
                completedAt);
        }

        public Task<bool> TryAddRunningAsync(BatchExecution execution, CancellationToken cancellationToken)
        {
            Inserts++;
            InsertedOrganization = execution.OrganizationId;
            InsertedFingerprint = execution.Fingerprint.ToArray();
            (Guid, Guid) key = (execution.OrganizationId.Value, execution.IdempotencyKey);

            if (_rows.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            _rows[key] = Snapshot(
                execution.OrganizationId,
                execution.IdempotencyKey,
                execution.Fingerprint,
                BatchExecutionStatus.Running,
                null,
                execution.CreatedAt);
            return Task.FromResult(true);
        }

        public Task<bool> TryCompleteAsync(
            BatchExecutionId id,
            OrganizationId organizationId,
            BatchExecutionResult result,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        private static BatchExecutionSnapshot Snapshot(
            OrganizationId organizationId,
            Guid key,
            BatchExecutionFingerprint fingerprint,
            BatchExecutionStatus status,
            string? payload,
            DateTimeOffset createdAt)
        {
            return new BatchExecutionSnapshot(
                BatchExecutionId.New(),
                organizationId,
                key,
                fingerprint,
                status,
                status == BatchExecutionStatus.Completed ? 1 : null,
                payload,
                createdAt,
                status == BatchExecutionStatus.Completed ? createdAt : null);
        }
    }

    private sealed class RecordingOwner(RecordingStore store) : IBatchExecutionOwner
    {
        public bool Completed { get; private set; }

        public int Acquired { get; private set; }

        public async Task<Result<IBatchExecutionLease>> TryAcquireAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Acquired++;
            BatchExecutionSnapshot? snapshot = await store.FindAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);
            return Result<IBatchExecutionLease>.Success(new Lease(this, store, snapshot!));
        }

        private sealed class Lease(
            RecordingOwner owner,
            RecordingStore store,
            BatchExecutionSnapshot snapshot) : IBatchExecutionLease
        {
            public BatchExecutionSnapshot Snapshot { get; } = snapshot;

            public int OwnerSessionId => 1;

            public Task<bool> TryCompleteAsync(
                BatchExecutionResult result,
                DateTimeOffset completedAt,
                CancellationToken cancellationToken)
            {
                owner.Completed = true;
                store.Complete(
                    Snapshot.OrganizationId.Value,
                    Snapshot.IdempotencyKey,
                    BatchExecutionResultDocument.Write(result),
                    completedAt);
                return Task.FromResult(true);
            }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingExecutor : IBatchExactMatchItemExecutor
    {
        private readonly Queue<BatchItemAttempt> _attempts = new();

        public RecordingExecutor(params BatchItemAttempt[] attempts)
        {
            foreach (BatchItemAttempt attempt in attempts)
            {
                _attempts.Enqueue(attempt);
            }
        }

        public List<InvoiceId> Invoices { get; } = [];

        public int ThrowOnCall { get; set; }

        public Task<BatchItemAttempt> ExecuteAsync(
            OrganizationId organizationId,
            Guid batchIdempotencyKey,
            InvoiceId invoiceId,
            BankTransactionId bankTransactionId,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken)
        {
            Invoices.Add(invoiceId);

            if (Invoices.Count == ThrowOnCall)
            {
                throw new InvalidOperationException("forced infrastructure failure");
            }

            return Task.FromResult(_attempts.Dequeue());
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}