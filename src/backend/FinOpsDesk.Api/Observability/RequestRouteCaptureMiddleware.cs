using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Api.Observability;

internal sealed class RequestRouteCaptureMiddleware
{
    private readonly RequestDelegate _next;

    public RequestRouteCaptureMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint() is RouteEndpoint endpoint
            && !string.IsNullOrEmpty(endpoint.RoutePattern.RawText))
        {
            context.Items[RequestLog.RouteItemKey] = endpoint.RoutePattern.RawText;
        }

        return _next(context);
    }
}