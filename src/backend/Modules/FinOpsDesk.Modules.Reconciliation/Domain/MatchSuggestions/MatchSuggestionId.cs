namespace FinOpsDesk.Modules.Reconciliation.Domain.MatchSuggestions;

public sealed record MatchSuggestionId
{
    public Guid Value { get; }

    public MatchSuggestionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Match suggestion id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static MatchSuggestionId New()
    {
        return new MatchSuggestionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}