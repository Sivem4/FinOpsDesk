using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.OpenApi;
using FinOpsDesk.Api.Security;
using FinOpsDesk.Modules.Access;
using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(OpenApiAuthenticationTransformer.Configure);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FinOpsDesk.Api.Authorization.FinancialCommitAuthorizationExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddFinOpsDeskAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddFinOpsDeskAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentOrganizationContext, HttpContextCurrentOrganizationContext>();
builder.Services.AddScoped<ICurrentActorContext, HttpContextCurrentActorContext>();

string? connectionString = builder.Configuration.GetConnectionString("FinOpsDesk");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'FinOpsDesk' is not configured.");
}

builder.Services.AddAccessModule(connectionString);
builder.Services.AddReconciliationModule(connectionString);
builder.Services.AddScoped<IFinancialCommitGuard, FinOpsDesk.Api.Authorization.FinancialCommitGuard>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
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

var reconciliation = app.MapGroup("/api")
    .RequireAuthorization(FinOpsDeskAuthorizationPolicies.TenantMember);
if (composition.UsesProductionCookie)
{
    reconciliation.WithMetadata(new ProductionCsrfMetadata());
}

reconciliation.MapReconciliationEndpoints();
if (FinancialClassificationProbeEndpoints.IsEnabled(app.Configuration))
{
    reconciliation.MapFinancialClassificationProbes();
}

app.Run();

public partial class Program;