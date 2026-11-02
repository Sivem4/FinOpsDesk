using FinOpsDesk.AccessBootstrap;
using FinOpsDesk.Modules.Access;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;

if (!BootstrapCommandLine.TryParse(args, out BootstrapInvocation? invocation, out string? parseFailure))
{
    Console.WriteLine("result=" + parseFailure);
    return 1;
}

string? connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FinOpsDesk");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("result=not-configured");
    return 1;
}

try
{
    ServiceCollection services = new();
    services.AddAccessModule(connectionString);
    services.AddScoped<IDeploymentRoleBootstrap, DeploymentRoleBootstrap>();
    await using ServiceProvider provider = services.BuildServiceProvider();
    await using AsyncServiceScope scope = provider.CreateAsyncScope();
    IDeploymentRoleBootstrap bootstrap = scope.ServiceProvider.GetRequiredService<IDeploymentRoleBootstrap>();
    DeploymentCommandStatus status = await DispatchAsync(bootstrap, invocation!);
    Console.WriteLine("result=" + Token(status));
    return Succeeded(status) ? 0 : 1;
}
catch (OperationCanceledException)
{
    throw;
}
catch (Exception)
{
    Console.WriteLine("result=unavailable");
    return 1;
}

static async Task<DeploymentCommandStatus> DispatchAsync(
    IDeploymentRoleBootstrap bootstrap,
    BootstrapInvocation invocation)
{
    return invocation.Command switch
    {
        "create-organization" => await bootstrap.CreateOrganizationAsync(
            invocation.OrganizationId,
            invocation.Name ?? string.Empty,
            CancellationToken.None),
        "grant-membership" => await bootstrap.GrantMembershipAsync(
            invocation.Issuer ?? string.Empty,
            invocation.Subject ?? string.Empty,
            invocation.OrganizationId,
            CancellationToken.None),
        "grant-role" => await bootstrap.GrantRoleAsync(
            invocation.Issuer ?? string.Empty,
            invocation.Subject ?? string.Empty,
            invocation.OrganizationId,
            invocation.Role,
            invocation.OperatorName,
            CancellationToken.None),
        "revoke-role" => await bootstrap.RevokeRoleAsync(
            invocation.Issuer ?? string.Empty,
            invocation.Subject ?? string.Empty,
            invocation.OrganizationId,
            invocation.Role,
            invocation.OperatorName,
            CancellationToken.None),
        _ => DeploymentCommandStatus.InvalidRole,
    };
}

static string Token(DeploymentCommandStatus status)
{
    return status switch
    {
        DeploymentCommandStatus.OrganizationCreated => "organization-created",
        DeploymentCommandStatus.OrganizationAlreadyExists => "organization-already-exists",
        DeploymentCommandStatus.InvalidOrganizationName => "invalid-organization-name",
        DeploymentCommandStatus.MembershipGranted => "membership-granted",
        DeploymentCommandStatus.MembershipAlreadyActive => "membership-already-active",
        DeploymentCommandStatus.UserNotFound => "user-not-found",
        DeploymentCommandStatus.OrganizationNotFound => "organization-not-found",
        DeploymentCommandStatus.MembershipInactive => "membership-inactive",
        DeploymentCommandStatus.MembershipNotFound => "membership-not-found",
        DeploymentCommandStatus.RoleGranted => "role-granted",
        DeploymentCommandStatus.RoleAlreadyActive => "role-already-active",
        DeploymentCommandStatus.RoleRevoked => "role-revoked",
        DeploymentCommandStatus.RoleNotActive => "role-not-active",
        DeploymentCommandStatus.LastOrganizationAdmin => "last-organization-admin",
        DeploymentCommandStatus.DisabledUser => "disabled-user",
        DeploymentCommandStatus.DisabledOrganization => "disabled-organization",
        DeploymentCommandStatus.InvalidRole => "invalid-role",
        DeploymentCommandStatus.InvalidOperator => "invalid-operator",
        DeploymentCommandStatus.ConcurrencyConflict => "concurrency-conflict",
        _ => "unavailable",
    };
}

static bool Succeeded(DeploymentCommandStatus status)
{
    return status is DeploymentCommandStatus.OrganizationCreated
        or DeploymentCommandStatus.OrganizationAlreadyExists
        or DeploymentCommandStatus.MembershipGranted
        or DeploymentCommandStatus.MembershipAlreadyActive
        or DeploymentCommandStatus.RoleGranted
        or DeploymentCommandStatus.RoleAlreadyActive
        or DeploymentCommandStatus.RoleRevoked
        or DeploymentCommandStatus.RoleNotActive;
}