using System.Security.Claims;

using FinOpsDesk.Modules.Access.Domain;

using Microsoft.AspNetCore.Antiforgery;

namespace FinOpsDesk.Api.Authentication;

// Binds the request token to the exact issuer and subject. The built-in claim
// uid can follow the sub claim's issuer, which is not this application's identity.
internal sealed class ExternalIdentityAntiforgeryDataProvider : IAntiforgeryAdditionalDataProvider
{
    internal const string AnonymousMarker = "anon";

    internal const string InvalidMarker = "bad";

    public string GetAdditionalData(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return AnonymousMarker;
        }

        return TryIdentity(context.User, out string identity) ? identity : InvalidMarker;
    }

    public bool ValidateAdditionalData(HttpContext context, string additionalData)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(additionalData) || additionalData == InvalidMarker)
        {
            return false;
        }

        string expected = GetAdditionalData(context);
        if (expected == InvalidMarker)
        {
            return false;
        }

        return string.Equals(expected, additionalData, StringComparison.Ordinal);
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out string identity)
    {
        identity = string.Empty;
        if (!CookieClaimReader.TryGetSingle(principal, "iss", out string issuer)
            || !CookieClaimReader.TryGetSingle(principal, "sub", out string subject))
        {
            return false;
        }

        try
        {
            identity = Convert.ToHexStringLower(ExternalIdentityKey.Compute(issuer, subject));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}