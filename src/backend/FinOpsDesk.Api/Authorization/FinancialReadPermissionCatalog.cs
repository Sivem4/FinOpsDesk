using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Api.Authorization;

// Read classifications accept only these permissions. A write permission, an
// administrative permission, or an unknown string is not a financial read.
internal static class FinancialReadPermissionCatalog
{
    private static readonly Dictionary<string, AccessPermission> Reads = new(StringComparer.Ordinal)
    {
        [AccessPermission.InvoicesRead.Value] = AccessPermission.InvoicesRead,
        [AccessPermission.BankTransactionsRead.Value] = AccessPermission.BankTransactionsRead,
        [AccessPermission.ReconciliationRead.Value] = AccessPermission.ReconciliationRead,
        [AccessPermission.AllocationsRead.Value] = AccessPermission.AllocationsRead,
        [AccessPermission.AuditRead.Value] = AccessPermission.AuditRead,
        [AccessPermission.ReviewCasesRead.Value] = AccessPermission.ReviewCasesRead,
    };

    public static bool TryGet(string? permission, out AccessPermission resolved)
    {
        if (permission is null || !Reads.TryGetValue(permission, out resolved))
        {
            resolved = default;
            return false;
        }

        return true;
    }
}