using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Matching;

public sealed class GetExactMatchesHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly CurrencyCode Pln = new("PLN");

    [Fact]
    public async Task Handle_queries_the_trusted_organization_with_the_fixed_limit_and_cancellation_token()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeCandidateQueries queries = new(EmptyCandidates());
        GetExactMatchesHandler handler = CreateHandler(organizationContext, queries);
        using CancellationTokenSource cancellationTokenSource = new();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<ExactMatchResponse> result = await handler.Handle(cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExactMatchRules.AlgorithmVersion, result.Value.AlgorithmVersion);
        Assert.Empty(result.Value.ExactMatches);
        Assert.Empty(result.Value.AmbiguousGroups);
        Assert.Empty(result.Value.Unmatched);
        Assert.Empty(result.Value.Ineligible);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(TestOrganizationId, queries.OrganizationId);
        Assert.Equal(ExactMatchCandidateLimits.MaxPerSide, queries.Limit);
        Assert.Equal(cancellationToken, queries.CancellationToken);
    }

    [Fact]
    public async Task Handle_returns_an_empty_result_when_the_open_book_is_empty()
    {
        FakeCandidateQueries queries = new(EmptyCandidates());
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExactMatchRules.AlgorithmVersion, result.Value.AlgorithmVersion);
        Assert.Empty(result.Value.ExactMatches);
        Assert.Empty(result.Value.AmbiguousGroups);
        Assert.Empty(result.Value.Unmatched);
        Assert.Empty(result.Value.Ineligible);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_returns_the_existing_exact_match_rules_result()
    {
        MatchInvoiceSnapshot exactInvoice = Invoice(1, " inv-001 ", 10.5m);
        MatchInvoiceSnapshot unmatchedInvoice = Invoice(2, "ONLY", 3m);
        MatchBankTransactionSnapshot exactTransaction = Transaction(3, "INV-001", 10.5m);
        MatchBankTransactionSnapshot ineligibleTransaction = Transaction(4, null, 4m);
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [exactInvoice, unmatchedInvoice],
            [exactTransaction, ineligibleTransaction],
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: false));
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExactMatchRules.AlgorithmVersion, result.Value.AlgorithmVersion);
        ExactMatchItemResponse exactMatch = Assert.Single(result.Value.ExactMatches);
        Assert.Equal(exactInvoice.InvoiceId.Value, exactMatch.InvoiceId);
        Assert.Equal(exactTransaction.BankTransactionId.Value, exactMatch.BankTransactionId);
        Assert.Equal(new MoneyDto(10.5m, "PLN"), exactMatch.Amount);
        Assert.Equal("INV-001", exactMatch.NormalizedReference);
        Assert.Equal(
            nameof(MatchReasonCode.ExactReferenceCurrencyAndOpenAmount),
            exactMatch.Reason);
        Assert.Empty(result.Value.AmbiguousGroups);

        ClassifiedMatchResponse unmatched = Assert.Single(result.Value.Unmatched);
        Assert.Equal(nameof(MatchClassification.NoMatch), unmatched.Classification);
        Assert.Equal(nameof(MatchReasonCode.NoCounterpart), unmatched.Reason);
        Assert.Equal(unmatchedInvoice.InvoiceId.Value, unmatched.InvoiceId);
        Assert.Null(unmatched.BankTransactionId);
        Assert.Equal("ONLY", unmatched.NormalizedReference);
        Assert.Equal(new MoneyDto(3m, "PLN"), unmatched.OpenAmount);

        ClassifiedMatchResponse ineligible = Assert.Single(result.Value.Ineligible);
        Assert.Equal(nameof(MatchClassification.Ineligible), ineligible.Classification);
        Assert.Equal(nameof(MatchReasonCode.MissingReference), ineligible.Reason);
        Assert.Null(ineligible.InvoiceId);
        Assert.Equal(ineligibleTransaction.BankTransactionId.Value, ineligible.BankTransactionId);
        Assert.Null(ineligible.NormalizedReference);
        Assert.Equal(new MoneyDto(4m, "PLN"), ineligible.OpenAmount);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_returns_candidate_limit_failure_when_invoices_exceed_the_limit_without_matching()
    {
        FakeCandidateQueries queries = new(LimitExceeded(
            invoiceLimitExceeded: true,
            bankTransactionLimitExceeded: false));
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded,
            result.Error);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_returns_candidate_limit_failure_when_bank_transactions_exceed_the_limit_without_matching()
    {
        FakeCandidateQueries queries = new(LimitExceeded(
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: true));
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded,
            result.Error);
    }

    [Fact]
    public async Task Handle_returns_inconsistent_money_without_matching()
    {
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [Invoice(1, "INV-001", 10m)],
            [Transaction(2, "INV-001", 10m)],
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: true));
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.InconsistentMatchMoney,
            result.Error);
    }

    [Fact]
    public async Task Handle_prefers_inconsistent_money_when_a_limit_is_also_exceeded()
    {
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [Invoice(1, "INV-001", 10m)],
            [Transaction(2, "INV-001", 10m)],
            invoiceLimitExceeded: true,
            bankTransactionLimitExceeded: true,
            inconsistentMoney: true));
        GetExactMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<ExactMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InconsistentMatchMoney,
            result.Error);
    }

    [Fact]
    public async Task Handle_does_not_query_when_the_organization_context_fails()
    {
        FakeCandidateQueries queries = new(EmptyCandidates());
        GetExactMatchesHandler handler = CreateHandler(
            new ThrowingOrganizationContext(),
            queries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(CancellationToken.None));

        Assert.Equal(0, queries.CallCount);
    }

    private static GetExactMatchesHandler CreateHandler(
        ICurrentOrganizationContext organizationContext,
        IExactMatchCandidateQueries queries)
    {
        return new GetExactMatchesHandler(organizationContext, queries);
    }

    private static ExactMatchCandidateSet EmptyCandidates()
    {
        return new ExactMatchCandidateSet(
            [],
            [],
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: false);
    }

    private static ExactMatchCandidateSet LimitExceeded(
        bool invoiceLimitExceeded,
        bool bankTransactionLimitExceeded)
    {
        return new ExactMatchCandidateSet(
            [Invoice(1, "INV-001", 10m)],
            [Transaction(2, "INV-001", 10m)],
            invoiceLimitExceeded,
            bankTransactionLimitExceeded,
            inconsistentMoney: false);
    }

    private static MatchInvoiceSnapshot Invoice(int suffix, string invoiceNumber, decimal amount)
    {
        return new MatchInvoiceSnapshot(
            TestOrganizationId,
            new InvoiceId(GuidOf(suffix)),
            invoiceNumber,
            new Money(amount, Pln));
    }

    private static MatchBankTransactionSnapshot Transaction(
        int suffix,
        string? reference,
        decimal amount)
    {
        return new MatchBankTransactionSnapshot(
            TestOrganizationId,
            new BankTransactionId(GuidOf(suffix)),
            reference,
            new Money(amount, Pln));
    }

    private static Guid GuidOf(int suffix)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{suffix:000000000000}");
    }

    private sealed class RecordingOrganizationContext : ICurrentOrganizationContext
    {
        private readonly OrganizationId _organizationId;

        public RecordingOrganizationContext(OrganizationId organizationId)
        {
            _organizationId = organizationId;
        }

        public int ReadCount { get; private set; }

        public OrganizationId OrganizationId
        {
            get
            {
                ReadCount++;
                return _organizationId;
            }
        }
    }

    private sealed class ThrowingOrganizationContext : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId =>
            throw new InvalidOperationException(
                "A valid organization claim is required on the authenticated user.");
    }

    private sealed class FakeCandidateQueries : IExactMatchCandidateQueries
    {
        private readonly ExactMatchCandidateSet _candidates;

        public FakeCandidateQueries(ExactMatchCandidateSet candidates)
        {
            _candidates = candidates;
        }

        public int CallCount { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public int Limit { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OrganizationId = organizationId;
            Limit = limit;
            CancellationToken = cancellationToken;
            return Task.FromResult(_candidates);
        }
    }
}