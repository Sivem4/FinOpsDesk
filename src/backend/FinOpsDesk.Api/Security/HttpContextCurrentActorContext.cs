using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Api.Security;

// DevelopmentJwt keeps the JWT sub claim. ProductionCookie uses ApplicationUser.Id
// and does not fall back to sub when Access validation did not succeed.
internal sealed class HttpContextCurrentActorContext : ICurrentActorContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationCompositionState _state;
    private readonly AccessSessionGate? _sessions;

    public HttpContextCurrentActorContext(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationCompositionState state,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(services);

        _httpContextAccessor = httpContextAccessor;
        _state = state;
        _sessions = state.UsesProductionCookie
            ? services.GetRequiredService<AccessSessionGate>()
            : null;
    }

    public bool TryGetActorId(out string actorId)
    {
        actorId = string.Empty;

        if (_httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!_state.UsesProductionCookie)
        {
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

        if (_sessions?.Current is not { Kind: AccessSessionKind.Active, UserId: Guid userId }
            || userId == Guid.Empty)
        {
            return false;
        }

        actorId = userId.ToString("D");
        return true;
    }
}