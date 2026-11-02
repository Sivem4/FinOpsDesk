namespace FinOpsDesk.Api.Authentication;

// Host-wide result of the selected composition. Production cookie mode keeps
// financial routes fail-closed until Phase 7D.
internal sealed class AuthenticationCompositionState
{
    public required bool UsesProductionCookie { get; init; }

    public required bool ProductionFinancialAccessFailClosed { get; init; }
}