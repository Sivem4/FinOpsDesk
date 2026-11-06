using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Api.Security;

// The actor id follows HttpContextCurrentActorContext. The source is recorded
// beside that id so a JWT sub is not treated as an ApplicationUser id.
internal sealed class HttpContextCurrentActorAttribution : ICurrentActorAttribution
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationCompositionState _state;
    private readonly AccessSessionGate? _sessions;

    public HttpContextCurrentActorAttribution(
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

    public bool TryGet(out ReviewCaseActorSource source, out string actorId)
    {
        source = default;
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

            source = ReviewCaseActorSource.DevelopmentJwt;
            actorId = trimmed;
            return true;
        }

        if (_sessions?.Current is not { Kind: AccessSessionKind.Active, UserId: Guid userId }
            || userId == Guid.Empty)
        {
            return false;
        }

        source = ReviewCaseActorSource.ProductionCookie;
        actorId = userId.ToString("D");
        return true;
    }
}