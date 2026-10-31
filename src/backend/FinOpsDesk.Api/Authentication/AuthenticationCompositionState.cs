namespace FinOpsDesk.Api.Authentication;

// Host-wide result of the selected composition. Production cookie mode keeps
// financial routes fail-closed until Phase 7C checks organization membership.
internal sealed class AuthenticationCompositionState
{
    public required bool ProductionFinancialAccessFailClosed { get; init; }
}