using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed class SmartMatchSuggestion
{
    public const string ScoreScale = "points";

    public int Rank { get; }

    public int Score { get; }

    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money InvoiceOutstanding { get; }

    public Money BankTransactionUnapplied { get; }

    public Money SuggestedAmount { get; }

    public bool Contested { get; }

    public bool ExactMatchAmbiguous { get; }

    public IReadOnlyList<SmartMatchSignal> Signals { get; }

    public SmartMatchSuggestion(
        int rank,
        int score,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money invoiceOutstanding,
        Money bankTransactionUnapplied,
        Money suggestedAmount,
        bool contested,
        bool exactMatchAmbiguous,
        IReadOnlyList<SmartMatchSignal> signals)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rank);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(invoiceOutstanding);
        ArgumentNullException.ThrowIfNull(bankTransactionUnapplied);
        ArgumentNullException.ThrowIfNull(suggestedAmount);
        ArgumentNullException.ThrowIfNull(signals);

        if (signals.Count == 0)
        {
            throw new ArgumentException("A suggestion must explain its score.", nameof(signals));
        }

        if (!invoiceOutstanding.HasSameCurrency(bankTransactionUnapplied)
            || !invoiceOutstanding.HasSameCurrency(suggestedAmount))
        {
            throw new ArgumentException(
                "Suggestion amounts must use the same currency.",
                nameof(suggestedAmount));
        }

        decimal smallerOpenAmount = invoiceOutstanding.Amount <= bankTransactionUnapplied.Amount
            ? invoiceOutstanding.Amount
            : bankTransactionUnapplied.Amount;

        if (suggestedAmount.Amount != smallerOpenAmount || suggestedAmount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suggestedAmount),
                "Suggested amount must be the smaller positive open amount.");
        }

        int explained = 0;

        foreach (SmartMatchSignal signal in signals)
        {
            ArgumentNullException.ThrowIfNull(signal);
            explained += signal.Points;
        }

        if (explained != score)
        {
            throw new ArgumentException(
                "Signal points must add up to the suggestion score.",
                nameof(signals));
        }

        SmartMatchSignal[] copy = new SmartMatchSignal[signals.Count];

        for (int index = 0; index < signals.Count; index++)
        {
            copy[index] = signals[index];
        }

        Rank = rank;
        Score = score;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        InvoiceOutstanding = invoiceOutstanding;
        BankTransactionUnapplied = bankTransactionUnapplied;
        SuggestedAmount = suggestedAmount;
        Contested = contested;
        ExactMatchAmbiguous = exactMatchAmbiguous;
        Signals = Array.AsReadOnly(copy);
    }
}