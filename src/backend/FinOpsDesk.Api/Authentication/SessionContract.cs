using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace FinOpsDesk.Api.Authentication;

// The header selects the session JSON shape. It does not grant financial access.
internal static class SessionContract
{
    public const string HeaderName = "X-FinOpsDesk-Session-Contract";

    public const string Version2 = "2";

    private static readonly AccessPermission[] OrderedReads =
    [
        AccessPermission.InvoicesRead,
        AccessPermission.BankTransactionsRead,
        AccessPermission.ReconciliationRead,
        AccessPermission.AllocationsRead,
        AccessPermission.AuditRead,
        AccessPermission.ReviewCasesRead,
    ];

    public static bool IsVersion2(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(HeaderName, out StringValues values) || values.Count != 1)
        {
            return false;
        }

        return string.Equals(values[0], Version2, StringComparison.Ordinal);
    }

    public static string[] EnabledReads(IReadOnlySet<AccessPermission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        List<string> enabled = [];
        foreach (AccessPermission candidate in OrderedReads)
        {
            if (permissions.Contains(candidate))
            {
                enabled.Add(candidate.Value);
            }
        }

        return enabled.ToArray();
    }

    public static string[] EnabledCapabilities(
        IReadOnlySet<AccessPermission> permissions,
        bool advertiseActivatedWrites)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        List<string> enabled = [.. EnabledReads(permissions)];
        if (advertiseActivatedWrites)
        {
            ProductionFinancialWriteActivation.AppendAdvertised(permissions, enabled);
        }

        return enabled.ToArray();
    }
}