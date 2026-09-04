using FinOpsDesk.Modules.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health")
    .AllowAnonymous();

app.Run();

public partial class Program;