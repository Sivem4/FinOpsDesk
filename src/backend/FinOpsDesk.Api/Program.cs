using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.Observability;
using FinOpsDesk.Api.OpenApi;
using FinOpsDesk.Api.RateLimiting;
using FinOpsDesk.Api.Security;
using FinOpsDesk.Modules.Access;
using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(OpenApiAuthenticationTransformer.Configure);
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        JsonSerializerOptions serializer = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .SerializerOptions;
        string key = serializer.PropertyNamingPolicy?.ConvertName("traceId") ?? "traceId";
        string? accepted = null;
        if (context.ProblemDetails.Extensions.TryGetValue(key, out object? raw) && raw is string traceId)
        {
            accepted = RequestLog.AcceptProblemTraceId(traceId, context.HttpContext.TraceIdentifier);
        }

        context.HttpContext.Items[RequestLog.ProblemTraceIdItemKey] = accepted;
    };
});
builder.Services.AddExceptionHandler<FinOpsDesk.Api.Authorization.FinancialCommitAuthorizationExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddFinOpsDeskAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddFinOpsDeskAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentOrganizationContext, HttpContextCurrentOrganizationContext>();
builder.Services.AddScoped<ICurrentActorContext, HttpContextCurrentActorContext>();
builder.Services.AddScoped<ICurrentActorAttribution, HttpContextCurrentActorAttribution>();
builder.Services.AddScoped<IReviewCaseDelegationDirectory, ConfigurationReviewCaseDelegationDirectory>();

string? connectionString = builder.Configuration.GetConnectionString("FinOpsDesk");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'FinOpsDesk' is not configured.");
}

builder.Services.AddAccessModule(connectionString);
builder.Services.AddReconciliationModule(connectionString);
builder.Services.AddScoped<IFinancialCommitGuard, FinOpsDesk.Api.Authorization.FinancialCommitGuard>();
builder.Services.AddSingleton<IDatabaseReadinessProbe, DatabaseReadinessProbe>();
builder.Services.AddSingleton(static services =>
    RateLimitBudgetConfiguration.Create(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IOrganizationRateLimitPartitioner, ConfiguredOrganizationRateLimitPartitioner>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = RateLimitRejection.WriteAsync;
    options.AddPolicy(
        OrganizationRateLimitPolicies.MatchingReads,
        httpContext => httpContext.RequestServices
            .GetRequiredService<IOrganizationRateLimitPartitioner>()
            .Partition(httpContext, OrganizationRateLimitPolicies.MatchingReads));
    options.AddPolicy(
        OrganizationRateLimitPolicies.MatchingExecute,
        httpContext => httpContext.RequestServices
            .GetRequiredService<IOrganizationRateLimitPartitioner>()
            .Partition(httpContext, OrganizationRateLimitPolicies.MatchingExecute));
    options.AddPolicy(
        OrganizationRateLimitPolicies.Imports,
        httpContext => httpContext.RequestServices
            .GetRequiredService<IOrganizationRateLimitPartitioner>()
            .Partition(httpContext, OrganizationRateLimitPolicies.Imports));
});

var app = builder.Build();

// One validated instance, created before the server listens.
app.Services.GetRequiredService<RateLimitBudgetOptions>();

app.UseMiddleware<RequestCompletionLogMiddleware>();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<RequestRouteCaptureMiddleware>();
app.UseMiddleware<ReadinessMiddleware>();
app.UseAuthentication();
app.UseMiddleware<FinancialAnonymousRouteGuardMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health")
    .AllowAnonymous();

AuthenticationCompositionState composition = app.Services.GetRequiredService<AuthenticationCompositionState>();
if (composition.UsesProductionCookie)
{
    app.UseMiddleware<ProductionCsrfMiddleware>();
    app.MapFinOpsDeskBrowserSession();
}

app.UseMiddleware<ConfirmedOrganizationRateLimitMiddleware>();
app.UseRateLimiter();

var reconciliation = app.MapGroup("/api")
    .RequireAuthorization(FinOpsDeskAuthorizationPolicies.TenantMember);
if (composition.UsesProductionCookie)
{
    reconciliation.WithMetadata(new ProductionCsrfMetadata());
}

OrganizationRateLimitPolicies.Apply(reconciliation);
reconciliation.MapReconciliationEndpoints();
if (FinancialClassificationProbeEndpoints.IsEnabled(app.Configuration))
{
    reconciliation.MapFinancialClassificationProbes();
}

app.Run();

public partial class Program;