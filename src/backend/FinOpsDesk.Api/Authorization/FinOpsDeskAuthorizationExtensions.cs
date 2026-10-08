using Microsoft.AspNetCore.Authorization;

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
                    policy.AddRequirements(new TenantMemberRequirement());
                });
        });

        services.AddSingleton<IAuthorizationHandler, TenantMemberAuthorizationHandler>();

        return services;
    }
}