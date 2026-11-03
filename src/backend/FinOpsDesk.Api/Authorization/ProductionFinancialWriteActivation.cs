using System.Collections.Frozen;

using FinOpsDesk.Modules.Reconciliation.Presentation;

namespace FinOpsDesk.Api.Authorization;

// ProductionCookie eligibility for a financial write. Membership, the exact
// permission, CSRF, and the commit-time lock stay outside this set. A write
// that is absent here is denied even when both activation flags are Enabled.
internal static class ProductionFinancialWriteActivation
{
    private static readonly FrozenSet<FinancialWriteOperation> Activated = new FinancialWriteOperation[]
    {
        FinancialWriteOperation.ConfirmExactMatch,
        FinancialWriteOperation.CreateAllocation,
        FinancialWriteOperation.ReverseAllocation,
    }.ToFrozenSet();

    public static bool IsActivated(FinancialEndpointClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return classification.Kind == FinancialEndpointClassification.OperationKind.Write
            && classification.Operation is FinancialWriteOperation operation
            && Enum.IsDefined(operation)
            && Activated.Contains(operation);
    }
}