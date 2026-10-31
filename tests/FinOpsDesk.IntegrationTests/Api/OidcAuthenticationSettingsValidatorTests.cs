using FinOpsDesk.Api.Authentication;

using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class OidcAuthenticationSettingsValidatorTests
{
    private readonly OidcAuthenticationSettingsValidator _validator = new();

    [Fact]
    public void Https_authority_is_accepted()
    {
        ValidateOptionsResult result = _validator.Validate(null, Settings("https://login.example"));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://[::1]:5000")]
    public void Loopback_http_authority_is_accepted_when_https_metadata_is_not_required(string authority)
    {
        ValidateOptionsResult result = _validator.Validate(null, Settings(authority, requireHttpsMetadata: false));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Loopback_http_authority_is_rejected_when_https_metadata_is_required()
    {
        ValidateOptionsResult result = _validator.Validate(null, Settings("http://127.0.0.1:5000"));

        Assert.Contains(
            "must use HTTPS when RequireHttpsMetadata is true",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://idp.example")]
    [InlineData("http://10.1.2.3")]
    public void Remote_http_authority_is_rejected_even_when_https_metadata_is_not_required(string authority)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            Settings(authority, requireHttpsMetadata: false));

        Assert.Contains(
            "HTTP is accepted only for a loopback host",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_client_secret_is_rejected()
    {
        OidcAuthenticationSettings settings = Settings("https://login.example");
        settings.ClientSecret = " ";

        ValidateOptionsResult result = _validator.Validate(null, settings);

        Assert.Contains(
            "Authentication:Oidc:ClientSecret is required",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    private static OidcAuthenticationSettings Settings(string authority, bool requireHttpsMetadata = true)
    {
        return new OidcAuthenticationSettings
        {
            Authority = authority,
            ClientId = "finopsdesk-test-client",
            ClientSecret = "finopsdesk-test-client-secret-not-production",
            BrowserOrigin = "https://localhost",
            RequireHttpsMetadata = requireHttpsMetadata,
        };
    }
}