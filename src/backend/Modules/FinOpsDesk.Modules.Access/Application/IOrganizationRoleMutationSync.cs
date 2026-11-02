namespace FinOpsDesk.Modules.Access.Application;

// Test seam for one transaction. Production uses the no-op registration.
internal interface IOrganizationRoleMutationSync
{
    Task BeforeAuthoritativeLockAsync(CancellationToken cancellationToken);

    Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken);
}