namespace FinOpsDesk.Modules.Access.Application;

internal sealed class NoOrganizationRoleMutationSync : IOrganizationRoleMutationSync
{
    public static readonly NoOrganizationRoleMutationSync Instance = new();

    public Task BeforeAuthoritativeLockAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}