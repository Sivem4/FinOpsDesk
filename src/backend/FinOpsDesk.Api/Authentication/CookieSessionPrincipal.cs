using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.Cookies;

namespace FinOpsDesk.Api.Authentication;

internal static class CookieSessionPrincipal
{
    public static ClaimsPrincipal Create(
        string issuer,
        string subject,
        string? displayName,
        Guid sessionStamp,
        Guid? organizationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        if (sessionStamp == Guid.Empty)
        {
            throw new ArgumentException("Session stamp cannot be empty.", nameof(sessionStamp));
        }

        var claims = new List<Claim>
        {
            new("iss", issuer, ClaimValueTypes.String, issuer),
            new("sub", subject, ClaimValueTypes.String, issuer),
            new(FinOpsDeskCookieClaims.SessionStamp, sessionStamp.ToString("D"), ClaimValueTypes.String, issuer),
        };

        if (!string.IsNullOrEmpty(displayName) && displayName.Length <= 256)
        {
            claims.Add(new Claim("name", displayName, ClaimValueTypes.String, issuer));
        }

        if (organizationId is Guid organization && organization != Guid.Empty)
        {
            claims.Add(new Claim(
                FinOpsDeskCookieClaims.Organization,
                organization.ToString("D"),
                ClaimValueTypes.String,
                issuer));
        }

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: "name",
            roleType: null);
        return new ClaimsPrincipal(identity);
    }
}