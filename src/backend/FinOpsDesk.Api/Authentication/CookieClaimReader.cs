using System.Security.Claims;

namespace FinOpsDesk.Api.Authentication;

internal static class CookieClaimReader
{
    public static bool TryGetSingle(ClaimsPrincipal principal, string claimType, out string value)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(claimType);

        value = string.Empty;
        int count = 0;
        foreach (Claim claim in principal.FindAll(claimType))
        {
            count++;
            if (count > 1)
            {
                value = string.Empty;
                return false;
            }

            value = claim.Value;
        }

        return count == 1 && value.Length > 0;
    }

    public static bool TryGetOptionalGuid(ClaimsPrincipal principal, string claimType, out Guid? organizationId)
    {
        organizationId = null;
        int count = 0;
        string raw = string.Empty;
        foreach (Claim claim in principal.FindAll(claimType))
        {
            count++;
            raw = claim.Value;
        }

        if (count == 0)
        {
            return true;
        }

        if (count != 1
            || !Guid.TryParse(raw, out Guid parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        organizationId = parsed;
        return true;
    }
}