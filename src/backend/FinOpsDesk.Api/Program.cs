using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.Security;
using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddFinOpsDeskJwtBearerAuthentication(builder.Configuration);
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

builder.Services.AddReconciliationModule(connectionString);

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health")
    .AllowAnonymous();

app.MapGroup("/api")
    .RequireAuthorization(FinOpsDeskAuthorizationPolicies.TenantMember)
    .MapReconciliationEndpoints();

app.Run();

public partial class Program;