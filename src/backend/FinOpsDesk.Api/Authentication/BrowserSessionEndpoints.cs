using FinOpsDesk.Modules.Access.Application;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace FinOpsDesk.Api.Authentication;

internal static class BrowserSessionEndpoints
{
    public static void MapFinOpsDeskBrowserSession(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/session", (Delegate)GetSessionAsync)
            .AllowAnonymous();

        app.MapPost("/login", (Delegate)LoginAsync)
            .AllowAnonymous()
            .WithMetadata(new ProductionCsrfMetadata());

        app.MapPost("/logout", (Delegate)LogoutAsync)
            .WithMetadata(new ProductionCsrfMetadata());
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IOrganizationMembershipReader memberships,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(memberships);

        httpContext.Response.Headers.CacheControl = "no-store";
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(httpContext);
        string csrfToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery request token was not issued.");

        AuthenticateResult authentication = await httpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Json(AnonymousSession(csrfToken));
        }

        string? issuer = httpContext.User.FindFirst("iss")?.Value;
        string? subject = httpContext.User.FindFirst("sub")?.Value;
        if (issuer is null || issuer.Length == 0 || subject is null || subject.Length == 0)
        {
            return MembershipUnavailable();
        }

        IReadOnlyList<OrganizationChoice> organizations;
        try
        {
            organizations = await memberships.ListActiveAsync(issuer, subject, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return MembershipUnavailable();
        }

        SessionOrganizationResponse[] choices = new SessionOrganizationResponse[organizations.Count];
        for (int index = 0; index < organizations.Count; index++)
        {
            OrganizationChoice choice = organizations[index];
            choices[index] = new SessionOrganizationResponse(choice.OrganizationId, choice.DisplayName);
        }

        return Results.Json(new SessionResponse(
            Authenticated: true,
            FinancialAccess: false,
            CsrfToken: csrfToken,
            Issuer: issuer,
            Subject: subject,
            DisplayName: httpContext.User.FindFirst("name")?.Value,
            ExpiresAt: authentication.Properties?.ExpiresUtc,
            Organizations: choices));
    }

    private static SessionResponse AnonymousSession(string csrfToken)
    {
        return new SessionResponse(
            Authenticated: false,
            FinancialAccess: false,
            CsrfToken: csrfToken,
            Issuer: null,
            Subject: null,
            DisplayName: null,
            ExpiresAt: null,
            Organizations: []);
    }

    private static IResult MembershipUnavailable()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization membership could not be read.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "auth.membership.unavailable",
            });
    }

    private static async Task<IResult> LoginAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Request.ContentLength is > 4096)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Login request is too large.");
        }

        string? returnUrl = httpContext.Request.Query["returnUrl"];
        if (httpContext.Request.HasFormContentType)
        {
            IFormCollection form = await httpContext.Request.ReadFormAsync();
            if (form.TryGetValue("returnUrl", out StringValues formValue)
                && !StringValuesEmpty(formValue))
            {
                returnUrl = formValue.ToString();
            }
        }

        if (!BrowserReturnUrl.TryNormalize(returnUrl, out string path))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The return URL must be a local path.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "auth.return_url.invalid",
                });
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = path },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static IResult LogoutAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return Results.SignOut(
            new AuthenticationProperties(),
            [CookieAuthenticationDefaults.AuthenticationScheme]);
    }

    private static bool StringValuesEmpty(StringValues value)
    {
        return value.Count == 0 || string.IsNullOrWhiteSpace(value.ToString());
    }

    private sealed record SessionOrganizationResponse(Guid OrganizationId, string DisplayName);

    private sealed record SessionResponse(
        bool Authenticated,
        bool FinancialAccess,
        string CsrfToken,
        string? Issuer,
        string? Subject,
        string? DisplayName,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<SessionOrganizationResponse> Organizations);
}