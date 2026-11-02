using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Authorization;

internal sealed class TenantMemberAuthorizationHandler
    : AuthorizationHandler<TenantMemberRequirement>
{
    private readonly AuthenticationCompositionState _state;
    private readonly string _organizationClaimType;

    public TenantMemberAuthorizationHandler(
        AuthenticationCompositionState state,
        IOptions<JwtBearerAuthenticationSettings> options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.OrganizationClaimType);

        _state = state;
        _organizationClaimType = options.Value.OrganizationClaimType;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantMemberRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!_state.UsesProductionCookie)
        {
            if (OrganizationClaimParser.TryGetOrganizationId(
                context.User,
                _organizationClaimType,
                out _))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated != true
            || context.Resource is not HttpContext httpContext)
        {
            return Task.CompletedTask;
        }

        AccessSessionGate sessions = httpContext.RequestServices.GetRequiredService<AccessSessionGate>();
        AccessSessionSnapshot? snapshot = sessions.Current;
        if (snapshot?.Kind == AccessSessionKind.Active && snapshot.ActiveOrganization is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}