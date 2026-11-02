using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

internal sealed class VerifiedAccessProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<VerifiedAccessProbeMiddleware>();
            next(app);
        };
    }
}

internal sealed class VerifiedAccessProbeMiddleware
{
    public const string Path = "/api/test/verified-access";

    private readonly RequestDelegate _next;

    public VerifiedAccessProbeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.Equals(Path, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        AuthenticateResult authentication = await context.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (authentication.Succeeded && authentication.Principal is not null)
        {
            context.User = authentication.Principal;
        }

        AccessSessionGate sessions = context.RequestServices.GetRequiredService<AccessSessionGate>();
        ICurrentActorContext actors = context.RequestServices.GetRequiredService<ICurrentActorContext>();
        ICurrentOrganizationContext organizations = context.RequestServices
            .GetRequiredService<ICurrentOrganizationContext>();

        string? actorId = actors.TryGetActorId(out string resolvedActor) ? resolvedActor : null;
        Guid? organizationId = null;
        bool contextThrew = false;
        try
        {
            organizationId = organizations.OrganizationId.Value;
        }
        catch (InvalidOperationException)
        {
            contextThrew = true;
        }

        AccessSessionSnapshot? snapshot = sessions.Current;
        if (snapshot?.Kind == AccessSessionKind.Unavailable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            status = snapshot?.Kind.ToString() ?? "none",
            contextThrew,
            actorId,
            organizationId,
        });
    }
}