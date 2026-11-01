using System.Net;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class OidcProtocolStubRedirectTests
{
    [Fact]
    public async Task Authorize_accepts_only_the_redirect_uri_configured_for_that_stub()
    {
        const string redirectUri = "http://localhost:5173/signin-oidc";
        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync(redirectUri);
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage accepted = await client.GetAsync(AuthorizeUrl(stub, redirectUri));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.StartsWith(
            redirectUri + "?code=",
            accepted.Headers.Location?.AbsoluteUri,
            StringComparison.Ordinal);

        using HttpResponseMessage rejected = await client.GetAsync(AuthorizeUrl(stub, OidcProtocolStub.RedirectUri));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private static string AuthorizeUrl(OidcProtocolStub stub, string redirectUri)
    {
        return stub.Authority
            + "/authorize?client_id=" + OidcProtocolStub.ClientId
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&response_type=code&scope=openid&state=state-value&nonce=nonce-value"
            + "&code_challenge=challenge&code_challenge_method=S256";
    }
}