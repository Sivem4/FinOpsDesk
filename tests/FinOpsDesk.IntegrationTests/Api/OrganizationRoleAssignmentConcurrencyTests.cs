using System.Text.Json;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed partial class OrganizationRoleAssignmentTests
{
    [Fact]
    public async Task Grant_does_not_commit_after_authority_was_revoked_before_its_lock()
    {
        ApplicationUser actor = await SignedInUserAsync();
        ApplicationUser other = await CreateUserAsync("authority-revoker-" + Guid.NewGuid().ToString("N"));
        ApplicationUser target = await CreateUserAsync("authority-target-" + Guid.NewGuid().ToString("N"));
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(actor, organizationId);
        await GrantMembershipAsync(other, organizationId);
        await GrantMembershipAsync(target, organizationId);
        Guid actorMembership = await MembershipIdAsync(actor.Id, organizationId);
        Guid otherMembership = await MembershipIdAsync(other.Id, organizationId);
        Guid targetMembership = await MembershipIdAsync(target.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(actorMembership, OrganizationRole.OrganizationAdmin));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(otherMembership, OrganizationRole.OrganizationAdmin));
        (Guid assignmentId, byte[] version) = await ActiveIdentityAsync(actorMembership, OrganizationRole.OrganizationAdmin);

        PausingRoleSync sync = new() { PauseBefore = true };
        Task<OrganizationRoleMutationStatus> grant = AdministerGrantAsync(
            actor.Id,
            organizationId,
            targetMembership,
            OrganizationRole.Operator,
            sync);
        await sync.BeforeEntered.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal(
                OrganizationRoleMutationStatus.Revoked,
                await AdministerRevokeAsync(other.Id, organizationId, assignmentId, version));
        }
        finally
        {
            sync.ReleaseBefore();
        }

        Assert.Equal(OrganizationRoleMutationStatus.Forbidden, await grant);
        Assert.Equal(0, await CountActiveAsync(targetMembership, OrganizationRole.Operator));
        OrganizationRoleAssignment revoked = await SingleAssignmentAsync(assignmentId);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal(RoleActorKind.ApplicationUser, revoked.RevokeActorKind);
        Assert.Equal(other.Id, revoked.RevokeActorUserId);
        Assert.Null(revoked.RevokeOperatorName);
        Assert.Equal(1, await CountActiveAdministratorsAsync(organizationId));
    }

    [Fact]
    public async Task Grant_holding_the_administrator_lock_commits_before_the_conflicting_revocation()
    {
        ApplicationUser actor = await SignedInUserAsync();
        ApplicationUser other = await CreateUserAsync("lock-revoker-" + Guid.NewGuid().ToString("N"));
        ApplicationUser target = await CreateUserAsync("lock-target-" + Guid.NewGuid().ToString("N"));
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(actor, organizationId);
        await GrantMembershipAsync(other, organizationId);
        await GrantMembershipAsync(target, organizationId);
        Guid actorMembership = await MembershipIdAsync(actor.Id, organizationId);
        Guid otherMembership = await MembershipIdAsync(other.Id, organizationId);
        Guid targetMembership = await MembershipIdAsync(target.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(actorMembership, OrganizationRole.OrganizationAdmin));
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(otherMembership, OrganizationRole.OrganizationAdmin));
        (Guid assignmentId, byte[] version) = await ActiveIdentityAsync(actorMembership, OrganizationRole.OrganizationAdmin);

        PausingRoleSync sync = new() { PauseAfter = true };
        Task<OrganizationRoleMutationStatus> grant = AdministerGrantAsync(
            actor.Id,
            organizationId,
            targetMembership,
            OrganizationRole.Operator,
            sync);
        await sync.AfterEntered.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Exception? blocked = await RevokeWithLockTimeoutAsync(other.Id, organizationId, assignmentId, version);
            Assert.Equal(1222, FindSqlException(blocked)?.Number);
        }
        finally
        {
            sync.ReleaseAfter();
        }

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await grant);
        Assert.Equal(
            OrganizationRoleMutationStatus.Revoked,
            await AdministerRevokeAsync(other.Id, organizationId, assignmentId, version));
        OrganizationRoleAssignment granted = await SingleActiveAsync(targetMembership, OrganizationRole.Operator);
        Assert.Equal(actor.Id, granted.GrantActorUserId);
        Assert.Null(granted.RevokedAt);
        OrganizationRoleAssignment revoked = await SingleAssignmentAsync(assignmentId);
        Assert.Equal(other.Id, revoked.RevokeActorUserId);
        Assert.Equal(1, await CountActiveAdministratorsAsync(organizationId));
    }

    [Fact]
    public async Task Grant_does_not_commit_when_the_target_is_already_inactive()
    {
        ApplicationUser actor = await SignedInUserAsync();
        ApplicationUser revokedMember = await CreateUserAsync("revoked-member-" + Guid.NewGuid().ToString("N"));
        ApplicationUser disabledMember = await CreateUserAsync("disabled-member-" + Guid.NewGuid().ToString("N"));
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(actor, organizationId);
        await GrantMembershipAsync(revokedMember, organizationId);
        await GrantMembershipAsync(disabledMember, organizationId);
        Guid actorMembership = await MembershipIdAsync(actor.Id, organizationId);
        Guid revokedMembership = await MembershipIdAsync(revokedMember.Id, organizationId);
        Guid disabledMembership = await MembershipIdAsync(disabledMember.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(actorMembership, OrganizationRole.OrganizationAdmin));

        PausingRoleSync revokeSync = new() { PauseBefore = true };
        Task<OrganizationRoleMutationStatus> revokedGrant = AdministerGrantAsync(
            actor.Id,
            organizationId,
            revokedMembership,
            OrganizationRole.Operator,
            revokeSync);
        await revokeSync.BeforeEntered.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using AccessDbContext context = _fixture.CreateAccessContext();
            OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == revokedMembership);
            membership.Revoke(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }
        finally
        {
            revokeSync.ReleaseBefore();
        }

        Assert.Equal(OrganizationRoleMutationStatus.InactiveMembership, await revokedGrant);
        Assert.Equal(0, await CountActiveAsync(revokedMembership, OrganizationRole.Operator));

        PausingRoleSync disableSync = new() { PauseBefore = true };
        Task<OrganizationRoleMutationStatus> disabledGrant = AdministerGrantAsync(
            actor.Id,
            organizationId,
            disabledMembership,
            OrganizationRole.Operator,
            disableSync);
        await disableSync.BeforeEntered.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using AccessDbContext context = _fixture.CreateAccessContext();
            ApplicationUser user = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == disabledMember.Id);
            user.Disable();
            await context.SaveChangesAsync();
        }
        finally
        {
            disableSync.ReleaseBefore();
        }

        Assert.Equal(OrganizationRoleMutationStatus.DisabledUser, await disabledGrant);
        Assert.Equal(0, await CountActiveAsync(disabledMembership, OrganizationRole.Operator));
    }

    [Fact]
    public async Task Stale_revoke_does_not_revoke_the_replacement_assignment()
    {
        ApplicationUser actor = await SignedInUserAsync();
        ApplicationUser target = await CreateUserAsync("replacement-target-" + Guid.NewGuid().ToString("N"));
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(actor, organizationId);
        await GrantMembershipAsync(target, organizationId);
        Guid actorMembership = await MembershipIdAsync(actor.Id, organizationId);
        Guid targetMembership = await MembershipIdAsync(target.Id, organizationId);
        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(actorMembership, OrganizationRole.OrganizationAdmin));
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await StoreGrantAsync(targetMembership, OrganizationRole.Operator, "first-grant"));
        (Guid originalId, byte[] originalVersion) = await ActiveIdentityAsync(targetMembership, OrganizationRole.Operator);
        Assert.Equal(
            OrganizationRoleMutationStatus.Revoked,
            await StoreRevokeAsync(targetMembership, OrganizationRole.Operator, "first-revoker"));
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await StoreGrantAsync(targetMembership, OrganizationRole.Operator, "second-grant"));
        (Guid replacementId, byte[] _) = await ActiveIdentityAsync(targetMembership, OrganizationRole.Operator);

        Assert.Equal(
            OrganizationRoleMutationStatus.NotActive,
            await AdministerRevokeAsync(actor.Id, organizationId, originalId, originalVersion));

        OrganizationRoleAssignment original = await SingleAssignmentAsync(originalId);
        OrganizationRoleAssignment replacement = await SingleAssignmentAsync(replacementId);
        Assert.NotEqual(originalId, replacementId);
        Assert.NotNull(original.RevokedAt);
        Assert.Equal("first-grant", original.GrantOperatorName);
        Assert.Equal("first-revoker", original.RevokeOperatorName);
        Assert.Null(replacement.RevokedAt);
        Assert.Equal("second-grant", replacement.GrantOperatorName);
        Assert.Null(replacement.RevokeOperatorName);
        Assert.Equal(1, await CountActiveAsync(targetMembership, OrganizationRole.Operator));
    }

    [Fact]
    public async Task Revoked_browser_session_does_not_resolve_trusted_permissions()
    {
        using HttpClient client = _factory.CreateBrowserClient();
        await SignInAsync(client, _stub);
        ApplicationUser user = await RequireUserAsync();
        Guid organizationId = await CreateOrganizationAsync();
        await GrantMembershipAsync(user, organizationId);
        Guid membershipId = await MembershipIdAsync(user.Id, organizationId);
        await StoreGrantAsync(membershipId, OrganizationRole.Operator);
        string csrf = await ReadCsrfAsync(client);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationId)).StatusCode);
        JsonElement granted = await ReadProbeAsync(client);
        Assert.Equal("Resolved", granted.GetProperty("permissionStatus").GetString());
        Assert.Contains(
            granted.GetProperty("permissions").EnumerateArray(),
            permission => permission.GetString() == AccessPermission.ImportsCreate.Value);

        await using (AccessDbContext context = _fixture.CreateAccessContext())
        {
            Guid sessionId = await context.BrowserSessions
                .Where(session => session.UserId == user.Id && session.RevokedAt == null)
                .Select(session => session.Id)
                .SingleAsync();
            Assert.Equal(
                BrowserSessionRevocation.Revoked,
                await new BrowserSessionStore(context).RevokeAsync(sessionId, user.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        JsonElement revoked = await ReadProbeAsync(client);
        Assert.Equal("Rejected", revoked.GetProperty("status").GetString());
        Assert.Equal("Untrusted", revoked.GetProperty("permissionStatus").GetString());
        Assert.Empty(revoked.GetProperty("permissions").EnumerateArray());
    }

    private async Task<OrganizationRoleAssignment> SingleAssignmentAsync(Guid assignmentId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationRoleAssignments.AsNoTracking().SingleAsync(assignment => assignment.Id == assignmentId);
    }

    private async Task<Exception?> RevokeWithLockTimeoutAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] rowVersion)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0;");
        OrganizationRoleStore store = new(context);
        OrganizationRoleAdministration administration = new(store, TimeProvider.System);
        return await Record.ExceptionAsync(() => administration.RevokeAsync(
            actorUserId,
            organizationId,
            assignmentId,
            rowVersion,
            CancellationToken.None));
    }

    private static SqlException? FindSqlException(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
    }

    private sealed class PausingRoleSync : IOrganizationRoleMutationSync
    {
        private readonly TaskCompletionSource _beforeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _beforeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _afterEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _afterRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PauseBefore { get; init; }

        public bool PauseAfter { get; init; }

        public Task BeforeEntered => _beforeEntered.Task;

        public Task AfterEntered => _afterEntered.Task;

        public void ReleaseBefore()
        {
            _beforeRelease.TrySetResult();
        }

        public void ReleaseAfter()
        {
            _afterRelease.TrySetResult();
        }

        public async Task BeforeAuthoritativeLockAsync(CancellationToken cancellationToken)
        {
            if (!PauseBefore)
            {
                return;
            }

            _beforeEntered.TrySetResult();
            await _beforeRelease.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }

        public async Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken)
        {
            if (!PauseAfter)
            {
                return;
            }

            _afterEntered.TrySetResult();
            await _afterRelease.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }
}