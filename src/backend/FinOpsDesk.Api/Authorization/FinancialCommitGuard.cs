using System.Data.Common;
using System.Security.Claims;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Http;

namespace FinOpsDesk.Api.Authorization;

// The guard reads the authenticated cookie principal and the endpoint
// classification. It does not accept an organization, user, role, or actor from
// the request body. DevelopmentJwt never enters ProtectAsync.
internal sealed class FinancialCommitGuard : IFinancialCommitGuard
{
    private readonly AuthenticationCompositionState _state;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOrganizationFinancialAuthorization _authorization;
    private readonly IFinancialCommitLockSync _sync;
    private readonly ILogger<FinancialCommitGuard> _logger;

    public FinancialCommitGuard(
        AuthenticationCompositionState state,
        IHttpContextAccessor httpContextAccessor,
        IOrganizationFinancialAuthorization authorization,
        IFinancialCommitLockSync sync,
        ILogger<FinancialCommitGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);

        _state = state;
        _httpContextAccessor = httpContextAccessor;
        _authorization = authorization;
        _sync = sync;
        _logger = logger;
    }

    public bool RequiresCommitAuthorization
    {
        get
        {
            if (!_state.UsesProductionCookie)
            {
                return false;
            }

            Endpoint? endpoint = _httpContextAccessor.HttpContext?.GetEndpoint();
            IReadOnlyList<FinancialEndpointClassification> classifications = endpoint?.Metadata
                .GetOrderedMetadata<FinancialEndpointClassification>()
                ?? [];
            return classifications.Count == 1
                && classifications[0].Kind == FinancialEndpointClassification.OperationKind.Write;
        }
    }

    public async ValueTask ProtectAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!RequiresCommitAuthorization)
        {
            return;
        }

        if (!_state.ProductionFinancialWritesEnabled)
        {
            throw FinancialCommitAuthorizationException.Forbidden();
        }

        if (!TryReadIdentity(out FinancialCommitIdentity? identity, out AccessPermission permission)
            || identity is null)
        {
            throw identity is null
                ? FinancialCommitAuthorizationException.Unauthenticated()
                : FinancialCommitAuthorizationException.Forbidden();
        }

        try
        {
            await _authorization.AuthorizeSharedAsync(
                transaction.Connection ?? throw FinancialCommitAuthorizationException.Unavailable(),
                transaction,
                identity,
                permission.Value,
                cancellationToken);
        }
        catch (OrganizationAuthorizationException exception)
        {
            _logger.LogWarning(
                "Financial commit authorization failed with {Failure} and lock code {LockReturnCode}.",
                exception.Failure,
                exception.LockReturnCode);
            throw Map(exception);
        }

        await _sync.AfterSharedAuthorizationAsync(cancellationToken);
    }

    private bool TryReadIdentity(out FinancialCommitIdentity? identity, out AccessPermission permission)
    {
        identity = null;
        permission = default;
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        ClaimsPrincipal? principal = httpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        Endpoint? endpoint = httpContext?.GetEndpoint();
        FinancialEndpointClassification? classification = endpoint?.Metadata
            .GetOrderedMetadata<FinancialEndpointClassification>()
            .SingleOrDefault();
        if (classification is null
            || !FinancialWritePermissionCatalog.TryGet(classification.Permission, out permission))
        {
            identity = new FinancialCommitIdentity(string.Empty, string.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
            return false;
        }

        if (!CookieClaimReader.TryGetSingle(principal, "iss", out string issuer)
            || !CookieClaimReader.TryGetSingle(principal, "sub", out string subject)
            || !CookieClaimReader.TryGetSingle(principal, FinOpsDeskCookieClaims.SessionStamp, out string stampText)
            || !Guid.TryParse(stampText, out Guid sessionStamp)
            || sessionStamp == Guid.Empty
            || !CookieClaimReader.TryGetSingle(principal, FinOpsDeskCookieClaims.SessionId, out string sessionText)
            || !Guid.TryParse(sessionText, out Guid sessionId)
            || sessionId == Guid.Empty
            || !CookieClaimReader.TryGetOptionalGuid(principal, FinOpsDeskCookieClaims.Organization, out Guid? organizationId)
            || organizationId is not Guid organization)
        {
            return false;
        }

        identity = new FinancialCommitIdentity(issuer, subject, sessionStamp, sessionId, organization);
        return true;
    }

    private static FinancialCommitAuthorizationException Map(OrganizationAuthorizationException exception)
    {
        return exception.Failure switch
        {
            OrganizationAuthorizationFailure.Unauthenticated => FinancialCommitAuthorizationException.Unauthenticated(),
            OrganizationAuthorizationFailure.Forbidden => FinancialCommitAuthorizationException.Forbidden(),
            OrganizationAuthorizationFailure.LockNotAcquired => FinancialCommitAuthorizationException.LockUnavailable(),
            _ => FinancialCommitAuthorizationException.Unavailable(),
        };
    }
}