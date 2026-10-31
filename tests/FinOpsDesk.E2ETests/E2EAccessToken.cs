using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.IdentityModel.Tokens;

namespace FinOpsDesk.E2ETests;

internal static class E2EAccessToken
{
    public const string ActorId = "e2e-operator";

    public static string CreateSigningKey()
    {
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        if (key == "finopsdesk-integration-test-signing-key"
            || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("E2E could not create an ephemeral signing key.");
        }

        return key;
    }

    public static string Create(string signingKey, Guid organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id cannot be empty.", nameof(organizationId));
        }

        JwtSecurityToken token = new(
            issuer: E2EApiProcess.Issuer,
            audience: E2EApiProcess.Audience,
            claims:
            [
                new Claim("sub", ActorId),
                new Claim("org_id", organizationId.ToString("D")),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(20),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}