using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FinOpsDesk.Api.Authorization;

internal static class FinOpsDeskAuthorizationExtensions
{
    public static IServiceCollection AddFinOpsDeskAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(
                FinOpsDeskAuthorizationPolicies.TenantMember,
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(
                        new FinancialApiAccessRequirement(),
                        new TenantMemberRequirement());
                });
        });

        services.AddSingleton<IAuthorizationHandler, FinancialApiAccessHandler>();
        services.AddSingleton<IAuthorizationHandler, TenantMemberAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, FinOpsDeskAuthorizationResultHandler>();
        services.AddSingleton<FinancialAnonymousRouteGuardMiddleware>();

        return services;
    }
}