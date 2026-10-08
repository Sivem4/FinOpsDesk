using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Microsoft.IdentityModel.Tokens;

namespace FinOpsDesk.IntegrationTests.Api;

internal static class TestAccessTokens
{
    public const string Issuer = "https://finopsdesk-tests.local";

    public const string Audience = "finopsdesk-api";

    public const string SigningKey = "finopsdesk-integration-test-signing-key";

    public const string OrganizationClaimType = "org_id";

    public static string Create(Guid organizationId)
    {
        return Create(
            includeOrganizationClaim: true,
            organizationClaimValue: organizationId.ToString());
    }

    public static string CreateWithoutOrganizationClaim()
    {
        return Create(includeOrganizationClaim: false, organizationClaimValue: null);
    }

    public static string CreateWithOrganizationClaim(string organizationClaimValue)
    {
        return Create(
            includeOrganizationClaim: true,
            organizationClaimValue: organizationClaimValue);
    }

    public static string CreateSignedWithOtherKey(Guid organizationId)
    {
        return Create(
            includeOrganizationClaim: true,
            organizationClaimValue: organizationId.ToString(),
            signingKey: "different-finopsdesk-test-signing-key");
    }

    private static string Create(
        bool includeOrganizationClaim,
        string? organizationClaimValue,
        string? signingKey = null)
    {
        List<Claim> claims =
        [
            new("sub", "test-user"),
        ];

        if (includeOrganizationClaim)
        {
            claims.Add(new Claim(
                OrganizationClaimType,
                organizationClaimValue ?? string.Empty));
        }

        string key = signingKey ?? SigningKey;
        JwtSecurityToken token = new(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}