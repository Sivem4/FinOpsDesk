using System.Text;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.RateLimiting;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace FinOpsDesk.Api.OpenApi;

// Documents the authentication composition that this process actually registered.
// An optional header or form field does not make CSRF validation optional.
// OpenAPI cannot express "header or form field" as one required constraint.
internal static class OpenApiAuthenticationTransformer
{
    private const string JwtSchemeName = "DevelopmentJwt";

    private const string CookieSchemeName = "ProductionCookie";

    private const string FormTokenFieldName = "__RequestVerificationToken";

    private const string SessionCookieName = "__Host-finopsdesk-session";

    private const string CsrfFailureNote =
        "CSRF failure also uses HTTP 400 application/problem+json with code auth.csrf.invalid.";

    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer(TransformDocumentAsync);
        options.AddOperationTransformer(TransformOperationAsync);
    }

    private static Task TransformDocumentAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        AuthenticationCompositionState state = context.ApplicationServices
            .GetRequiredService<AuthenticationCompositionState>();
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        if (state.UsesProductionCookie)
        {
            document.Components.SecuritySchemes[CookieSchemeName] = CookieScheme();
        }
        else
        {
            document.Components.SecuritySchemes[JwtSchemeName] = JwtScheme();
        }

        return Task.CompletedTask;
    }

    private static Task TransformOperationAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        string path = NormalizePath(context.Description);
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        AuthenticationCompositionState state = context.ApplicationServices
            .GetRequiredService<AuthenticationCompositionState>();
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
        bool allowAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        bool explicitAuthorization = metadata.OfType<IAuthorizeData>().Any();
        bool requiresAuthentication = !allowAnonymous
            && (explicitAuthorization || FallbackRequiresAuthentication(context.ApplicationServices));
        if (requiresAuthentication)
        {
            RequireScheme(
                operation,
                RequireDocument(context),
                state.UsesProductionCookie ? CookieSchemeName : JwtSchemeName);
        }

        FinancialEndpointClassification? classification = metadata
            .OfType<FinancialEndpointClassification>()
            .SingleOrDefault();
        bool unsafeMethod = !IsSafeMethod(context.Description.HttpMethod);
        bool csrf = state.UsesProductionCookie
            && unsafeMethod
            && metadata.OfType<ProductionCsrfMetadata>().Any();

        if (classification is not null)
        {
            ApplyFinancialOperation(operation, state, classification, csrf, path, context.Description.HttpMethod);
            return Task.CompletedTask;
        }

        if (!state.UsesProductionCookie || !csrf)
        {
            return Task.CompletedTask;
        }

        if (allowAnonymous)
        {
            ApplyLogin(operation);
        }
        else if (!explicitAuthorization)
        {
            ApplyLogout(operation);
        }
        else
        {
            ApplyOrganizationSelection(operation);
        }

        return Task.CompletedTask;
    }

    private static bool FallbackRequiresAuthentication(IServiceProvider services)
    {
        AuthorizationOptions options = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        return options.FallbackPolicy?.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any() == true;
    }

    private static void ApplyFinancialOperation(
        OpenApiOperation operation,
        AuthenticationCompositionState state,
        FinancialEndpointClassification classification,
        bool csrf,
        string path,
        string? method)
    {
        bool allowlisted = ProductionFinancialWriteActivation.IsActivated(classification);
        bool excludedWrite = classification.Kind == FinancialEndpointClassification.OperationKind.Write
            && !allowlisted;
        bool commitGuard = state.UsesProductionCookie && allowlisted;
        bool delegationCatalog = path.Equals("/api/review-cases/delegation-recipients", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        bool handlerProblem = HasProblemContent(operation, "403")
            && !delegationCatalog
            && !(state.UsesProductionCookie && excludedWrite);
        string description = FinancialDescription(state, classification, allowlisted, excludedWrite);
        if (handlerProblem)
        {
            description +=
                " A missing or invalid server actor returns 403 application/problem+json with code reconciliation.actor.required, including an identical replay.";
        }

        if (delegationCatalog)
        {
            description +=
                " ProductionCookie returns 403 application/problem+json with code reconciliation.review_case_assignment.delegation_unavailable before recipient configuration is read.";
        }

        AppendDescription(operation, description);
        string unauthenticated =
            "Authentication challenge. The response has no body. Cache-Control is no-store.";
        if (commitGuard)
        {
            unauthenticated +=
                " A FinancialCommitGuard denial can return application/problem+json titled \"Financial authorization could not be completed.\"";
        }

        DescribeStatus(operation, "401", unauthenticated, preserveProblem: false);
        if (commitGuard)
        {
            AddProblemContent(operation, "401");
        }

        string forbidden =
            "Request-time authorization denial. The response has no body. Cache-Control is no-store.";
        if (handlerProblem)
        {
            forbidden +=
                " The handler can also return application/problem+json with code reconciliation.actor.required.";
        }

        if (commitGuard)
        {
            forbidden +=
                " A FinancialCommitGuard denial can return application/problem+json titled \"Financial authorization could not be completed.\"";
        }

        if (delegationCatalog)
        {
            forbidden =
                "application/problem+json with code reconciliation.review_case_assignment.delegation_unavailable. This denial is the delegation catalog refusal.";
        }

        DescribeStatus(operation, "403", forbidden, preserveProblem: handlerProblem || delegationCatalog);
        DescribeRateLimit(operation, path, method);

        if (!csrf || excludedWrite)
        {
            return;
        }

        AddHeader(operation, FinOpsDeskCsrf.HeaderName, required: true, "Required CSRF token for this JSON write.");
        NoteCsrfFailure(operation);
    }

    private static void DescribeRateLimit(OpenApiOperation operation, string path, string? method)
    {
        if (!OrganizationRateLimitPolicies.TryGetPolicy(method, path, out string policy))
        {
            return;
        }

        string budget = policy switch
        {
            OrganizationRateLimitPolicies.MatchingReads =>
                "Default budget: one shared fixed window of 60 permits per 60 seconds for GET /api/matches, GET /api/matches/smart, and GET /api/matches/batch/preview.",
            OrganizationRateLimitPolicies.MatchingExecute =>
                "Default budget: one fixed window of 10 permits per 60 seconds for POST /api/matches/batch/execute.",
            _ =>
                "Default budget: one shared token bucket of capacity 6 and one token per minute for POST /api/imports/invoices and POST /api/imports/bank-transactions.",
        };
        operation.Responses ??= new OpenApiResponses();
        operation.Responses["429"] = new OpenApiResponse
        {
            Description = budget
                + " The running instance uses the RateLimiting values captured at startup. Changing that configuration requires a restart. QueueLimit is 0 and replenishment is automatic."
                + " Rejection is HTTP 429 application/problem+json with title \"Too many requests.\" and code finopsdesk.rate_limit.exceeded. Cache-Control is no-store. Retry-After is optional.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    },
                },
            },
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Retry-After"] = new OpenApiHeader
                {
                    Description = "Optional whole seconds, rounded up, when the limiter reports a positive delay. Omitted when that delay is missing, zero, or negative.",
                    Required = false,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        };
    }

    private static void ApplyLogin(OpenApiOperation operation)
    {
        AppendDescription(
            operation,
            "Browser sign-in. This operation does not grant a financial capability and does not issue an API access token. "
            + "A valid CSRF token is required through X-CSRF-Token or the form field __RequestVerificationToken. "
            + "Sending both is accepted. OpenAPI cannot mark that alternative as one required value. "
            + "returnUrl is optional. The query value is read first. A non-blank form field replaces it, and a blank form field does not. "
            + "A missing, empty, or whitespace returnUrl normalizes to /.");
        AddHeader(
            operation,
            FinOpsDeskCsrf.HeaderName,
            required: false,
            "Optional CSRF transport. Validation still requires a valid token in this header or the form field.");
        AddQuery(
            operation,
            "returnUrl",
            "Optional. Missing, empty, and whitespace values normalize to /. A non-blank form field takes precedence.");
        operation.RequestBody = FormBody(
            includeReturnUrl: true,
            description:
            "Optional form. A valid CSRF token is still required in this field or in X-CSRF-Token.");
        NoteCsrfFailure(operation);
    }

    private static void ApplyLogout(OpenApiOperation operation)
    {
        AppendDescription(
            operation,
            "Local sign-out. The host fallback policy requires an authenticated caller. This operation does not grant a financial capability. "
            + "Authorization can reject the request before CSRF middleware. "
            + "A valid CSRF token is required through X-CSRF-Token or a normal form field __RequestVerificationToken. "
            + "Sending both is accepted. OpenAPI cannot mark that alternative as one required value. "
            + "The handler does not require a body. A multipart body still needs the header before antiforgery reads the form.");
        AddHeader(
            operation,
            FinOpsDeskCsrf.HeaderName,
            required: false,
            "Optional CSRF transport. Validation still requires a valid token in this header or the form field.");
        operation.RequestBody = FormBody(
            includeReturnUrl: false,
            description:
            "Optional form. A valid CSRF token is still required in this field or in X-CSRF-Token.");
        SetEmptyStatus(
            operation,
            "401",
            "Authorization can reject an unauthenticated request before CSRF middleware. The response has no body. Cache-Control is no-store.");
        NoteCsrfFailure(operation);
    }

    private static void ApplyOrganizationSelection(OpenApiOperation operation)
    {
        AppendDescription(
            operation,
            "Selects the active organization for the authenticated session. This is not a financial capability. "
            + "The JSON body must contain organizationId. A denied selection returns 403 application/problem+json with code auth.organization.forbidden and does not include that organization's name. "
            + "A valid CSRF token is required in X-CSRF-Token. A form token is not a successful request because the body must be JSON.");
        AddHeader(operation, FinOpsDeskCsrf.HeaderName, required: true, "Required CSRF token.");
        operation.RequestBody ??= JsonOrganizationBody();
        if (operation.RequestBody is OpenApiRequestBody organizationBody)
        {
            organizationBody.Required = true;
        }
        SetEmptyStatus(
            operation,
            "401",
            "Authentication challenge. The response has no body. Cache-Control is no-store.");
        operation.Responses ??= new OpenApiResponses();
        operation.Responses["403"] = new OpenApiResponse
        {
            Description = "Organization selection was denied. The body is application/problem+json with code auth.organization.forbidden.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    },
                },
            },
        };
        NoteCsrfFailure(operation);
    }

    private static string FinancialDescription(
        AuthenticationCompositionState state,
        FinancialEndpointClassification classification,
        bool allowlisted,
        bool excludedWrite)
    {
        StringBuilder description = new();
        description.Append("Authentication on this host: ");
        if (state.UsesProductionCookie)
        {
            description.Append(
                "ProductionCookie session and TenantMember. The effective OrganizationId is the active organization from AccessSessionGate. ");
        }
        else
        {
            description.Append(
                "DevelopmentJwt bearer token and TenantMember. The effective OrganizationId is the validated organization claim. This host does not validate CSRF. ");
        }

        description.Append("A client-supplied organization id is not the tenant. Authentication alone does not grant the operation. ");
        if (!string.IsNullOrWhiteSpace(classification.Permission))
        {
            description.Append("Classified permission: ");
            description.Append(classification.Permission);
            description.Append(". ");
        }

        if (!state.UsesProductionCookie)
        {
            description.Append("A consistent classification is sufficient. This host does not read Access roles or activation flags.");
            return description.ToString();
        }

        description.Append(
            "Deployed configuration leaves Authentication:FinancialReadAuthorization and Authentication:FinancialWriteAuthorization unset. ");
        if (classification.Kind == FinancialEndpointClassification.OperationKind.Read)
        {
            description.Append(
                "Reads stay closed until Authentication:FinancialReadAuthorization is Enabled, and then the active membership must hold the classified permission.");
            return description.ToString();
        }

        if (allowlisted)
        {
            description.Append(
                "This write is on the ProductionCookie allowlist (exact confirmation, explicit allocation, or reversal). ");
            description.Append(
                "It also requires Authentication:FinancialWriteAuthorization and the classified permission. A role is not sufficient while the flags are unset.");
            return description.ToString();
        }

        if (excludedWrite)
        {
            description.Append(
                "This write is outside the ProductionCookie allowlist. Authorization returns 403 before CSRF even when the role has the permission and both flags are Enabled. ");
            if (classification.Operation is FinancialWriteOperation.ImportInvoices
                or FinancialWriteOperation.ImportBankTransactions)
            {
                description.Append(
                    "A multipart body that reached CSRF validation would still require the X-CSRF-Token header.");
            }
        }

        return description.ToString();
    }

    private static void RequireScheme(OpenApiOperation operation, OpenApiDocument document, string schemeName)
    {
        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(schemeName, document)] = [],
            },
        ];
    }

    private static void AddHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        operation.Parameters ??= [];
        if (operation.Parameters.OfType<OpenApiParameter>().Any(parameter =>
            parameter.In == ParameterLocation.Header
            && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }

    private static void AddQuery(OpenApiOperation operation, string name, string description)
    {
        operation.Parameters ??= [];
        if (operation.Parameters.OfType<OpenApiParameter>().Any(parameter =>
            parameter.In == ParameterLocation.Query
            && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }

    private static OpenApiRequestBody FormBody(bool includeReturnUrl, string description)
    {
        Dictionary<string, IOpenApiSchema> properties = new()
        {
            [FormTokenFieldName] = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        if (includeReturnUrl)
        {
            properties["returnUrl"] = new OpenApiSchema { Type = JsonSchemaType.String };
        }

        return new OpenApiRequestBody
        {
            Required = false,
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/x-www-form-urlencoded"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = properties,
                    },
                },
            },
        };
    }

    private static OpenApiRequestBody JsonOrganizationBody()
    {
        return new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = false,
                        Required = new HashSet<string> { "organizationId" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["organizationId"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "uuid",
                            },
                        },
                    },
                },
            },
        };
    }

    private static void SetEmptyStatus(OpenApiOperation operation, string status, string description)
    {
        DescribeStatus(operation, status, description, preserveProblem: false);
    }

    private static void DescribeStatus(
        OpenApiOperation operation,
        string status,
        string description,
        bool preserveProblem)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.TryGetValue(status, out IOpenApiResponse? current)
            && current is OpenApiResponse existing)
        {
            existing.Description = description;
            if (!preserveProblem)
            {
                existing.Content?.Clear();
            }

            return;
        }

        operation.Responses[status] = new OpenApiResponse { Description = description };
    }

    private static bool HasProblemContent(OpenApiOperation operation, string status)
    {
        return operation.Responses?.TryGetValue(status, out IOpenApiResponse? current) == true
            && current is OpenApiResponse response
            && response.Content?.ContainsKey("application/problem+json") == true;
    }

    private static void AddProblemContent(OpenApiOperation operation, string status)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.TryGetValue(status, out IOpenApiResponse? current)
            && current is OpenApiResponse existing)
        {
            existing.Content ??= new Dictionary<string, OpenApiMediaType>();
            existing.Content.TryAdd("application/problem+json", ProblemContent());
            return;
        }

        operation.Responses[status] = new OpenApiResponse
        {
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = ProblemContent(),
            },
        };
    }

    private static OpenApiMediaType ProblemContent()
    {
        return new OpenApiMediaType
        {
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        };
    }

    private static void NoteCsrfFailure(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.TryGetValue("400", out IOpenApiResponse? current)
            && current is OpenApiResponse existing)
        {
            if (existing.Description?.Contains("auth.csrf.invalid", StringComparison.Ordinal) != true)
            {
                existing.Description = string.IsNullOrWhiteSpace(existing.Description)
                    ? CsrfFailureNote
                    : existing.Description.TrimEnd() + " " + CsrfFailureNote;
            }

            return;
        }

        operation.Responses["400"] = new OpenApiResponse { Description = CsrfFailureNote };
    }

    private static void AppendDescription(OpenApiOperation operation, string addition)
    {
        if (operation.Description?.Contains("Authentication on this host:", StringComparison.Ordinal) == true
            || operation.Description?.Contains("A valid CSRF token is required", StringComparison.Ordinal) == true)
        {
            return;
        }

        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? addition
            : operation.Description.TrimEnd() + " " + addition;
    }

    private static string NormalizePath(ApiDescription description)
    {
        string path = description.RelativePath ?? string.Empty;
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static OpenApiDocument RequireDocument(OpenApiOperationTransformerContext context)
    {
        return context.Document ?? throw new InvalidOperationException("OpenAPI operation transformer has no document.");
    }

    private static bool IsSafeMethod(string? method)
    {
        return method is not null
            && (HttpMethods.IsGet(method)
                || HttpMethods.IsHead(method)
                || HttpMethods.IsOptions(method)
                || HttpMethods.IsTrace(method));
    }

    private static OpenApiSecurityScheme JwtScheme()
    {
        return new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "DevelopmentJwt access token. This host does not accept the application session cookie.",
        };
    }

    private static OpenApiSecurityScheme CookieScheme()
    {
        return new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = SessionCookieName,
            Description =
                "ProductionCookie session cookie created by the browser OpenID Connect authorization-code flow. It is not an access token and it does not grant financial operations.",
        };
    }
}