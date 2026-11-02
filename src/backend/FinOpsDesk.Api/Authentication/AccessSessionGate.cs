using System.Security.Claims;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FinOpsDesk.Api.Authentication;

// One resolution per request. The result is not reused by another request.
internal sealed class AccessSessionGate
{
    private readonly IOrganizationMembershipReader _memberships;
    private readonly IBrowserSessionStore _sessions;
    private readonly IOrganizationRoleReader _roles;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccessSessionGate> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly object _permissionLock = new();
    private Task<AccessSessionSnapshot>? _pending;
    private Task<AccessPermissionResolution>? _permissionResolution;

    public AccessSessionGate(
        IOrganizationMembershipReader memberships,
        IBrowserSessionStore sessions,
        IOrganizationRoleReader roles,
        TimeProvider timeProvider,
        ILogger<AccessSessionGate> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        _memberships = memberships;
        _sessions = sessions;
        _roles = roles;
        _timeProvider = timeProvider;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public AccessSessionSnapshot? Current { get; private set; }

    // Test seam. Production leaves this null. Every caller invokes it before the shared task
    // is published, so a test can hold those callers together. It does not read roles.
    internal Action? BeforePublishPermissionResolution { get; set; }

    // One published task per gate. The lock covers only that publication: the async method
    // returns its Task at the first incomplete await, so the role query is not inside the lock.
    // LazyInitializer.EnsureInitialized can invoke its factory more than once and is not used.
    // A missing snapshot is not cached. Another request has another gate and another task.
    public Task<AccessPermissionResolution> ResolvePermissionsAsync(CancellationToken cancellationToken)
    {
        if (Current is null)
        {
            return Task.FromResult(AccessPermissionResolution.Untrusted("session-not-resolved"));
        }

        BeforePublishPermissionResolution?.Invoke();

        Task<AccessPermissionResolution> resolution;
        lock (_permissionLock)
        {
            // The waiter token is not the query token. Request abort cancels the shared read.
            // Cancelling only one waiter does not.
            _permissionResolution ??= ResolvePermissionsUncachedAsync(SharedQueryCancellationToken());
            resolution = _permissionResolution;
        }

        return resolution.WaitAsync(cancellationToken);
    }

    private CancellationToken SharedQueryCancellationToken()
    {
        return _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
    }

    public async Task<AccessSessionSnapshot> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (Current is not null)
        {
            return Current;
        }

        _pending ??= ResolveUncachedAsync(principal, cancellationToken);
        try
        {
            Current = await _pending.WaitAsync(cancellationToken);
            return Current;
        }
        catch
        {
            _pending = null;
            throw;
        }
    }

