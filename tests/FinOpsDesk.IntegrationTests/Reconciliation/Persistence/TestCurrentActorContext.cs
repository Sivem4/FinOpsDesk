using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal sealed class TestCurrentActorContext : ICurrentActorContext
{
    public const string DefaultActorId = "operator-a";

    private readonly string? _actorId;

    public TestCurrentActorContext(string? actorId = DefaultActorId)
    {
        _actorId = actorId;
    }

    public bool TryGetActorId(out string actorId)
    {
        if (string.IsNullOrWhiteSpace(_actorId))
        {
            actorId = string.Empty;
            return false;
        }

        actorId = _actorId;
        return true;
    }
}