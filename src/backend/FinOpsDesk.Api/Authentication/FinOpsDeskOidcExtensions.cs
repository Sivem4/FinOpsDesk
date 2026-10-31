using System.Security.Claims;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace FinOpsDesk.Api.Authentication;

internal static class FinOpsDeskOidcExtensions
{
    public static IServiceCollection AddFinOpsDeskOpenIdConnect(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<OidcAuthenticationSettings>, OidcAuthenticationSettingsValidator>();
        services.AddOptions<OidcAuthenticationSettings>()
            .Bind(configuration.GetSection(OidcAuthenticationSettings.SectionName))
            .ValidateOnStart();

        services.AddAuthentication()
            .AddOpenIdConnect(ConfigureOpenIdConnect);

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<OidcAuthenticationSettings>>((options, settings) =>
            {
                ApplySettings(options, settings.Value);
            });

        return services;
    }

    public static IServiceCollection AddFinOpsDeskAntiforgery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = FinOpsDeskCsrf.HeaderName;
            options.Cookie.Name = "__Host-finopsdesk-csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        services.AddSingleton<ProductionCsrfMiddleware>();

        return services;
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options)
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.UseTokenLifetime = false;
        options.DisableTelemetry = true;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.UseIfAvailable;
        options.RemoteSignOutPath = PathString.Empty;
        // The handler unprotects state and clears it before the protocol
        // validator runs. RequireStateValidation would reject every callback.
        // State is still required: the handler rejects a missing, unreadable,
        // or uncorrelated state before a cookie is issued.
        options.ProtocolValidator.RequireStateValidation = false;
        options.ProtocolValidator.RequireNonce = true;
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        };

        // Correlation and nonce cookies travel on the cross-site return from the
        // identity provider. They stay SameSite=None and are not copied from the
        // application session cookie.
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.HttpOnly = true;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;

        options.Scope.Clear();
        options.Scope.Add("openid");

        // The handler runs these actions after OnTokenValidated, including a
        // DeleteClaim("iss") action. The filtered principal is the identity
        // that must be stored.
        options.ClaimActions.Clear();

        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
    }

    private static void ApplySettings(OpenIdConnectOptions options, OidcAuthenticationSettings settings)
    {
        if (!OidcAuthenticationSettingsValidator.TryBrowserOrigin(settings.BrowserOrigin, out Uri origin))
        {
            throw new InvalidOperationException(
                "Authentication:Oidc:BrowserOrigin is not a trusted origin.");
        }

        string browserOrigin = origin.GetLeftPart(UriPartial.Authority);
        options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.CallbackPath = settings.CallbackPath;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        options.TokenValidationParameters.ValidAudience = settings.ClientId;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = browserOrigin + options.CallbackPath;
            context.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.Query;
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            ReplacePrincipal(context);
            return Task.CompletedTask;
        };
    }

    private static void ReplacePrincipal(TokenValidatedContext context)
    {
        // The ID token is validated by JsonWebTokenHandler. This ASP.NET Core
        // version converts that result to JwtSecurityToken before the event, so
        // the event token type is not a stable contract. Issuer is read from the
        // validated SecurityToken. Subject and display name come from the
        // principal produced with MapInboundClaims disabled.
        string? issuer = context.SecurityToken?.Issuer;
        string? subject = context.Principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            context.Fail("OIDC identity is missing issuer or subject.");
            return;
        }

        var claims = new List<Claim>
        {
            new("iss", issuer, ClaimValueTypes.String, issuer),
            new("sub", subject, ClaimValueTypes.String, issuer),
        };

        string? name = context.Principal?.FindFirst("name")?.Value;
        if (!string.IsNullOrWhiteSpace(name))
        {
            string trimmed = name.Trim();
            if (trimmed.Length <= 256)
            {
                claims.Add(new Claim("name", trimmed, ClaimValueTypes.String, issuer));
            }
        }

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: "name",
            roleType: null);
        context.Principal = new ClaimsPrincipal(identity);
    }
}