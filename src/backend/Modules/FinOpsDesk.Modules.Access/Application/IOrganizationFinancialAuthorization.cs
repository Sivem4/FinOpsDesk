using System.Data.Common;

namespace FinOpsDesk.Modules.Access.Application;

public sealed record FinancialCommitIdentity(
    string Issuer,
    string Subject,
    Guid SessionStamp,
    Guid BrowserSessionId,
    Guid OrganizationId);

// Runs on the caller's connection and transaction. Shared authorization holds
// its locks until that transaction commits or rolls back. Exclusive acquisition
// is the matching lock for security mutations of the same organization.
public interface IOrganizationFinancialAuthorization
{
    Task AuthorizeSharedAsync(
        DbConnection connection,
        DbTransaction transaction,
        FinancialCommitIdentity identity,
        string permission,
        CancellationToken cancellationToken);

    Task AcquireExclusiveAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        CancellationToken cancellationToken);
}