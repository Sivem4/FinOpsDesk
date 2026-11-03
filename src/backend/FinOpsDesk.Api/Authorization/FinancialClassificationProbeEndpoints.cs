using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Api.Authorization;

// Mapped only when Authentication:FinancialClassificationProbes is Enabled.
// Production configuration leaves the key unset, so these routes are not registered.
internal static class FinancialClassificationProbeEndpoints
{
    public const string ConfigurationKey = "Authentication:FinancialClassificationProbes";

    public const string Enabled = "Enabled";

    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.Equals(configuration[ConfigurationKey], Enabled, StringComparison.Ordinal);
    }

    public static RouteGroupBuilder MapFinancialClassificationProbes(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/test/financial-unclassified", () => Results.Text("ran"));
        group.MapGet("/test/financial-unknown", () => Results.Text("ran"))
            .WithMetadata(FinancialEndpointClassification.Read("not.a.permission"));
        group.MapGet("/test/financial-write-permission", () => Results.Text("ran"))
            .WithMetadata(FinancialEndpointClassification.Read(AccessPermission.ReconciliationConfirm.Value));
        group.MapPost("/test/financial-read-on-post", () => Results.Text("ran"))
            .WithMetadata(FinancialEndpointClassification.Read(AccessPermission.InvoicesRead.Value));
        group.MapGet("/test/financial-write-on-get", () => Results.Text("ran"))
            .WithMetadata(FinancialEndpointClassification.Write());
        group.MapPost("/test/financial-write-unlisted", () => Results.Text("ran"))
            .WithMetadata(FinancialEndpointClassification.Write(
                AccessPermission.AllocationsCreate.Value,
                FinancialWriteOperation.UnlistedProbe));
        group.MapGet("/test/financial-allow-anonymous", () => Results.Text("ran"))
            .AllowAnonymous()
            .WithMetadata(FinancialEndpointClassification.Read(AccessPermission.InvoicesRead.Value));

        return group;
    }
}