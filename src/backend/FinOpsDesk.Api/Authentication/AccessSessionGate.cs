using System.Security.Claims;

using FinOpsDesk.Modules.Access.Application;

using Microsoft.Extensions.Logging;

namespace FinOpsDesk.Api.Authentication;

// One resolution per request. The result is not reused by another request.
internal sealed class AccessSessionGate
{
    private readonly IOrganizationMembershipReader _memberships;
    private readonly ILogger<AccessSessionGate> _logger;
    private Task<AccessSessionSnapshot>? _pending;

    public AccessSessionGate(
        IOrganizationMembershipReader memberships,
        ILogger<AccessSessionGate> logger)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(logger);

        _memberships = memberships;
        _logger = logger;
    }

    public AccessSessionSnapshot? Current { get; private set; }

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

        // A malformed or duplicated selection is not a trusted tenant and does not
        // invalidate the login. Only issuer, subject, and the session stamp do.
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
            resolution.Organizations,
            active);
    }
}