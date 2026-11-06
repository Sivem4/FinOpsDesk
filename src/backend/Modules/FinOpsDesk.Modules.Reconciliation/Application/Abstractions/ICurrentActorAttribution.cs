using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public interface ICurrentActorAttribution
{
    bool TryGet(out ReviewCaseActorSource source, out string actorId);
}