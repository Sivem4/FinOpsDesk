using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Api.Security;

// The development list is read only after the composition fixed when the host
// started. A later change to Authentication:Composition does not reopen it.
// ProductionCookie returns before the delegation section is touched.
internal sealed class ConfigurationReviewCaseDelegationDirectory : IReviewCaseDelegationDirectory
{
    internal const string SectionName = "DevelopmentDelegation";

    private readonly IConfiguration _configuration;
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly AuthenticationCompositionState _composition;

    public ConfigurationReviewCaseDelegationDirectory(
        IConfiguration configuration,
        ICurrentOrganizationContext currentOrganizationContext,
        AuthenticationCompositionState composition)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(composition);

        _configuration = configuration;
        _currentOrganizationContext = currentOrganizationContext;
        _composition = composition;
    }

    public ReviewCaseDelegationDirectoryRead ReadCurrentOrganization()
    {
        if (!IsDevelopmentJwt())
        {
            return ReviewCaseDelegationDirectoryRead.Unavailable.Instance;
        }

        return new ReviewCaseDelegationDirectoryRead.Available(
            List(_currentOrganizationContext.OrganizationId));
    }

    public bool Allows(OrganizationId organizationId, string normalizedRecipientActorId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(normalizedRecipientActorId);

        if (!IsDevelopmentJwt())
        {
            return false;
        }

        foreach (string actorId in List(organizationId))
        {
            if (string.Equals(actorId, normalizedRecipientActorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsDevelopmentJwt()
    {
        return !_composition.UsesProductionCookie;
    }

    private IReadOnlyList<string> List(OrganizationId organizationId)
    {
        IConfigurationSection organizations = _configuration
            .GetSection(SectionName)
            .GetSection("Organizations");
        if (!organizations.Exists())
        {
            return [];
        }

        IConfigurationSection organization = organizations.GetSection(organizationId.Value.ToString("D"));
        if (!organization.Exists())
        {
            return [];
        }

        List<string> actorIds = [];
        foreach (IConfigurationSection child in organization.GetChildren())
        {
            string? raw = child.Value;
            if (raw is null)
            {
                throw new InvalidOperationException(
                    "A development delegation recipient must be a string.");
            }

            string actorId = raw.Trim();
            if (actorId.Length == 0 || actorId.Length > ReviewCaseAssignmentEntry.MaxActorIdLength)
            {
                throw new InvalidOperationException(
                    "A development delegation recipient must be a trimmed value of 1 to 256 characters.");
            }

            if (!actorIds.Contains(actorId, StringComparer.Ordinal))
            {
                actorIds.Add(actorId);
            }
        }

        actorIds.Sort(StringComparer.Ordinal);
        return actorIds;
    }
}