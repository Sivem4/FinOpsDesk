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

public sealed class GetSmartMatchesHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly CurrencyCode Pln = new("PLN");

    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public async Task Handle_queries_the_trusted_organization_with_the_fixed_limit_and_cancellation_token()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeCandidateQueries queries = new(EmptyCandidates());
        GetSmartMatchesHandler handler = CreateHandler(organizationContext, queries);
        using CancellationTokenSource cancellationTokenSource = new();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<SmartMatchResponse> result = await handler.Handle(cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(SmartMatchRules.AlgorithmVersion, result.Value.AlgorithmVersion);
        Assert.True(result.Value.Advisory);
        Assert.False(result.Value.Truncated);
        Assert.False(result.Value.CoverageLimited);
        Assert.Empty(result.Value.Suggestions);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(TestOrganizationId, queries.OrganizationId);
        Assert.Equal(ExactMatchCandidateLimits.MaxPerSide, queries.Limit);
        Assert.Equal(cancellationToken, queries.CancellationToken);
    }

    [Fact]
    public async Task Handle_maps_score_breakdown_amount_and_flags_without_persisting()
    {
        MatchInvoiceSnapshot invoice = Invoice(1, "INV-001234", 100m);
        MatchBankTransactionSnapshot bankTransaction = Transaction(2, "PAY-001234", 40m);
        FakeCandidateQueries queries = new(Candidates([invoice], [bankTransaction]));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Advisory);
        Assert.False(result.Value.Truncated);
        Assert.False(result.Value.CoverageLimited);
        SmartMatchSuggestionResponse suggestion = Assert.Single(result.Value.Suggestions);
        Assert.Equal(1, suggestion.Rank);
        Assert.Equal(invoice.InvoiceId.Value, suggestion.InvoiceId);
        Assert.Equal(bankTransaction.BankTransactionId.Value, suggestion.BankTransactionId);
        Assert.Equal(60, suggestion.Score);
        Assert.Equal(SmartMatchSuggestion.ScoreScale, suggestion.ScoreScale);
        Assert.Equal("points", suggestion.ScoreScale);
        Assert.False(suggestion.Contested);
        Assert.False(suggestion.ExactMatchAmbiguous);
        Assert.Equal(new MoneyDto(100m, "PLN"), suggestion.InvoiceOutstanding);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.BankTransactionUnapplied);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.SuggestedAmount);
        Assert.Equal(60, suggestion.Signals.Sum(signal => signal.Points));
        Assert.Contains(
            suggestion.Signals,
            signal => signal.Code == nameof(SmartMatchSignalCode.NumericIdentifierEqual)
                && signal.Points == SmartMatchRules.QualifiedReferencePoints);
        Assert.Contains(
            suggestion.Signals,
            signal => signal.Code == nameof(SmartMatchSignalCode.AmountDiffers) && signal.Points == 0);
        Assert.All(suggestion.Signals, signal => Assert.False(int.TryParse(signal.Code, out _)));
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_returns_candidate_limit_failure_without_scoring_a_matching_subset()
    {
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [Invoice(1, "INV-001234", 10m)],
            [Transaction(2, "INV-001234", 10m)],
            invoiceLimitExceeded: true,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: false));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded,
            result.Error);
    }

    [Fact]
    public async Task Handle_returns_candidate_limit_failure_when_bank_transactions_exceed_the_limit()
    {
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [Invoice(1, "INV-001234", 10m)],
            [Transaction(2, "INV-001234", 10m)],
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: true,
            inconsistentMoney: false));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded,
            result.Error);
    }

    [Fact]
    public async Task Handle_returns_inconsistent_money_without_an_empty_match_list()
    {
        FakeCandidateQueries queries = new(new ExactMatchCandidateSet(
            [Invoice(1, "INV-001234", 10m)],
            [Transaction(2, "INV-001234", 10m)],
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
            inconsistentMoney: true));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

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
            [],
            [],
            invoiceLimitExceeded: true,
            bankTransactionLimitExceeded: true,
            inconsistentMoney: true));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InconsistentMatchMoney,
            result.Error);
    }

    [Fact]
    public async Task Handle_does_not_query_when_the_organization_context_fails()
    {
        FakeCandidateQueries queries = new(EmptyCandidates());
        GetSmartMatchesHandler handler = CreateHandler(new ThrowingOrganizationContext(), queries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(CancellationToken.None));

        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task Handle_returns_no_suggestion_for_amount_only_or_cross_currency_rows()
    {
        FakeCandidateQueries queries = new(Candidates(
            [Invoice(1, "RENT ONLY", 50m), Invoice(2, "CUR-30", 30m)],
            [
                Transaction(3, "OFFICE ONLY", 50m),
                Transaction(4, "CUR-30", 30m, Eur),
            ]));
        GetSmartMatchesHandler handler = CreateHandler(
            new RecordingOrganizationContext(TestOrganizationId),
            queries);

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Suggestions);
        Assert.False(result.Value.CoverageLimited);
    }

    private static GetSmartMatchesHandler CreateHandler(
        ICurrentOrganizationContext organizationContext,
        IExactMatchCandidateQueries queries)
    {
        return new GetSmartMatchesHandler(organizationContext, queries);
    }

    private static ExactMatchCandidateSet EmptyCandidates()
    {
        return Candidates([], []);
    }

    private static ExactMatchCandidateSet Candidates(
        IReadOnlyList<MatchInvoiceSnapshot> invoices,
        IReadOnlyList<MatchBankTransactionSnapshot> bankTransactions)
    {
        return new ExactMatchCandidateSet(
            invoices,
            bankTransactions,
            invoiceLimitExceeded: false,
            bankTransactionLimitExceeded: false,
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
        decimal amount,
        CurrencyCode? currency = null)
    {
        return new MatchBankTransactionSnapshot(
            TestOrganizationId,
            new BankTransactionId(GuidOf(suffix)),
            reference,
            new Money(amount, currency ?? Pln));
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