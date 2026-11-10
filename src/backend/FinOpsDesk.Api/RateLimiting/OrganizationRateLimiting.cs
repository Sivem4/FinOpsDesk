using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Observability;
using FinOpsDesk.Api.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.RateLimiting;

internal static class OrganizationRateLimitPolicies
{
    public const string MatchingReads = "matching-reads";

    public const string MatchingExecute = "matching-execute";

    public const string Imports = "imports";

    public const string SectionName = "RateLimiting";

    public static void Apply(IEndpointConventionBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.Add(builder =>
        {
            string? pattern = builder is RouteEndpointBuilder route
                ? route.RoutePattern.RawText
                : null;
            HttpMethodMetadata? methods = builder.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault();
            string? method = methods?.HttpMethods.Count == 1 ? methods.HttpMethods[0] : null;
            if (TryGetPolicy(method, pattern, out string policy))
            {
                builder.Metadata.Add(new EnableRateLimitingAttribute(policy));
            }
        });
    }

    public static bool TryGetPolicy(string? method, string? rawPattern, out string policy)
    {
        policy = string.Empty;
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(rawPattern))
        {
            return false;
        }

        string path = rawPattern.StartsWith('/') ? rawPattern : "/" + rawPattern;
        if (HttpMethods.IsGet(method))
        {
            if (path is "/api/matches" or "/api/matches/smart" or "/api/matches/batch/preview")
            {
                policy = MatchingReads;
                return true;
            }

            return false;
        }

        if (!HttpMethods.IsPost(method))
        {
            return false;
        }

        if (path == "/api/matches/batch/execute")
        {
            policy = MatchingExecute;
            return true;
        }

        if (path is "/api/imports/invoices" or "/api/imports/bank-transactions")
        {
            policy = Imports;
            return true;
        }

        return false;
    }

    public static bool TryReadOrganization(
        HttpContext httpContext,
        AuthenticationCompositionState state,
        string organizationClaimType,
        out Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(state);

        if (!state.UsesProductionCookie)
        {
            return OrganizationClaimParser.TryGetOrganizationId(
                httpContext.User,
                organizationClaimType,
                out organizationId);
        }

        AccessSessionGate? sessions = httpContext.RequestServices.GetService<AccessSessionGate>();
        if (sessions?.Current is { Kind: AccessSessionKind.Active, ActiveOrganization: { } active }
            && active.OrganizationId != Guid.Empty)
        {
            organizationId = active.OrganizationId;
            return true;
        }

        organizationId = Guid.Empty;
        return false;
    }

    public static string PartitionKey(string policy, Guid organizationId)
    {
        return policy + ":" + organizationId.ToString("D");
    }
}

internal interface IOrganizationRateLimitPartitioner
{
    RateLimitPartition<string> Partition(HttpContext httpContext, string policy);
}

internal sealed class ConfiguredOrganizationRateLimitPartitioner : IOrganizationRateLimitPartitioner
{
    private readonly int _matchingReadsPermitLimit;
    private readonly int _matchingReadsWindowSeconds;
    private readonly int _matchingExecutePermitLimit;
    private readonly int _matchingExecuteWindowSeconds;
    private readonly int _importsTokenLimit;
    private readonly int _importsTokensPerPeriod;
    private readonly int _importsReplenishmentSeconds;
    private readonly AuthenticationCompositionState _state;
    private readonly string _organizationClaimType;

    public ConfiguredOrganizationRateLimitPartitioner(
        RateLimitBudgetOptions budget,
        AuthenticationCompositionState state,
        IOptions<JwtBearerAuthenticationSettings> jwt)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(jwt);

        RateLimitBudgetOptions snapshot = budget;
        _matchingReadsPermitLimit = snapshot.MatchingReadsPermitLimit;
        _matchingReadsWindowSeconds = snapshot.MatchingReadsWindowSeconds;
        _matchingExecutePermitLimit = snapshot.MatchingExecutePermitLimit;
        _matchingExecuteWindowSeconds = snapshot.MatchingExecuteWindowSeconds;
        _importsTokenLimit = snapshot.ImportsTokenLimit;
        _importsTokensPerPeriod = snapshot.ImportsTokensPerPeriod;
        _importsReplenishmentSeconds = snapshot.ImportsReplenishmentSeconds;
        _state = state;
        _organizationClaimType = jwt.Value.OrganizationClaimType;
    }

    public RateLimitPartition<string> Partition(HttpContext httpContext, string policy)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!OrganizationRateLimitPolicies.TryReadOrganization(
                httpContext,
                _state,
                _organizationClaimType,
                out Guid organizationId))
        {
            throw new InvalidOperationException("A rate-limit partition requires a confirmed organization.");
        }

        string key = OrganizationRateLimitPolicies.PartitionKey(policy, organizationId);
        if (policy == OrganizationRateLimitPolicies.MatchingReads)
        {
            return FixedWindow(key, _matchingReadsPermitLimit, _matchingReadsWindowSeconds);
        }

        if (policy == OrganizationRateLimitPolicies.MatchingExecute)
        {
            return FixedWindow(key, _matchingExecutePermitLimit, _matchingExecuteWindowSeconds);
        }

        if (policy == OrganizationRateLimitPolicies.Imports)
        {
            return RateLimitPartition.GetTokenBucketLimiter(
                key,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = _importsTokenLimit,
                    TokensPerPeriod = _importsTokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(_importsReplenishmentSeconds),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        }

        throw new InvalidOperationException("The rate-limit policy is not registered.");
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit, int windowSeconds)
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }
}

