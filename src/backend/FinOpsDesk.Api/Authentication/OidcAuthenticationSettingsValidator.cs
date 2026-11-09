using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Authentication;

internal sealed class OidcAuthenticationSettingsValidator
    : IValidateOptions<OidcAuthenticationSettings>
{
    public ValidateOptionsResult Validate(string? name, OidcAuthenticationSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!TryAbsoluteHttpUri(options.Authority, out Uri? authority))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:Authority must be an absolute http or https URI.");
        }

        if (authority!.Scheme != Uri.UriSchemeHttps)
        {
            if (options.RequireHttpsMetadata)
            {
                return ValidateOptionsResult.Fail(
                    "Authentication:Oidc:Authority must use HTTPS when RequireHttpsMetadata is true.");
            }

            if (!authority.IsLoopback)
            {
                return ValidateOptionsResult.Fail(
                    "Authentication:Oidc:Authority must use HTTPS. HTTP is accepted only for a loopback host when RequireHttpsMetadata is false.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:ClientId is required for ProductionCookie.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:ClientSecret is required for ProductionCookie.");
        }

        if (!TryBrowserOrigin(options.BrowserOrigin, out _))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:BrowserOrigin must be an absolute origin with no path, query, or user info. HTTP is accepted only for localhost.");
        }

        if (string.IsNullOrWhiteSpace(options.CallbackPath)
            || !options.CallbackPath.StartsWith('/'))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:CallbackPath must start with '/'.");
        }

        if (new PathString(options.CallbackPath).Equals(new PathString("/ready")))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Oidc:CallbackPath must not match the readiness path /ready.");
        }

        return ValidateOptionsResult.Success;
    }

    internal static bool TryBrowserOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (!TryAbsoluteHttpUri(value, out Uri? uri) || uri is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
        {
            return false;
        }

        bool local = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == Uri.UriSchemeHttp && !local)
        {
            return false;
        }

        origin = uri;
        return true;
    }

    private static bool TryAbsoluteHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}