using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace FinOpsDesk.Api.Authorization;

// AllowAnonymous would skip the financial policy. A classified financial
// route, or any route inside the Reconciliation mapping boundary, stays denied.
internal sealed class FinancialAnonymousRouteGuardMiddleware : IMiddleware
{
    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            && (endpoint.Metadata.GetMetadata<FinancialEndpointClassification>() is not null
                || endpoint.Metadata.GetMetadata<ReconciliationRouteBoundary>() is not null))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}