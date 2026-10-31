using FinOpsDesk.Api.Authentication;

using Microsoft.AspNetCore.Authorization;

namespace FinOpsDesk.Api.Authorization;

// ProductionCookie does not accept org_id as membership. The requirement stays
// failed until Phase 7C replaces this gate with a membership check.
internal sealed class FinancialApiAccessHandler
    : AuthorizationHandler<FinancialApiAccessRequirement>
{
    private readonly AuthenticationCompositionState _state;

    public FinancialApiAccessHandler(AuthenticationCompositionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FinancialApiAccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!_state.ProductionFinancialAccessFailClosed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}