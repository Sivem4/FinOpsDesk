namespace FinOpsDesk.Modules.Access.Application;

// Test seam. Production uses the no-op. A throw before commit must leave both
// the security row and the audit event uncommitted.
internal interface IDeploymentEmergencySync
{
    Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken);

    Task BeforeCommitAsync(CancellationToken cancellationToken);
}