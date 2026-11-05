using System.Net;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class OpenApiAuthenticationDocumentTests
{
    private const string JwtScheme = "DevelopmentJwt";

    private const string CookieScheme = "ProductionCookie";

    private const string CsrfHeader = "X-CSRF-Token";

    private const string FormToken = "__RequestVerificationToken";

    private static readonly (string Path, string Method, string Permission)[] FinancialReads =
    [
        ("/api/invoices", "get", "invoices.read"),
        ("/api/bank-transactions", "get", "bank-transactions.read"),
        ("/api/matches", "get", "reconciliation.read"),
        ("/api/matches/smart", "get", "reconciliation.read"),
        ("/api/matches/batch/preview", "get", "reconciliation.read"),
        ("/api/allocations/context", "get", "allocations.read"),
        ("/api/allocations", "get", "allocations.read"),
        ("/api/allocations/{allocationId}/audit", "get", "audit.read"),
        ("/api/review-cases/{id}/audit", "get", "audit.read"),
        ("/api/allocations/{allocationId}/reversal-context", "get", "allocations.read"),
        ("/api/review-cases", "get", "review-cases.read"),
        ("/api/review-cases/{id}", "get", "review-cases.read"),
    ];

    private static readonly (string Path, string Method, string Permission)[] AllowlistedWrites =
    [
        ("/api/matches/confirm", "post", "reconciliation.confirm"),
        ("/api/allocations", "post", "allocations.create"),
        ("/api/allocations/{allocationId}/reverse", "post", "allocations.reverse"),
    ];

    private static readonly (string Path, string Method, string Permission)[] ExcludedWrites =
    [
        ("/api/imports/invoices", "post", "imports.create"),
        ("/api/imports/bank-transactions", "post", "imports.create"),
        ("/api/matches/batch/execute", "post", "reconciliation.confirm"),
        ("/api/review-cases", "post", "review-cases.open"),
        ("/api/review-cases/{id}/resolve", "post", "review-cases.resolve"),
        ("/api/review-cases/{id}/dismiss", "post", "review-cases.resolve"),
    ];

    private readonly SqlServerFixture _fixture;

    public OpenApiAuthenticationDocumentTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DevelopmentJwt_document_describes_bearer_authentication_without_csrf()
    {
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        JsonElement root = await ReadDocumentAsync(client);

        Assert.False(root.TryGetProperty("security", out _));
        JsonElement scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty(JwtScheme);
        Assert.Single(root.GetProperty("components").GetProperty("securitySchemes").EnumerateObject());
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        Assert.False(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty(CookieScheme, out _));

        JsonElement paths = root.GetProperty("paths");
        Assert.False(paths.TryGetProperty("/login", out _));
        Assert.False(paths.TryGetProperty("/logout", out _));
        Assert.False(paths.TryGetProperty("/api/session", out _));
        Assert.False(paths.TryGetProperty("/signin-oidc", out _));
        AssertNoFinancialProbe(paths);
        AssertHealthHasNoFinancialSecurity(paths);

        foreach ((string path, string method, string permission) in FinancialReads.Concat(AllowlistedWrites).Concat(ExcludedWrites))
        {
            JsonElement operation = paths.GetProperty(path).GetProperty(method);
            AssertScheme(operation, JwtScheme);
            string description = operation.GetProperty("description").GetString() ?? string.Empty;
            Assert.Contains("DevelopmentJwt", description, StringComparison.Ordinal);
            Assert.Contains("does not validate CSRF", description, StringComparison.Ordinal);
            Assert.Contains(permission, description, StringComparison.Ordinal);
            Assert.DoesNotContain("Phase 7", description, StringComparison.Ordinal);
            Assert.DoesNotContain(CsrfHeader, description, StringComparison.Ordinal);
            Assert.False(HasParameter(operation, CsrfHeader));
            AssertEmptyStatus(operation, "401");
            Assert.DoesNotContain(
                "Financial authorization could not be completed",
                StatusDescription(operation, "401"),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Financial authorization could not be completed",
                StatusDescription(operation, "403"),
                StringComparison.Ordinal);
            if (HasHandlerActorForbidden(path, method))
            {
                Assert.Contains("reconciliation.actor.required", description, StringComparison.Ordinal);
                AssertHandlerActorForbidden(operation);
            }
            else
            {
                Assert.DoesNotContain("reconciliation.actor.required", description, StringComparison.Ordinal);
                AssertEmptyStatus(operation, "403");
                Assert.DoesNotContain("reconciliation.actor.required", StatusDescription(operation, "403"), StringComparison.Ordinal);
            }
        }

        AssertReviewCaseAuditDocument(root);

        JsonElement reverse = paths.GetProperty("/api/allocations/{allocationId}/reverse").GetProperty("post");
        Assert.True(Parameter(reverse, "Idempotency-Key").GetProperty("required").GetBoolean());
        Assert.Equal(
            JsonValueKind.False,
            reverse.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("additionalProperties").ValueKind);
        Assert.False(reverse.GetProperty("responses").GetProperty("400").GetProperty("description").GetString()!
            .Contains("auth.csrf.invalid", StringComparison.Ordinal));
        Assert.True(reverse.GetProperty("responses").GetProperty("400").GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    [Fact]
    public async Task ProductionCookie_document_describes_the_session_cookie_and_csrf_transports()
    {
        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync();
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            environmentName: "Development",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = stub.Authority,
                ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            });
        using HttpClient client = factory.CreateBrowserClient();
        await SignInAsync(client);

        JsonElement root = await ReadDocumentAsync(client);

        Assert.False(root.TryGetProperty("security", out _));
        JsonElement scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty(CookieScheme);
        Assert.Single(root.GetProperty("components").GetProperty("securitySchemes").EnumerateObject());
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("cookie", scheme.GetProperty("in").GetString());
        Assert.Equal("__Host-finopsdesk-session", scheme.GetProperty("name").GetString());
        Assert.False(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty(JwtScheme, out _));

        JsonElement paths = root.GetProperty("paths");
        Assert.False(paths.TryGetProperty("/signin-oidc", out _));
        AssertNoFinancialProbe(paths);
        AssertHealthHasNoFinancialSecurity(paths);

        JsonElement session = paths.GetProperty("/api/session").GetProperty("get");
        Assert.False(session.TryGetProperty("security", out _));
        Assert.False(HasParameter(session, CsrfHeader));
        Assert.DoesNotContain("Classified permission", Description(session), StringComparison.Ordinal);

        JsonElement login = paths.GetProperty("/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out _));
        AssertOptionalTransport(login);
        Assert.False(RequiredFlag(Parameter(login, "returnUrl")));
        Assert.Contains("valid CSRF token is required", Description(login), StringComparison.Ordinal);
        Assert.Contains("normalizes to /", Description(login), StringComparison.Ordinal);
        Assert.DoesNotContain("Classified permission", Description(login), StringComparison.Ordinal);
        JsonElement loginBody = login.GetProperty("requestBody");
        Assert.False(RequiredFlag(loginBody));
        JsonElement loginSchema = loginBody.GetProperty("content").GetProperty("application/x-www-form-urlencoded").GetProperty("schema");
        Assert.False(loginSchema.TryGetProperty("required", out _));
        Assert.True(loginSchema.GetProperty("properties").TryGetProperty(FormToken, out _));
        Assert.True(loginSchema.GetProperty("properties").TryGetProperty("returnUrl", out _));

        JsonElement logout = paths.GetProperty("/logout").GetProperty("post");
        AssertScheme(logout, CookieScheme);
        AssertOptionalTransport(logout);
        Assert.Contains("valid CSRF token is required", Description(logout), StringComparison.Ordinal);
        Assert.Contains("before CSRF", Description(logout), StringComparison.Ordinal);
        Assert.Contains("before CSRF", StatusDescription(logout, "401"), StringComparison.Ordinal);
        Assert.DoesNotContain("Classified permission", Description(logout), StringComparison.Ordinal);
        Assert.DoesNotContain("TenantMember", Description(logout), StringComparison.Ordinal);
        Assert.False(RequiredFlag(Parameter(logout, CsrfHeader)));
        JsonElement logoutBody = logout.GetProperty("requestBody");
        Assert.False(RequiredFlag(logoutBody));
        JsonElement logoutSchema = logoutBody.GetProperty("content").GetProperty("application/x-www-form-urlencoded").GetProperty("schema");
        Assert.False(logoutSchema.TryGetProperty("required", out _));
        Assert.False(logoutSchema.GetProperty("properties").TryGetProperty("returnUrl", out _));

        JsonElement organization = paths.GetProperty("/api/session/organization").GetProperty("post");
        AssertScheme(organization, CookieScheme);
        Assert.True(RequiredFlag(Parameter(organization, CsrfHeader)));
        Assert.False(HasParameter(organization, FormToken));
        JsonElement organizationBody = organization.GetProperty("requestBody");
        Assert.True(RequiredFlag(organizationBody));
        JsonElement organizationSchema = organizationBody.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Contains("organizationId", organizationSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("Classified permission", Description(organization), StringComparison.Ordinal);
        Assert.Contains("auth.organization.forbidden", Description(organization), StringComparison.Ordinal);
        Assert.True(organization.GetProperty("responses").GetProperty("403").GetProperty("content").TryGetProperty("application/problem+json", out _));
        AssertEmptyStatus(organization, "401");

        foreach ((string path, string method, string permission) in FinancialReads)
        {
            JsonElement operation = paths.GetProperty(path).GetProperty(method);
            AssertScheme(operation, CookieScheme);
            string description = Description(operation);
            Assert.Contains(permission, description, StringComparison.Ordinal);
            Assert.Contains("FinancialReadAuthorization", description, StringComparison.Ordinal);
            Assert.Contains("unset", description, StringComparison.Ordinal);
            Assert.DoesNotContain("does not validate CSRF", description, StringComparison.Ordinal);
            Assert.False(HasParameter(operation, CsrfHeader));
            AssertEmptyStatus(operation, "401");
            AssertEmptyStatus(operation, "403");
            Assert.DoesNotContain("reconciliation.actor.required", StatusDescription(operation, "403"), StringComparison.Ordinal);
            Assert.DoesNotContain("Financial authorization could not be completed", StatusDescription(operation, "401"), StringComparison.Ordinal);
        }

        AssertReviewCaseAuditDocument(root);

        foreach ((string path, string method, string permission) in AllowlistedWrites)
        {
            JsonElement operation = paths.GetProperty(path).GetProperty(method);
            AssertScheme(operation, CookieScheme);
            string description = Description(operation);
            Assert.Contains(permission, description, StringComparison.Ordinal);
            Assert.Contains("allowlist", description, StringComparison.Ordinal);
            Assert.Contains("FinancialWriteAuthorization", description, StringComparison.Ordinal);
            Assert.Contains("unset", description, StringComparison.Ordinal);
            Assert.Contains("reconciliation.actor.required", description, StringComparison.Ordinal);
            Assert.True(RequiredFlag(Parameter(operation, CsrfHeader)));
            Assert.Contains("auth.csrf.invalid", operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString(), StringComparison.Ordinal);
            AssertHandlerActorForbidden(operation);
            Assert.Contains(
                "Financial authorization could not be completed",
                StatusDescription(operation, "401"),
                StringComparison.Ordinal);
            Assert.Contains("no body", StatusDescription(operation, "401"), StringComparison.Ordinal);
            Assert.True(operation.GetProperty("responses").GetProperty("401").GetProperty("content").TryGetProperty("application/problem+json", out _));
            Assert.Contains(
                "Financial authorization could not be completed",
                StatusDescription(operation, "403"),
                StringComparison.Ordinal);
            Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        }

        foreach (string idempotentPath in new[] { "/api/allocations", "/api/allocations/{allocationId}/reverse", "/api/matches/batch/execute" })
        {
            Assert.True(Parameter(paths.GetProperty(idempotentPath).GetProperty("post"), "Idempotency-Key").GetProperty("required").GetBoolean());
        }

        JsonElement reverse = paths.GetProperty("/api/allocations/{allocationId}/reverse").GetProperty("post");
        Assert.True(reverse.GetProperty("responses").GetProperty("400").GetProperty("content").TryGetProperty("application/problem+json", out _));
        Assert.Equal(
            JsonValueKind.False,
            reverse.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("additionalProperties").ValueKind);

        foreach ((string path, string method, string permission) in ExcludedWrites)
        {
            JsonElement operation = paths.GetProperty(path).GetProperty(method);
            AssertScheme(operation, CookieScheme);
            string description = Description(operation);
            Assert.Contains(permission, description, StringComparison.Ordinal);
            Assert.Contains("before CSRF", description, StringComparison.Ordinal);
            Assert.Contains("allowlist", description, StringComparison.Ordinal);
            Assert.DoesNotContain("reconciliation.actor.required", description, StringComparison.Ordinal);
            Assert.False(HasParameter(operation, CsrfHeader));
            Assert.DoesNotContain(
                "Financial authorization could not be completed",
                StatusDescription(operation, "403"),
                StringComparison.Ordinal);
            Assert.DoesNotContain("reconciliation.actor.required", StatusDescription(operation, "403"), StringComparison.Ordinal);
            AssertEmptyStatus(operation, "403");
        }

        foreach (string importPath in new[] { "/api/imports/invoices", "/api/imports/bank-transactions" })
        {
            string description = Description(paths.GetProperty(importPath).GetProperty("post"));
            Assert.Contains("multipart", description, StringComparison.Ordinal);
            Assert.Contains(CsrfHeader, description, StringComparison.Ordinal);
        }
    }

    private static async Task SignInAsync(HttpClient client)
    {
        HttpResponseMessage session = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using JsonDocument sessionDocument = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        string csrf = sessionDocument.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");

        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        HttpResponseMessage loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        using HttpClient provider = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage authorize = await provider.GetAsync(loginResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        using HttpResponseMessage callback = await client.GetAsync(authorize.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    private static async Task<JsonElement> ReadDocumentAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertScheme(JsonElement operation, string scheme)
    {
        JsonElement requirement = operation.GetProperty("security")[0];
        Assert.True(requirement.TryGetProperty(scheme, out JsonElement scopes), requirement.GetRawText());
        Assert.Equal(JsonValueKind.Array, scopes.ValueKind);
        Assert.Single(requirement.EnumerateObject());
    }

    private static void AssertOptionalTransport(JsonElement operation)
    {
        JsonElement header = Parameter(operation, CsrfHeader);
        Assert.False(RequiredFlag(header));
        string description = Description(operation);
        Assert.Contains(CsrfHeader, description, StringComparison.Ordinal);
        Assert.Contains(FormToken, description, StringComparison.Ordinal);
        Assert.Contains("valid CSRF token is required", description, StringComparison.Ordinal);
        Assert.Contains("auth.csrf.invalid", operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    private static bool HasHandlerActorForbidden(string path, string method)
    {
        return method == "post"
            && path is "/api/matches/confirm"
                or "/api/allocations"
                or "/api/allocations/{allocationId}/reverse"
                or "/api/matches/batch/execute"
                or "/api/review-cases"
                or "/api/review-cases/{id}/resolve"
                or "/api/review-cases/{id}/dismiss";
    }

    private static void AssertHandlerActorForbidden(JsonElement operation)
    {
        JsonElement forbidden = operation.GetProperty("responses").GetProperty("403");
        string description = forbidden.GetProperty("description").GetString() ?? string.Empty;
        Assert.Contains("no body", description, StringComparison.Ordinal);
        Assert.Contains("reconciliation.actor.required", description, StringComparison.Ordinal);
        Assert.True(forbidden.GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    private static string StatusDescription(JsonElement operation, string status)
    {
        return operation.GetProperty("responses").GetProperty(status).GetProperty("description").GetString()
            ?? string.Empty;
    }

    private static void AssertEmptyStatus(JsonElement operation, string status)
    {
        JsonElement response = operation.GetProperty("responses").GetProperty(status);
        if (response.TryGetProperty("content", out JsonElement content))
        {
            Assert.False(content.TryGetProperty("application/problem+json", out _));
            Assert.Empty(content.EnumerateObject());
        }
    }

    private static void AssertHealthHasNoFinancialSecurity(JsonElement paths)
    {
        if (!paths.TryGetProperty("/health", out JsonElement health))
        {
            return;
        }

        JsonElement operation = health.GetProperty("get");
        Assert.False(operation.TryGetProperty("security", out _));
        Assert.DoesNotContain("Classified permission", Description(operation), StringComparison.Ordinal);
    }

    private static void AssertNoFinancialProbe(JsonElement paths)
    {
        Assert.DoesNotContain(
            paths.EnumerateObject(),
            path => path.Name.Contains("/financial-", StringComparison.Ordinal));
    }

    private static JsonElement Parameter(JsonElement operation, string name)
    {
        return operation.GetProperty("parameters").EnumerateArray().Single(parameter =>
            parameter.GetProperty("name").GetString() == name);
    }

    private static bool HasParameter(JsonElement operation, string name)
    {
        return operation.TryGetProperty("parameters", out JsonElement parameters)
            && parameters.EnumerateArray().Any(parameter => parameter.GetProperty("name").GetString() == name);
    }

    private static bool RequiredFlag(JsonElement element)
    {
        return element.TryGetProperty("required", out JsonElement required) && required.GetBoolean();
    }

    private static void AssertReviewCaseAuditDocument(JsonElement root)
    {
        JsonElement operation = root.GetProperty("paths")
            .GetProperty("/api/review-cases/{id}/audit")
            .GetProperty("get");
        string description = Description(operation);
        Assert.Equal("GetReviewCaseAudit", operation.GetProperty("operationId").GetString());
        Assert.Contains("audit.read", description, StringComparison.Ordinal);
        Assert.DoesNotContain("reconciliation.actor.required", description, StringComparison.Ordinal);
        Assert.False(HasParameter(operation, CsrfHeader));
        JsonElement page = Parameter(operation, "page");
        JsonElement pageSize = Parameter(operation, "pageSize");
        Assert.Equal(1, page.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(50, pageSize.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(100, pageSize.GetProperty("schema").GetProperty("maximum").GetInt32());
        JsonElement schema = ResolveSchema(
            root,
            operation.GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        foreach (string name in new[] { "items", "page", "pageSize", "totalCount" })
        {
            Assert.True(schema.GetProperty("properties").TryGetProperty(name, out _), name);
        }

        JsonElement itemSchema = ResolveSchema(root, schema.GetProperty("properties").GetProperty("items").GetProperty("items"));
        foreach (string name in new[]
        {
            "id",
            "action",
            "actorId",
            "recordedAt",
            "reason",
            "invoiceId",
            "bankTransactionId",
            "sourceLabel",
            "note",
            "outcome",
            "allocationId",
            "resolutionNote",
        })
        {
            Assert.True(itemSchema.GetProperty("properties").TryGetProperty(name, out _), name);
        }

        Assert.False(itemSchema.GetProperty("properties").TryGetProperty("organizationId", out _));
        Assert.False(itemSchema.GetProperty("properties").TryGetProperty("reviewCaseId", out _));
        Assert.False(itemSchema.GetProperty("properties").TryGetProperty("concurrencyToken", out _));
    }

    private static JsonElement ResolveSchema(JsonElement root, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out JsonElement reference))
        {
            return schema;
        }

        string name = reference.GetString() ?? string.Empty;
        int slash = name.LastIndexOf('/');
        string schemaName = slash < 0 ? name : name[(slash + 1)..];
        return root.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
    }

    private static string Description(JsonElement operation)
    {
        return operation.TryGetProperty("description", out JsonElement description)
            ? description.GetString() ?? string.Empty
            : string.Empty;
    }
}