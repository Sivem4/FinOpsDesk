using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Authorization;

internal sealed class TenantMemberAuthorizationHandler
    : AuthorizationHandler<TenantMemberRequirement>
{
    private readonly string _organizationClaimType;

    public TenantMemberAuthorizationHandler(
        IOptions<JwtBearerAuthenticationSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.OrganizationClaimType);

        _organizationClaimType = options.Value.OrganizationClaimType;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantMemberRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (OrganizationClaimParser.TryGetOrganizationId(
            context.User,
            _organizationClaimType,
            out _))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}