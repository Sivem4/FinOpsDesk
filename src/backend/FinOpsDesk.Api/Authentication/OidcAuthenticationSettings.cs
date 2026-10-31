namespace FinOpsDesk.Api.Authentication;

internal sealed class OidcAuthenticationSettings
{
    public const string SectionName = "Authentication:Oidc";

    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? BrowserOrigin { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc";

    public bool RequireHttpsMetadata { get; set; } = true;
}