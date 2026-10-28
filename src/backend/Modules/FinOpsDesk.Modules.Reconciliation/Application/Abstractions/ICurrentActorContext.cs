namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public interface ICurrentActorContext
{
    bool TryGetActorId(out string actorId);
}