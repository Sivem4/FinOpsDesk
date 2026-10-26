namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record SmartMatchSignal
{
    public SmartMatchSignalCode Code { get; }

    public int Points { get; }

    public SmartMatchSignal(SmartMatchSignalCode code, int points)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), "Signal code is not defined.");
        }

        if (points < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(points), "Signal points cannot be negative.");
        }

        Code = code;
        Points = points;
    }
}