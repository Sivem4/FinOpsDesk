using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.UnitTests.Access;

public sealed class OrganizationRolePermissionTests
{
    [Fact]
    public void Persisted_role_values_stay_explicit()
    {
        Assert.Equal(1, (byte)OrganizationRole.Viewer);
        Assert.Equal(2, (byte)OrganizationRole.Operator);
        Assert.Equal(3, (byte)OrganizationRole.ReversalApprover);
        Assert.Equal(4, (byte)OrganizationRole.OrganizationAdmin);
    }

    [Fact]
    public void Viewer_has_the_read_set_only()
    {
        Assert.Equal(
            Set(
                AccessPermission.InvoicesRead,
                AccessPermission.BankTransactionsRead,
                AccessPermission.ReconciliationRead,
                AccessPermission.AllocationsRead,
                AccessPermission.ReviewCasesRead),
            OrganizationRolePermissions.For(OrganizationRole.Viewer));
    }

    [Fact]
    public void Operator_adds_writes_and_audit_to_viewer()
    {
        Assert.Equal(
            Set(
                AccessPermission.InvoicesRead,
                AccessPermission.BankTransactionsRead,
                AccessPermission.ReconciliationRead,
                AccessPermission.AllocationsRead,
                AccessPermission.ReviewCasesRead,
                AccessPermission.ImportsCreate,
                AccessPermission.ReconciliationConfirm,
                AccessPermission.AllocationsCreate,
                AccessPermission.ReviewCasesOpen,
                AccessPermission.ReviewCasesResolve,
                AccessPermission.ReviewCasesComment,
                AccessPermission.ReviewCasesAssign,
                AccessPermission.ReviewCasesAssignmentRead,
                AccessPermission.AllocationDisputesRegister,
                AccessPermission.AllocationDisputesRead,
                AccessPermission.AllocationDisputesResolve,
                AccessPermission.AllocationDisputesResolutionRead,
                AccessPermission.AuditRead),
            OrganizationRolePermissions.For(OrganizationRole.Operator));
    }

    [Fact]
    public void Reversal_approver_adds_reversal_and_audit_to_viewer()
    {
        Assert.Equal(
            Set(
                AccessPermission.InvoicesRead,
                AccessPermission.BankTransactionsRead,
                AccessPermission.ReconciliationRead,
                AccessPermission.AllocationsRead,
                AccessPermission.ReviewCasesRead,
                AccessPermission.AllocationsReverse,
                AccessPermission.AuditRead),
            OrganizationRolePermissions.For(OrganizationRole.ReversalApprover));
    }

    [Fact]
    public void Organization_admin_has_administration_permissions_only()
    {
        Assert.Equal(
            Set(AccessPermission.MembershipManage, AccessPermission.RolesManage),
            OrganizationRolePermissions.For(OrganizationRole.OrganizationAdmin));
        Assert.DoesNotContain(
            AccessPermission.InvoicesRead,
            (IEnumerable<AccessPermission>)OrganizationRolePermissions.For(OrganizationRole.OrganizationAdmin));
    }

    [Fact]
    public void Union_combines_distinct_roles_and_an_empty_list_is_empty()
    {
        Assert.Equal(OrganizationRolePermissions.Empty, OrganizationRolePermissions.Union([]));
        IReadOnlySet<AccessPermission> combined = OrganizationRolePermissions.Union(
            [OrganizationRole.Operator, OrganizationRole.ReversalApprover]);
        Assert.Contains(AccessPermission.AllocationsCreate, combined);
        Assert.Contains(AccessPermission.AllocationsReverse, combined);
        Assert.Contains(AccessPermission.AllocationDisputesRead, combined);
        Assert.Contains(AccessPermission.AllocationDisputesResolve, combined);
        Assert.Contains(AccessPermission.AllocationDisputesResolutionRead, combined);
        Assert.DoesNotContain(AccessPermission.RolesManage, (IEnumerable<AccessPermission>)combined);
        Assert.DoesNotContain(
            AccessPermission.AllocationDisputesResolve,
            (IEnumerable<AccessPermission>)OrganizationRolePermissions.For(OrganizationRole.Viewer));
        Assert.DoesNotContain(
            AccessPermission.AllocationDisputesResolutionRead,
            (IEnumerable<AccessPermission>)OrganizationRolePermissions.For(OrganizationRole.ReversalApprover));
        Assert.DoesNotContain(
            AccessPermission.AllocationDisputesResolve,
            (IEnumerable<AccessPermission>)OrganizationRolePermissions.For(OrganizationRole.OrganizationAdmin));
        IReadOnlySet<AccessPermission> administration = OrganizationRolePermissions.Union(
            [OrganizationRole.Viewer, OrganizationRole.OrganizationAdmin]);
        Assert.DoesNotContain(AccessPermission.AllocationDisputesRead, (IEnumerable<AccessPermission>)administration);
    }

    [Fact]
    public void Unknown_role_fails_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganizationRolePermissions.For((OrganizationRole)9));
        Assert.Throws<InvalidOperationException>(() => OrganizationRolePermissions.Require(9));
    }

    [Fact]
    public void Equal_revocation_clock_moves_one_tick_and_an_earlier_clock_is_rejected()
    {
        DateTimeOffset grantedAt = DateTimeOffset.Parse("2026-11-02T12:00:00.0000000Z");
        Assert.Equal(grantedAt.AddTicks(1), OrganizationRoleAssignment.ResolveRevokedAt(grantedAt, grantedAt));
        Assert.Throws<ArgumentException>(() => OrganizationRoleAssignment.ResolveRevokedAt(grantedAt, grantedAt.AddTicks(-1)));
    }

    [Fact]
    public void Revoked_assignment_cannot_be_revoked_again()
    {
        OrganizationRoleAssignment assignment = OrganizationRoleAssignment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            OrganizationRole.Viewer,
            DateTimeOffset.Parse("2026-11-02T12:00:00Z"),
            RoleActor.DeploymentBootstrap("deploy-operator"));
        assignment.Revoke(DateTimeOffset.Parse("2026-11-02T13:00:00Z"), RoleActor.DeploymentBootstrap("deploy-operator"));

        Assert.Throws<InvalidOperationException>(() => assignment.Revoke(
            DateTimeOffset.Parse("2026-11-02T14:00:00Z"),
            RoleActor.DeploymentBootstrap("deploy-operator")));
    }

    private static HashSet<AccessPermission> Set(params AccessPermission[] permissions)
    {
        return [.. permissions];
    }
}