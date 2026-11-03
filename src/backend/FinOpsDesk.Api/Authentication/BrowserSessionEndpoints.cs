using System.Security.Claims;
using System.Text.Json;

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
    private const int MaximumOrganizationBodyBytes = 4096;
    public static void MapFinOpsDeskBrowserSession(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/session", (Delegate)GetSessionAsync)
            .AllowAnonymous();

        app.MapPost("/api/session/organization", (Delegate)SelectOrganizationAsync)
            .RequireAuthorization()
            .WithMetadata(new ProductionCsrfMetadata());

        app.MapPost("/login", (Delegate)LoginAsync)
            .AllowAnonymous()
            .WithMetadata(new ProductionCsrfMetadata());

        app.MapPost("/logout", (Delegate)LogoutAsync)
            .WithMetadata(new ProductionCsrfMetadata());
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        AccessSessionGate sessions,
        IConfiguration configuration,
        AuthenticationCompositionState composition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(composition);

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Vary = SessionContract.HeaderName;
        bool contractVersion2 = SessionContract.IsVersion2(httpContext.Request);
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(httpContext);
        string csrfToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery request token was not issued.");

        AuthenticateResult authentication = await httpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Json(contractVersion2 ? AnonymousCapabilitySession(csrfToken) : AnonymousSession(csrfToken));
        }

        AccessSessionSnapshot snapshot = sessions.Current
            ?? await sessions.ResolveAsync(httpContext.User, cancellationToken);
        if (snapshot.Kind == AccessSessionKind.Unavailable)
        {
            return MembershipUnavailable();
        }

        if (snapshot.Kind != AccessSessionKind.Active
            || snapshot.Issuer is null
            || snapshot.Subject is null)
        {
            return Results.Json(contractVersion2 ? AnonymousCapabilitySession(csrfToken) : AnonymousSession(csrfToken));
        }

        DateTimeOffset? expiresAt = authentication.Properties?.ExpiresUtc;
        if (!contractVersion2)
        {
            return Results.Json(AuthenticatedSession(csrfToken, snapshot, expiresAt));
        }

        if (!FinancialReadAuthorization.IsEnabled(configuration) || snapshot.ActiveOrganization is null)
        {
            return Results.Json(AuthenticatedCapabilitySession(csrfToken, snapshot, expiresAt, false, []));
        }

        AccessPermissionResolution resolution = await sessions.ResolvePermissionsAsync(cancellationToken);
        if (resolution.Kind != AccessPermissionResolutionKind.Resolved)
        {
            return AuthorizationUnavailable();
        }

        string[] reads = SessionContract.EnabledReads(resolution.Permissions);
        string[] permissions = SessionContract.EnabledCapabilities(
            resolution.Permissions,
            composition.ProductionFinancialWritesEnabled);
        return Results.Json(AuthenticatedCapabilitySession(
            csrfToken,
            snapshot,
            expiresAt,
            reads.Length > 0,
            permissions));
    }

    private static async Task<IResult> SelectOrganizationAsync(
        HttpContext httpContext,
        AccessSessionGate sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(sessions);

        httpContext.Response.Headers.CacheControl = "no-store";
        if (httpContext.Request.ContentLength is > MaximumOrganizationBodyBytes)
        {
            return OrganizationInvalid();
        }

        AuthenticateResult authentication = await httpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded
            || authentication.Principal is null
            || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        AccessSessionSnapshot snapshot = sessions.Current
            ?? await sessions.ResolveAsync(authentication.Principal, cancellationToken);
        if (snapshot.Kind == AccessSessionKind.Unavailable)
        {
            return MembershipUnavailable();
        }

        if (snapshot.Kind != AccessSessionKind.Active
            || snapshot.Issuer is null
            || snapshot.Subject is null
            || snapshot.SessionStamp is not Guid sessionStamp)
        {
            return Results.Unauthorized();
        }

        Guid? requested = await TryReadOrganizationIdAsync(httpContext.Request, cancellationToken);
        if (requested is not Guid organizationId)
        {
            return OrganizationInvalid();
        }

        OrganizationChoice? selected = null;
        foreach (OrganizationChoice choice in snapshot.Organizations)
        {
            if (choice.OrganizationId == organizationId)
            {
                selected = choice;
                break;
            }
        }

        if (selected is null)
        {
            return OrganizationForbidden();
        }

        if (authentication.Properties?.IssuedUtc is not DateTimeOffset issuedUtc
            || authentication.Properties.ExpiresUtc is not DateTimeOffset expiresUtc)
        {
            throw new InvalidOperationException("The application cookie has no absolute expiry.");
        }

        if (snapshot.BrowserSessionId is not Guid sessionId || sessionId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        ClaimsPrincipal principal = CookieSessionPrincipal.Create(
            snapshot.Issuer,
            snapshot.Subject,
            snapshot.DisplayName,
            sessionStamp,
            sessionId,
            organizationId);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issuedUtc,
                ExpiresUtc = expiresUtc,
                IsPersistent = authentication.Properties.IsPersistent,
                AllowRefresh = authentication.Properties.AllowRefresh,
            });
        return Results.NoContent();
    }

    private static async Task<Guid?> TryReadOrganizationIdAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        byte[]? body = await ReadBoundedBodyAsync(request.Body, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int count = 0;
            string? raw = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                count++;
                if (count > 1 || !property.NameEquals("organizationId"u8))
                {
                    return null;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                raw = property.Value.GetString();
            }

            if (count != 1
                || raw is null
                || !Guid.TryParse(raw, out Guid organizationId)
                || organizationId == Guid.Empty)
            {
                return null;
            }

            return organizationId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaximumOrganizationBodyBytes + 1];
        int total = 0;
        while (total <= MaximumOrganizationBodyBytes)
        {
            int read = await body.ReadAsync(
                buffer.AsMemory(total, MaximumOrganizationBodyBytes + 1 - total),
                cancellationToken);
            if (read == 0)
            {
                return total == 0 ? null : buffer.AsSpan(0, total).ToArray();
            }

            total += read;
        }

        return null;
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
            Organizations: [],
            ActiveOrganization: null);
    }

    private static SessionResponse AuthenticatedSession(
        string csrfToken,
        AccessSessionSnapshot snapshot,
        DateTimeOffset? expiresAt)
    {
        SessionOrganizationResponse[] choices = new SessionOrganizationResponse[snapshot.Organizations.Count];
        for (int index = 0; index < snapshot.Organizations.Count; index++)
        {
            OrganizationChoice choice = snapshot.Organizations[index];
            choices[index] = new SessionOrganizationResponse(choice.OrganizationId, choice.DisplayName);
        }

        SessionOrganizationResponse? active = snapshot.ActiveOrganization is OrganizationChoice selected
            ? new SessionOrganizationResponse(selected.OrganizationId, selected.DisplayName)
            : null;
        return new SessionResponse(
            Authenticated: true,
            FinancialAccess: false,
            CsrfToken: csrfToken,
            Issuer: snapshot.Issuer,
            Subject: snapshot.Subject,
            DisplayName: snapshot.DisplayName,
            ExpiresAt: expiresAt,
            Organizations: choices,
            ActiveOrganization: active);
    }

    private static SessionCapabilityResponse AnonymousCapabilitySession(string csrfToken)
    {
        return new SessionCapabilityResponse(
            Authenticated: false,
            FinancialAccess: false,
            CsrfToken: csrfToken,
            Issuer: null,
            Subject: null,
            DisplayName: null,
            ExpiresAt: null,
            Organizations: [],
            ActiveOrganization: null,
            Permissions: []);
    }

    private static SessionCapabilityResponse AuthenticatedCapabilitySession(
        string csrfToken,
        AccessSessionSnapshot snapshot,
        DateTimeOffset? expiresAt,
        bool financialAccess,
        string[] permissions)
    {
        SessionOrganizationResponse[] choices = new SessionOrganizationResponse[snapshot.Organizations.Count];
        for (int index = 0; index < snapshot.Organizations.Count; index++)
        {
            OrganizationChoice choice = snapshot.Organizations[index];
            choices[index] = new SessionOrganizationResponse(choice.OrganizationId, choice.DisplayName);
        }

        SessionOrganizationResponse? active = snapshot.ActiveOrganization is OrganizationChoice selected
            ? new SessionOrganizationResponse(selected.OrganizationId, selected.DisplayName)
            : null;
        return new SessionCapabilityResponse(
            Authenticated: true,
            FinancialAccess: financialAccess,
            CsrfToken: csrfToken,
            Issuer: snapshot.Issuer,
            Subject: snapshot.Subject,
            DisplayName: snapshot.DisplayName,
            ExpiresAt: expiresAt,
            Organizations: choices,
            ActiveOrganization: active,
            Permissions: permissions);
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

    private static IResult AuthorizationUnavailable()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Financial authorization could not be completed.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "auth.authorization.unavailable",
            });
    }

    private static IResult OrganizationInvalid()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Organization selection is not valid.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "auth.organization.invalid",
            });
    }

    private static IResult OrganizationForbidden()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Organization selection was denied.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "auth.organization.forbidden",
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

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        AccessSessionGate sessions,
        IBrowserSessionStore browserSessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(browserSessions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        httpContext.Response.Headers.CacheControl = "no-store";
        AuthenticateResult authentication = await httpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded
            || authentication.Principal is null
            || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        AccessSessionSnapshot snapshot;
        try
        {
            snapshot = sessions.Current
                ?? await sessions.ResolveAsync(authentication.Principal, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (snapshot.Kind == AccessSessionKind.Unavailable)
        {
            return MembershipUnavailable();
        }

        if (snapshot.Kind != AccessSessionKind.Active
            || snapshot.UserId is not Guid userId
            || snapshot.BrowserSessionId is not Guid sessionId)
        {
            return Results.Unauthorized();
        }

        BrowserSessionRevocation revocation;
        try
        {
            revocation = await browserSessions.RevokeAsync(
                sessionId,
                userId,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return MembershipUnavailable();
        }

        if (revocation is not (BrowserSessionRevocation.Revoked or BrowserSessionRevocation.AlreadyRevoked))
        {
            return Results.Unauthorized();
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
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
        IReadOnlyList<SessionOrganizationResponse> Organizations,
        SessionOrganizationResponse? ActiveOrganization);

    private sealed record SessionCapabilityResponse(
        bool Authenticated,
        bool FinancialAccess,
        string CsrfToken,
        string? Issuer,
        string? Subject,
        string? DisplayName,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<SessionOrganizationResponse> Organizations,
        SessionOrganizationResponse? ActiveOrganization,
        IReadOnlyList<string> Permissions);
}