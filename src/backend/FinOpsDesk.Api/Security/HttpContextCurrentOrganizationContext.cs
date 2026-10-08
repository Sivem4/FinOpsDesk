using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Security;

// OrganizationId comes only from the authenticated principal.
// The configured token issuer is trusted to issue that claim only for an
// organization the caller is authorized to act within. See ADR 0004.
internal sealed class HttpContextCurrentOrganizationContext : ICurrentOrganizationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _organizationClaimType;

    public HttpContextCurrentOrganizationContext(
        IHttpContextAccessor httpContextAccessor,
        IOptions<JwtBearerAuthenticationSettings> options)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.OrganizationClaimType);

        _httpContextAccessor = httpContextAccessor;
        _organizationClaimType = options.Value.OrganizationClaimType;
    }

    public OrganizationId OrganizationId
    {
        get
        {
            if (!OrganizationClaimParser.TryGetOrganizationId(
                _httpContextAccessor.HttpContext?.User,
                _organizationClaimType,
                out Guid organizationId))
            {
                throw new InvalidOperationException(
                    "A valid organization claim is required on the authenticated user.");
            }

            return new OrganizationId(organizationId);
        }
    }
}