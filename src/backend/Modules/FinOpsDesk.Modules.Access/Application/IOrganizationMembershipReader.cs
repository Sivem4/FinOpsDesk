namespace FinOpsDesk.Modules.Access.Application;

public interface IOrganizationMembershipReader
{
    Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken);

    Task<MembershipResolution> ResolveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken);

    // Conditional update: rotates the stamp only when the row is still disabled and still has this stamp.
    Task RetireDisabledSessionAsync(
        Guid userId,
        Guid observedStamp,
        CancellationToken cancellationToken);
}