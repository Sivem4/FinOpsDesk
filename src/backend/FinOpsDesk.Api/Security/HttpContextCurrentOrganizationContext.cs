using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Security;

// DevelopmentJwt reads the validated org_id claim. ProductionCookie reads the
// request-scoped Access result and never the raw finopsdesk_org claim.
internal sealed class HttpContextCurrentOrganizationContext : ICurrentOrganizationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationCompositionState _state;
    private readonly AccessSessionGate? _sessions;
    private readonly string _organizationClaimType;

    public HttpContextCurrentOrganizationContext(
        IHttpContextAccessor httpContextAccessor,
        IOptions<JwtBearerAuthenticationSettings> options,
        AuthenticationCompositionState state,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.OrganizationClaimType);

        _httpContextAccessor = httpContextAccessor;
        _state = state;
        _organizationClaimType = options.Value.OrganizationClaimType;
        _sessions = state.UsesProductionCookie
            ? services.GetRequiredService<AccessSessionGate>()
            : null;
    }

    public OrganizationId OrganizationId
    {
        get
        {
            if (!_state.UsesProductionCookie)
            {
                if (!OrganizationClaimParser.TryGetOrganizationId(
                    _httpContextAccessor.HttpContext?.User,
                    _organizationClaimType,
                    out Guid organizationId))
                {
                    throw new InvalidOperationException(
                        "A valid organization claim is required on the authenticated user.");
                }

                return new OrganizationId(organizationId);
            }

            if (_sessions?.Current is not { Kind: AccessSessionKind.Active, ActiveOrganization: { } active })
            {
                throw new InvalidOperationException(
                    "A validated organization membership is required on the authenticated user.");
            }

            return new OrganizationId(active.OrganizationId);
        }
    }
}