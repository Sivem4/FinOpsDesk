namespace FinOpsDesk.Api.Authentication;

// Marker for unsafe ProductionCookie endpoints. The CSRF middleware reads it
// and calls IAntiforgery before the endpoint handler.
internal sealed class ProductionCsrfMetadata
{
}