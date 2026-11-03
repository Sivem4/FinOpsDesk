using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Reconciliation.Presentation;

namespace FinOpsDesk.Api.Authorization;

// ProductionCookie eligibility for a financial write. Membership, the exact
// permission, CSRF, and the commit-time lock stay outside this set. A write
// that is absent here is denied even when both activation flags are Enabled.
// Session contract v2 advertises the same operations' permissions, in this order.
internal static class ProductionFinancialWriteActivation
{
    private static readonly (FinancialWriteOperation Operation, AccessPermission Permission)[] Activated =
    [
        (FinancialWriteOperation.ConfirmExactMatch, AccessPermission.ReconciliationConfirm),
        (FinancialWriteOperation.CreateAllocation, AccessPermission.AllocationsCreate),
        (FinancialWriteOperation.ReverseAllocation, AccessPermission.AllocationsReverse),
    ];

    public static bool IsActivated(FinancialEndpointClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return classification.Kind == FinancialEndpointClassification.OperationKind.Write
            && classification.Operation is FinancialWriteOperation operation
            && Enum.IsDefined(operation)
            && ActivatedPermission(operation, classification.Permission);
    }

    public static void AppendAdvertised(IReadOnlySet<AccessPermission> granted, List<string> target)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(target);

        foreach ((FinancialWriteOperation _, AccessPermission permission) in Activated)
        {
            if (granted.Contains(permission))
            {
                target.Add(permission.Value);
            }
        }
    }

    private static bool ActivatedPermission(FinancialWriteOperation operation, string? permission)
    {
        foreach ((FinancialWriteOperation candidate, AccessPermission required) in Activated)
        {
            if (candidate == operation)
            {
                return string.Equals(permission, required.Value, StringComparison.Ordinal);
            }
        }

        return false;
    }
}