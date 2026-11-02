using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Api.Authentication;

internal static class FinOpsDeskCookieAuthenticationExtensions
{
    public static IServiceCollection AddFinOpsDeskCookieAuthentication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<AccessSessionGate>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // __Host- requires Secure, Path=/, and no Domain attribute.
                options.Cookie.Name = "__Host-finopsdesk-session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);

                // API clients receive status codes. Phase 7A has no sign-in route.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnSigningOut = context =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    if (context.Request.Path.Equals("/logout", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                    }

                    return Task.CompletedTask;
                };
                // RequestRefresh rewrites ExpiresUtc to now plus the original
                // lifetime. Validation must not set ShouldRenew.
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
                options.Events.OnRedirectToReturnUrl = context =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        AccessSessionGate sessions = context.HttpContext.RequestServices
            .GetRequiredService<AccessSessionGate>();
        AccessSessionSnapshot snapshot = await sessions.ResolveAsync(
            context.Principal ?? new ClaimsPrincipal(),
            context.HttpContext.RequestAborted);
        if (snapshot.Kind is not (AccessSessionKind.Rejected or AccessSessionKind.InvalidIdentity))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}