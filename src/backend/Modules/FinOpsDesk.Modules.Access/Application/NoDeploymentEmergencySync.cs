namespace FinOpsDesk.Modules.Access.Application;

internal sealed class NoDeploymentEmergencySync : IDeploymentEmergencySync
{
    public static readonly NoDeploymentEmergencySync Instance = new();

    public Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task BeforeCommitAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}