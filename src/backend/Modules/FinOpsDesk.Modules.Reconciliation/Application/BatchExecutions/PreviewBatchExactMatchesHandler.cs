using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed class PreviewBatchExactMatchesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IExactMatchCandidateQueries _candidateQueries;
    private readonly IOpenReviewBlockQueries _openReviewBlockQueries;

    public PreviewBatchExactMatchesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IExactMatchCandidateQueries candidateQueries,
        IOpenReviewBlockQueries openReviewBlockQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(candidateQueries);
        ArgumentNullException.ThrowIfNull(openReviewBlockQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _candidateQueries = candidateQueries;
        _openReviewBlockQueries = openReviewBlockQueries;
    }

    public async Task<Result<PreviewBatchExactMatchesResponse>> Handle(
        PreviewBatchExactMatchesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1
            || query.PageSize < 1
            || query.PageSize > BatchExecutionLimits.MaxPreviewPageSize)
        {
            return Result<PreviewBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInvalidRequest);
        }

        int offset;
        try
        {
            offset = checked((query.Page - 1) * query.PageSize);
        }
        catch (OverflowException)
        {
            return Result<PreviewBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInvalidRequest);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ExactMatchCandidateSet candidates = await _candidateQueries.GetOpenCandidatesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide,
            cancellationToken);

        if (candidates.InconsistentMoney)
        {
            return Result<PreviewBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        if (candidates.InvoiceLimitExceeded || candidates.BankTransactionLimitExceeded)
        {
            return Result<PreviewBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchCandidateLimitExceeded);
        }

        ExactMatchBatchResult classification = ExactMatchRules.Evaluate(
            organizationId,
            candidates.Invoices,
            candidates.BankTransactions);
        IReadOnlyList<ReviewBlockSubject> subjects = await _openReviewBlockQueries.ListOpenAsync(
            organizationId,
            cancellationToken);
        OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(subjects);
        BatchExactMatchPlan plan = BatchExactMatchPlanner.Plan(
            classification,
            blocks.InvoiceIds,
            blocks.BankTransactionIds,
            BatchMatchCoverage.Complete);

        return Result<PreviewBatchExactMatchesResponse>.Success(
            new PreviewBatchExactMatchesResponse(
                plan.AlgorithmVersion,
                plan.InputComplete,
                plan.EligibleCount,
                plan.ExcludedByReviewCount,
                plan.AmbiguousGroupCount,
                plan.UnmatchedCount,
                plan.IneligibleCount,
                query.Page,
                query.PageSize,
                Page(plan.EligiblePairs, offset, query.PageSize),
                Summarize(classification.AmbiguousGroups),
                Totals(plan.EligiblePairs)));
    }

    private static IReadOnlyList<PreviewEligiblePairResponse> Page(
        IReadOnlyList<ExactMatch> pairs,
        int offset,
        int pageSize)
    {
        if (offset >= pairs.Count)
        {
            return [];
        }

        int count = Math.Min(pageSize, pairs.Count - offset);
        PreviewEligiblePairResponse[] page = new PreviewEligiblePairResponse[count];

        for (int index = 0; index < count; index++)
        {
            ExactMatch pair = pairs[offset + index];
            page[index] = new PreviewEligiblePairResponse(
                pair.InvoiceId.Value,
                pair.BankTransactionId.Value,
                new MoneyDto(pair.Amount.Amount, pair.Amount.Currency.Value),
                pair.NormalizedReference);
        }

        return page;
    }

    private static IReadOnlyList<AmbiguousMatchGroupResponse> Summarize(
        IReadOnlyList<AmbiguousMatchGroup> groups)
    {
        int count = Math.Min(groups.Count, BatchExecutionLimits.MaxAmbiguousSummaries);
        AmbiguousMatchGroupResponse[] summaries = new AmbiguousMatchGroupResponse[count];

        for (int index = 0; index < count; index++)
        {
            AmbiguousMatchGroup group = groups[index];
            summaries[index] = new AmbiguousMatchGroupResponse(
                group.NormalizedReference,
                new MoneyDto(group.OpenAmount.Amount, group.OpenAmount.Currency.Value),
                group.Reason.ToString(),
                InvoiceIds(group.InvoiceIds),
                BankTransactionIds(group.BankTransactionIds));
        }

        return summaries;
    }

    private static IReadOnlyList<Guid> InvoiceIds(IReadOnlyList<InvoiceId> values)
    {
        Guid[] ids = new Guid[values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            ids[index] = values[index].Value;
        }

        return ids;
    }

    private static IReadOnlyList<Guid> BankTransactionIds(IReadOnlyList<BankTransactionId> values)
    {
        Guid[] ids = new Guid[values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            ids[index] = values[index].Value;
        }

        return ids;
    }

    private static IReadOnlyList<PreviewCurrencyTotalResponse> Totals(IReadOnlyList<ExactMatch> pairs)
    {
        Dictionary<string, (decimal Amount, int Count)> totals = new(StringComparer.Ordinal);

        foreach (ExactMatch pair in pairs)
        {
            string currency = pair.Amount.Currency.Value;
            (decimal amount, int count) = totals.GetValueOrDefault(currency);
            totals[currency] = (amount + pair.Amount.Amount, count + 1);
        }

        string[] currencies = totals.Keys.ToArray();
        Array.Sort(currencies, StringComparer.Ordinal);
        PreviewCurrencyTotalResponse[] response = new PreviewCurrencyTotalResponse[currencies.Length];

        for (int index = 0; index < currencies.Length; index++)
        {
            string currency = currencies[index];
            (decimal amount, int count) = totals[currency];
            response[index] = new PreviewCurrencyTotalResponse(currency, amount, count);
        }

        return response;
    }
}