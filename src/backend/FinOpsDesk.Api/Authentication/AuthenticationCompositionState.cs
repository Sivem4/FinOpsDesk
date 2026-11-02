namespace FinOpsDesk.Api.Authentication;

// Host-wide result of the selected composition. ProductionFinancialAccessFailClosed
// is true for ProductionCookie unless FinancialReadAuthorization is Enabled.
// False on DevelopmentJwt keeps the existing bearer path. False on
// ProductionCookie evaluates read permissions and still denies writes.
internal sealed class AuthenticationCompositionState
{
    public required bool UsesProductionCookie { get; init; }

    public required bool ProductionFinancialAccessFailClosed { get; init; }
}