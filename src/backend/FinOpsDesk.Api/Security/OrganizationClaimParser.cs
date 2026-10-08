using System.Security.Claims;

namespace FinOpsDesk.Api.Security;

// Shared by tenant authorization and ICurrentOrganizationContext so claim type,
// Guid parsing, and Guid.Empty handling cannot drift.
internal static class OrganizationClaimParser
{
    public static bool TryGetOrganizationId(
        ClaimsPrincipal? principal,
        string organizationClaimType,
        out Guid organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationClaimType);

        organizationId = Guid.Empty;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        string? claimValue = principal.FindFirst(organizationClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(claimValue))
        {
            return false;
        }

        return Guid.TryParse(claimValue, out organizationId)
            && organizationId != Guid.Empty;
    }
}