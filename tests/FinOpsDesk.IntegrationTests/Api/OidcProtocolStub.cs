using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FinOpsDesk.IntegrationTests.Api;

public enum OidcTokenFault
{
    None,
    WrongKey,
    WrongIssuer,
    WrongAudience,
    WrongNonce,
    TokenEndpointUnavailable,
}

internal sealed class OidcProtocolStub : IAsyncDisposable
{
    public const string ClientId = "finopsdesk-test-client";

    public const string ClientSecret = "finopsdesk-test-client-secret-not-production";

    public const string RedirectUri = "https://localhost/signin-oidc";

    public const string Subject = "oidc-operator";

    public const string DisplayName = "OIDC Operator";

    public const string RejectedEmail = "operator@example.invalid";

    public const string RejectedOrganizationId = "11111111-1111-1111-1111-111111111111";

    public const string AccessTokenMarker = "test-access-token-not-for-the-browser";

    private readonly WebApplication _app;
    private readonly RSA _signingKey;
    private readonly RSA _wrongKey;
    private readonly string _redirectUri;
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new();
    private string _authority = "";
    private OidcTokenFault _nextFault = OidcTokenFault.None;

    private OidcProtocolStub(WebApplication app, RSA signingKey, RSA wrongKey, string redirectUri)
    {
        _app = app;
        _signingKey = signingKey;
        _wrongKey = wrongKey;
        _redirectUri = redirectUri;
    }

    public string Authority => _authority;

    public static Task<OidcProtocolStub> StartAsync()
    {
        return StartAsync(RedirectUri);
    }

    public static async Task<OidcProtocolStub> StartAsync(string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        RSA signingKey = RSA.Create(2048);
        RSA wrongKey = RSA.Create(2048);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        OidcProtocolStub stub = new(app, signingKey, wrongKey, redirectUri);
        app.MapGet("/.well-known/openid-configuration", stub.Discovery);
        app.MapGet("/jwks", stub.Jwks);
        app.MapGet("/authorize", stub.Authorize);
        app.MapPost("/token", stub.Token);
        await app.StartAsync();

        stub._authority = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single()
            .TrimEnd('/');
        return stub;
    }

    public void ArmNextToken(OidcTokenFault fault)
    {
        _nextFault = fault;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _signingKey.Dispose();
        _wrongKey.Dispose();
    }

    private IResult Discovery(HttpContext httpContext)
    {
        string authority = CurrentAuthority(httpContext);
        return Results.Json(new
        {
            issuer = authority,
            authorization_endpoint = authority + "/authorize",
            token_endpoint = authority + "/token",
            jwks_uri = authority + "/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            code_challenge_methods_supported = new[] { "S256" },
        });
    }

    private IResult Jwks()
    {
        RSAParameters parameters = _signingKey.ExportParameters(false);
        return Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = "test-key",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    private IResult Authorize(HttpContext httpContext)
    {
        IQueryCollection query = httpContext.Request.Query;
        string? responseMode = query["response_mode"];
        if (!string.Equals(query["client_id"], ClientId, StringComparison.Ordinal)
            || !string.Equals(query["redirect_uri"], _redirectUri, StringComparison.Ordinal)
            || !string.Equals(query["response_type"], "code", StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(responseMode)
                && !string.Equals(responseMode, "query", StringComparison.Ordinal))
            || !string.Equals(query["code_challenge_method"], "S256", StringComparison.Ordinal)
            || !query["scope"].ToString().Contains("openid", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(query["state"])
            || string.IsNullOrWhiteSpace(query["nonce"])
            || string.IsNullOrWhiteSpace(query["code_challenge"]))
        {
            return Results.BadRequest();
        }

        if (query["scope"].ToString().Contains("offline_access", StringComparison.Ordinal)
            || query["scope"].ToString().Contains("profile", StringComparison.Ordinal))
        {
            return Results.BadRequest();
        }

        string code = Guid.NewGuid().ToString("N");
        OidcTokenFault fault = _nextFault;
        _nextFault = OidcTokenFault.None;
        _codes[code] = new IssuedCode(
            query["code_challenge"].ToString(),
            query["redirect_uri"].ToString(),
            query["nonce"].ToString(),
            fault);

        string location = _redirectUri
            + "?code=" + Uri.EscapeDataString(code)
            + "&state=" + Uri.EscapeDataString(query["state"].ToString());
        return Results.Redirect(location);
    }

    private IResult Token(HttpContext httpContext)
    {
        IFormCollection form = httpContext.Request.Form;
        if (!_codes.TryRemove(form["code"].ToString(), out IssuedCode? issued))
        {
            return Results.BadRequest();
        }

        if (issued.Fault == OidcTokenFault.TokenEndpointUnavailable)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!string.Equals(form["client_id"], ClientId, StringComparison.Ordinal)
            || !string.Equals(form["client_secret"], ClientSecret, StringComparison.Ordinal)
            || !string.Equals(form["redirect_uri"], issued.RedirectUri, StringComparison.Ordinal)
            || !string.Equals(form["grant_type"], "authorization_code", StringComparison.Ordinal)
            || !PkceMatches(form["code_verifier"].ToString(), issued.CodeChallenge))
        {
            return Results.BadRequest();
        }

        string issuer = issued.Fault == OidcTokenFault.WrongIssuer
            ? "https://evil.invalid"
            : CurrentAuthority(httpContext);
        string audience = issued.Fault == OidcTokenFault.WrongAudience
            ? "other-client"
            : ClientId;
        string nonce = issued.Fault == OidcTokenFault.WrongNonce
            ? "wrong-nonce"
            : issued.Nonce;
        RSA key = issued.Fault == OidcTokenFault.WrongKey ? _wrongKey : _signingKey;
        string kid = issued.Fault == OidcTokenFault.WrongKey ? "other-key" : "test-key";

        string idToken = CreateIdToken(key, kid, issuer, audience, nonce);
        return Results.Json(new
        {
            token_type = "Bearer",
            expires_in = 300,
            id_token = idToken,
            access_token = AccessTokenMarker,
        });
    }

    private string CurrentAuthority(HttpContext httpContext)
    {
        if (!string.IsNullOrEmpty(_authority))
        {
            return _authority;
        }

        return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    }

    private static bool PkceMatches(string verifier, string challenge)
    {
        if (string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        string computed = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return string.Equals(computed, challenge, StringComparison.Ordinal);
    }

    private static string CreateIdToken(RSA key, string kid, string issuer, string audience, string nonce)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(key) { KeyId = kid },
                SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["nonce"] = nonce,
                ["name"] = DisplayName,
                ["email"] = RejectedEmail,
                ["org_id"] = RejectedOrganizationId,
            },
        };

        JwtSecurityTokenHandler handler = new();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private sealed record IssuedCode(
        string CodeChallenge,
        string RedirectUri,
        string Nonce,
        OidcTokenFault Fault);
}