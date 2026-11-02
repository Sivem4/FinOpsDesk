using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Api.Authentication;

internal enum AccessPermissionResolutionKind
{
    Resolved = 1,
    Unavailable = 2,
    Untrusted = 3,
}

internal sealed class AccessPermissionResolution
{
    private AccessPermissionResolution(
        AccessPermissionResolutionKind kind,
        string reason,
        IReadOnlySet<AccessPermission> permissions)
    {
        Kind = kind;
        Reason = reason;
        Permissions = permissions;
    }

    public AccessPermissionResolutionKind Kind { get; }

    public string Reason { get; }

    public IReadOnlySet<AccessPermission> Permissions { get; }

    public static AccessPermissionResolution Resolved(IReadOnlySet<AccessPermission> permissions, string reason)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new AccessPermissionResolution(AccessPermissionResolutionKind.Resolved, reason, permissions);
    }

    public static AccessPermissionResolution Unavailable(string reason)
    {
        return new AccessPermissionResolution(
            AccessPermissionResolutionKind.Unavailable,
            reason,
            OrganizationRolePermissions.Empty);
    }

    public static AccessPermissionResolution Untrusted(string reason)
    {
        return new AccessPermissionResolution(
            AccessPermissionResolutionKind.Untrusted,
            reason,
            OrganizationRolePermissions.Empty);
    }
}