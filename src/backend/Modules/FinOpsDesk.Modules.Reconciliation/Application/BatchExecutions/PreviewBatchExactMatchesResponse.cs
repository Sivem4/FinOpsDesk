using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed record PreviewBatchExactMatchesResponse(
    string AlgorithmVersion,
    bool InputComplete,
    int EligibleCount,
    int ExcludedByReviewCount,
    int AmbiguousGroupCount,
    int UnmatchedCount,
    int IneligibleCount,
    int Page,
    int PageSize,
    IReadOnlyList<PreviewEligiblePairResponse> EligiblePairs,
    IReadOnlyList<AmbiguousMatchGroupResponse> AmbiguousGroups,
    IReadOnlyList<PreviewCurrencyTotalResponse> CurrencyTotals);

public sealed record PreviewEligiblePairResponse(
    Guid InvoiceId,
    Guid BankTransactionId,
    MoneyDto Amount,
    string NormalizedReference);

public sealed record PreviewCurrencyTotalResponse(
    string Currency,
    decimal Amount,
    int PairCount);