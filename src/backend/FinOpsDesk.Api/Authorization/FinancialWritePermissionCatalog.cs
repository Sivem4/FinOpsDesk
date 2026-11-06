using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Api.Authorization;

// Write classifications accept only these permissions. A read permission, an
// administrative permission, or an unknown string is not a financial write.
internal static class FinancialWritePermissionCatalog
{
    private static readonly Dictionary<string, AccessPermission> Writes = new(StringComparer.Ordinal)
    {
        [AccessPermission.ImportsCreate.Value] = AccessPermission.ImportsCreate,
        [AccessPermission.ReconciliationConfirm.Value] = AccessPermission.ReconciliationConfirm,
        [AccessPermission.AllocationsCreate.Value] = AccessPermission.AllocationsCreate,
        [AccessPermission.AllocationsReverse.Value] = AccessPermission.AllocationsReverse,
        [AccessPermission.ReviewCasesOpen.Value] = AccessPermission.ReviewCasesOpen,
        [AccessPermission.ReviewCasesResolve.Value] = AccessPermission.ReviewCasesResolve,
        [AccessPermission.ReviewCasesComment.Value] = AccessPermission.ReviewCasesComment,
    };

    public static bool TryGet(string? permission, out AccessPermission resolved)
    {
        if (permission is null || !Writes.TryGetValue(permission, out resolved))
        {
            resolved = default;
            return false;
        }

        return true;
    }
}