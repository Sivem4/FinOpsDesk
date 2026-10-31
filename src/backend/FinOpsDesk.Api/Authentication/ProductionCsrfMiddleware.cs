using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace FinOpsDesk.Api.Authentication;

// Runs after authorization and before the endpoint, including model binding.
// UseAntiforgery does not short-circuit JSON Minimal API handlers by itself.
internal sealed class ProductionCsrfMiddleware : IMiddleware
{
    private readonly IAntiforgery _antiforgery;

    public ProductionCsrfMiddleware(IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(antiforgery);

        _antiforgery = antiforgery;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<ProductionCsrfMetadata>() is null
            || IsSafeMethod(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (IsMultipart(context.Request)
            && !context.Request.Headers.ContainsKey(FinOpsDeskCsrf.HeaderName))
        {
            await WriteFailureAsync(context);
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await WriteFailureAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsSafeMethod(string method)
    {
        return HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method);
    }

    private static bool IsMultipart(HttpRequest request)
    {
        return request.HasFormContentType
            && request.ContentType is not null
            && request.ContentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
    }

    private static Task WriteFailureAsync(HttpContext context)
    {
        return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid CSRF token.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = FinOpsDeskCsrf.ProblemCode,
                })
            .ExecuteAsync(context);
    }
}