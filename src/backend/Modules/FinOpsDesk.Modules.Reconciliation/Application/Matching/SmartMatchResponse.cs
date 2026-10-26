using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.Modules.Reconciliation.Application.Matching;

public sealed record SmartMatchResponse(
    string AlgorithmVersion,
    bool Advisory,
    bool Truncated,
    bool CoverageLimited,
    IReadOnlyList<SmartMatchSuggestionResponse> Suggestions)
{
    public static SmartMatchResponse From(SmartMatchBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        SmartMatchSuggestionResponse[] suggestions = new SmartMatchSuggestionResponse[result.Suggestions.Count];

        for (int index = 0; index < result.Suggestions.Count; index++)
        {
            suggestions[index] = SmartMatchSuggestionResponse.From(result.Suggestions[index]);
        }

        return new SmartMatchResponse(
            result.AlgorithmVersion,
            Advisory: true,
            result.Truncated,
            result.CoverageLimited,
            Array.AsReadOnly(suggestions));
    }
}

public sealed record SmartMatchSuggestionResponse(
    int Rank,
    Guid InvoiceId,
    Guid BankTransactionId,
    int Score,
    string ScoreScale,
    bool Contested,
    bool ExactMatchAmbiguous,
    MoneyDto InvoiceOutstanding,
    MoneyDto BankTransactionUnapplied,
    MoneyDto SuggestedAmount,
    IReadOnlyList<SmartMatchSignalResponse> Signals)
{
    public static SmartMatchSuggestionResponse From(SmartMatchSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        SmartMatchSignalResponse[] signals = new SmartMatchSignalResponse[suggestion.Signals.Count];

        for (int index = 0; index < suggestion.Signals.Count; index++)
        {
            SmartMatchSignal signal = suggestion.Signals[index];
            signals[index] = new SmartMatchSignalResponse(signal.Code.ToString(), signal.Points);
        }

        return new SmartMatchSuggestionResponse(
            suggestion.Rank,
            suggestion.InvoiceId.Value,
            suggestion.BankTransactionId.Value,
            suggestion.Score,
            SmartMatchSuggestion.ScoreScale,
            suggestion.Contested,
            suggestion.ExactMatchAmbiguous,
            ToMoney(suggestion.InvoiceOutstanding),
            ToMoney(suggestion.BankTransactionUnapplied),
            ToMoney(suggestion.SuggestedAmount),
            Array.AsReadOnly(signals));
    }

    private static MoneyDto ToMoney(Money money)
    {
        return new MoneyDto(money.Amount, money.Currency.Value);
    }
}

public sealed record SmartMatchSignalResponse(string Code, int Points);