    private async Task<AccessSessionSnapshot> ResolveUncachedAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!CookieClaimReader.TryGetSingle(principal, "iss", out string issuer)
            || !CookieClaimReader.TryGetSingle(principal, "sub", out string subject)
            || !CookieClaimReader.TryGetSingle(principal, FinOpsDeskCookieClaims.SessionStamp, out string stampText)
            || !Guid.TryParse(stampText, out Guid observedStamp)
            || observedStamp == Guid.Empty)
        {
            _logger.LogWarning(
                "Application cookie identity was rejected. Reason: {Reason}",
                "invalid-identity");
            return AccessSessionSnapshot.InvalidIdentity();
        }

        if (!TryReadSessionId(principal, out Guid sessionId))
        {
            _logger.LogWarning(
                "Application cookie identity was rejected. Reason: {Reason}",
                "invalid-session");
            return AccessSessionSnapshot.InvalidIdentity();
        }

        // A malformed or duplicated selection is not a trusted tenant and does not
        // invalidate the login. Only issuer, subject, the session stamp, and the
        // server session identifier do.
        if (!CookieClaimReader.TryGetOptionalGuid(
            principal,
            FinOpsDeskCookieClaims.Organization,
            out Guid? selectedOrganization))
        {
            selectedOrganization = null;
        }

        string? displayName = CookieClaimReader.TryGetSingle(principal, "name", out string name)
            ? name
            : null;

        try
        {
            MembershipResolution resolution = await _memberships.ResolveAsync(
                issuer,
                subject,
                cancellationToken);
            AccessSessionSnapshot snapshot = await MapAsync(
                resolution,
                issuer,
                subject,
                displayName,
                observedStamp,
                sessionId,
                selectedOrganization,
                cancellationToken);
            if (snapshot.Kind is AccessSessionKind.Rejected or AccessSessionKind.Unavailable)
            {
                _logger.LogWarning(
                    "Application cookie validation did not establish a session. Reason: {Reason}",
                    snapshot.Reason);
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string reason = exception is InvalidOperationException invalid
                && invalid.Message is "Identity key collision." or "Application user session stamp is missing."
                    ? "inconsistent-identity"
                    : "database";
            _logger.LogWarning(
                "Application cookie validation did not complete. Reason: {Reason}",
                reason);
            return AccessSessionSnapshot.Unavailable(reason);
        }
    }

    private async Task<AccessSessionSnapshot> MapAsync(
        MembershipResolution resolution,
        string issuer,
        string subject,
        string? displayName,
        Guid observedStamp,
        Guid sessionId,
        Guid? selectedOrganization,
        CancellationToken cancellationToken)
    {
        if (resolution.Kind == MembershipResolutionKind.Missing)
        {
            return AccessSessionSnapshot.Rejected("missing-user");
        }

        if (resolution.Kind == MembershipResolutionKind.Disabled)
        {
            if (resolution.UserId is Guid userId)
            {
                await _memberships.RetireDisabledSessionAsync(userId, observedStamp, cancellationToken);
            }

            return AccessSessionSnapshot.Rejected("disabled-user");
        }

        if (resolution.UserId is not Guid activeUserId
            || resolution.SessionStamp is not Guid currentStamp
            || currentStamp != observedStamp)
        {
            return AccessSessionSnapshot.Rejected("stamp-mismatch");
        }

        BrowserSessionState? browserSession = await _sessions.FindAsync(sessionId, cancellationToken);
        if (browserSession is null)
        {
            return AccessSessionSnapshot.Rejected("unknown-session");
        }

        if (browserSession.UserId != activeUserId)
        {
            return AccessSessionSnapshot.Rejected("session-user-mismatch");
        }

        if (browserSession.RevokedAt is not null)
        {
            return AccessSessionSnapshot.Rejected("session-revoked");
        }

        if (browserSession.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return AccessSessionSnapshot.Rejected("session-expired");
        }

        OrganizationChoice? active = null;
        if (selectedOrganization is Guid organizationId)
        {
            foreach (OrganizationChoice choice in resolution.Organizations)
            {
                if (choice.OrganizationId == organizationId)
                {
                    active = choice;
                    break;
                }
            }
        }

        return AccessSessionSnapshot.Active(
            issuer,
            subject,
            displayName,
            activeUserId,
            currentStamp,
            browserSession.Id,
            resolution.Organizations,
            active);
    }

    private static bool TryReadSessionId(ClaimsPrincipal principal, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (!CookieClaimReader.TryGetSingle(principal, FinOpsDeskCookieClaims.SessionId, out string text)
            || !Guid.TryParse(text, out sessionId)
            || sessionId == Guid.Empty)
        {
            sessionId = Guid.Empty;
            return false;
        }

        return true;
    }

    private async Task<AccessPermissionResolution> ResolvePermissionsUncachedAsync(CancellationToken cancellationToken)
    {
        AccessSessionSnapshot snapshot = Current ?? throw new InvalidOperationException("Session snapshot is missing.");
        if (snapshot.Kind != AccessSessionKind.Active)
        {
            return AccessPermissionResolution.Untrusted(snapshot.Reason);
        }

        if (snapshot.ActiveOrganization is null || snapshot.UserId is not Guid userId)
        {
            return AccessPermissionResolution.Resolved(OrganizationRolePermissions.Empty, "no-active-organization");
        }

        try
        {
            IReadOnlyList<OrganizationRole> roles = await _roles.ListActiveAsync(
                userId,
                snapshot.ActiveOrganization.OrganizationId,
                cancellationToken);
            return AccessPermissionResolution.Resolved(OrganizationRolePermissions.Union(roles), "resolved");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string reason = exception is InvalidOperationException { Message: "Organization role assignment is inconsistent." }
                ? "inconsistent-role"
                : "database";
            _logger.LogWarning(
                "Organization role resolution did not complete. Reason: {Reason}",
                reason);
            return AccessPermissionResolution.Unavailable(reason);
        }
    }
}