internal sealed class RateLimitBudgetOptions
{
    public int MatchingReadsPermitLimit { get; set; } = 60;

    public int MatchingReadsWindowSeconds { get; set; } = 60;

    public int MatchingExecutePermitLimit { get; set; } = 10;

    public int MatchingExecuteWindowSeconds { get; set; } = 60;

    public int ImportsTokenLimit { get; set; } = 6;

    public int ImportsTokensPerPeriod { get; set; } = 1;

    public int ImportsReplenishmentSeconds { get; set; } = 60;
}

internal static class RateLimitBudgetConfiguration
{
    public static RateLimitBudgetOptions Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        RateLimitBudgetOptions options = new();
        configuration.GetSection(OrganizationRateLimitPolicies.SectionName).Bind(options);
        ValidateOptionsResult result = new RateLimitBudgetOptionsValidator().Validate(Options.DefaultName, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(RateLimitBudgetOptions),
                result.Failures ?? []);
        }

        return options;
    }
}

internal sealed class RateLimitBudgetOptionsValidator : IValidateOptions<RateLimitBudgetOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        RequirePositive(failures, "RateLimiting:MatchingReadsPermitLimit", options.MatchingReadsPermitLimit);
        RequirePositive(failures, "RateLimiting:MatchingReadsWindowSeconds", options.MatchingReadsWindowSeconds);
        RequirePositive(failures, "RateLimiting:MatchingExecutePermitLimit", options.MatchingExecutePermitLimit);
        RequirePositive(failures, "RateLimiting:MatchingExecuteWindowSeconds", options.MatchingExecuteWindowSeconds);
        RequirePositive(failures, "RateLimiting:ImportsTokenLimit", options.ImportsTokenLimit);
        RequirePositive(failures, "RateLimiting:ImportsTokensPerPeriod", options.ImportsTokensPerPeriod);
        RequirePositive(failures, "RateLimiting:ImportsReplenishmentSeconds", options.ImportsReplenishmentSeconds);
        if (options.ImportsTokensPerPeriod > options.ImportsTokenLimit)
        {
            failures.Add("RateLimiting:ImportsTokensPerPeriod cannot exceed RateLimiting:ImportsTokenLimit.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, string key, int value)
    {
        if (value < 1)
        {
            failures.Add(key + " must be at least 1.");
        }
    }
}

internal static class RateLimitRejection
{
    public const string Code = "finopsdesk.rate_limit.exceeded";

    public const string Title = "Too many requests.";

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.HttpContext;
        HttpResponse response = httpContext.Response;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
            && retryAfter > TimeSpan.Zero)
        {
            double seconds = Math.Ceiling(retryAfter.TotalSeconds);
            if (seconds < 1d)
            {
                seconds = 1d;
            }

            if (seconds <= int.MaxValue)
            {
                response.Headers.RetryAfter = ((int)seconds).ToString(CultureInfo.InvariantCulture);
            }
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/problem+json; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        string requestId = httpContext.TraceIdentifier;
        httpContext.Items[RequestLog.ProblemTraceIdItemKey] =
            RequestLog.AcceptProblemTraceId(requestId, requestId);
        await JsonSerializer.SerializeAsync(
            response.Body,
            new Dictionary<string, object?>
            {
                ["title"] = Title,
                ["status"] = StatusCodes.Status429TooManyRequests,
                ["traceId"] = requestId,
                ["code"] = Code,
            },
            cancellationToken: cancellationToken);
    }
}

internal sealed class ConfirmedOrganizationRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthenticationCompositionState _state;
    private readonly string _organizationClaimType;

    public ConfirmedOrganizationRateLimitMiddleware(
        RequestDelegate next,
        AuthenticationCompositionState state,
        IOptions<JwtBearerAuthenticationSettings> jwt)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(jwt);

        _next = next;
        _state = state;
        _organizationClaimType = jwt.Value.OrganizationClaimType;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null
            && !OrganizationRateLimitPolicies.TryReadOrganization(
                context,
                _state,
                _organizationClaimType,
                out _))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return _next(context);
    }
}