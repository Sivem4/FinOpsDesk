using FinOpsDesk.Api.Authentication;

using Microsoft.AspNetCore.Authorization;

namespace FinOpsDesk.Api.Authorization;

// ProductionCookie stays closed through organization selection. Phase 7D
// replaces this gate. A selected membership is not financial access.
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