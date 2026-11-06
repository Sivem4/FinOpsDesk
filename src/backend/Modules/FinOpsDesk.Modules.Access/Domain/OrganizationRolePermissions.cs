using System.Collections.Frozen;

namespace FinOpsDesk.Modules.Access.Domain;

public static class OrganizationRolePermissions
{
    private static readonly FrozenSet<AccessPermission> ViewerPermissions = new AccessPermission[]
    {
        AccessPermission.InvoicesRead,
        AccessPermission.BankTransactionsRead,
        AccessPermission.ReconciliationRead,
        AccessPermission.AllocationsRead,
        AccessPermission.ReviewCasesRead,
    }.ToFrozenSet();

    private static readonly FrozenSet<AccessPermission> OperatorPermissions = ViewerPermissions.Concat(
    [
        AccessPermission.ImportsCreate,
        AccessPermission.ReconciliationConfirm,
        AccessPermission.AllocationsCreate,
        AccessPermission.ReviewCasesOpen,
        AccessPermission.ReviewCasesResolve,
        AccessPermission.ReviewCasesComment,
        AccessPermission.AuditRead,
    ]).ToFrozenSet();

    private static readonly FrozenSet<AccessPermission> ReversalApproverPermissions = ViewerPermissions.Concat(
    [
        AccessPermission.AllocationsReverse,
        AccessPermission.AuditRead,
    ]).ToFrozenSet();

    private static readonly FrozenSet<AccessPermission> OrganizationAdminPermissions = new AccessPermission[]
    {
        AccessPermission.MembershipManage,
        AccessPermission.RolesManage,
    }.ToFrozenSet();

    public static FrozenSet<AccessPermission> Empty { get; } = FrozenSet<AccessPermission>.Empty;

    public static bool IsDefined(OrganizationRole role)
    {
        return role is OrganizationRole.Viewer
            or OrganizationRole.Operator
            or OrganizationRole.ReversalApprover
            or OrganizationRole.OrganizationAdmin;
    }

    public static OrganizationRole Require(byte value)
    {
        if (value is < 1 or > 4)
        {
            throw new InvalidOperationException("Organization role assignment is inconsistent.");
        }

        return (OrganizationRole)value;
    }

    public static FrozenSet<AccessPermission> For(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Viewer => ViewerPermissions,
            OrganizationRole.Operator => OperatorPermissions,
            OrganizationRole.ReversalApprover => ReversalApproverPermissions,
            OrganizationRole.OrganizationAdmin => OrganizationAdminPermissions,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Organization role is not defined."),
        };
    }

    public static FrozenSet<AccessPermission> Union(IEnumerable<OrganizationRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        HashSet<AccessPermission> permissions = [];
        foreach (OrganizationRole role in roles)
        {
            permissions.UnionWith(For(role));
        }

        return permissions.ToFrozenSet();
    }
}