namespace FinOpsDesk.Api.Authentication;

// Host-wide result of the selected composition. ProductionFinancialAccessFailClosed
// is true for ProductionCookie unless FinancialReadAuthorization is Enabled.
// ProductionFinancialWritesEnabled is true only when that read activation and
// FinancialWriteAuthorization are both Enabled. DevelopmentJwt ignores both
// flags. A write flag without the read flag does not open GET or POST.
internal sealed class AuthenticationCompositionState
{
    public required bool UsesProductionCookie { get; init; }

    public required bool ProductionFinancialAccessFailClosed { get; init; }

    public required bool ProductionFinancialWritesEnabled { get; init; }
}