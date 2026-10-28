using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

namespace FinOpsDesk.Api.Security;

// ActorId comes only from the authenticated JWT sub claim.
// MapInboundClaims is disabled, so the claim type stays "sub".
internal sealed class HttpContextCurrentActorContext : ICurrentActorContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentActorContext(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        _httpContextAccessor = httpContextAccessor;
    }

    public bool TryGetActorId(out string actorId)
    {
        actorId = string.Empty;

        if (_httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        string? claim = _httpContextAccessor.HttpContext.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(claim))
        {
            return false;
        }

        string trimmed = claim.Trim();
        if (trimmed.Length == 0 || trimmed.Length > FinancialAuditEntry.MaxActorIdLength)
        {
            return false;
        }

        actorId = trimmed;
        return true;
    }
}