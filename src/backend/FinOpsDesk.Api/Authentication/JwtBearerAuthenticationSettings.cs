namespace FinOpsDesk.Api.Authentication;

internal sealed class JwtBearerAuthenticationSettings
{
    public const string SectionName = "Authentication:JwtBearer";

    public string? Authority { get; set; }

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SigningKey { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public string OrganizationClaimType { get; set; } = "org_id";
}