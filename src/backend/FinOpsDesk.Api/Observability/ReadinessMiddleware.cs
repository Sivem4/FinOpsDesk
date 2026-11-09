using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace FinOpsDesk.Api.Observability;

internal sealed class ReadinessMiddleware
{
    private static readonly byte[] ReadyBody = """{"status":"ready"}"""u8.ToArray();

    private static readonly byte[] NotReadyBody = """{"status":"not-ready"}"""u8.ToArray();

    private readonly RequestDelegate _next;
    private readonly IDatabaseReadinessProbe _probe;

    public ReadinessMiddleware(RequestDelegate next, IDatabaseReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(probe);

        _next = next;
        _probe = probe;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsReadyRequest(context))
        {
            await _next(context);
            return;
        }

        context.Items[RequestLog.RouteItemKey] = RequestLog.ReadyRoute;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(DatabaseReadinessProbe.TimeoutSeconds));

        // Classify only after the SQL call returns. Client abort wins over an
        // expired budget. Unexpected probe exceptions and response write
        // failures stay on the host error path.
        bool expectedFailure = false;
        try
        {
            await _probe.ProbeAsync(budget.Token);
        }
        catch (Exception exception) when (IsExpectedProbeFailure(exception))
        {
            expectedFailure = true;
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException(context.RequestAborted);
        }

        if (expectedFailure || budget.IsCancellationRequested)
        {
            await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, NotReadyBody);
            return;
        }

        await WriteAsync(context, StatusCodes.Status200OK, ReadyBody);
    }

    private static bool IsExpectedProbeFailure(Exception exception)
    {
        return exception is OperationCanceledException or TimeoutException or SqlException;
    }

    internal static bool IsReadyRequest(HttpContext context)
    {
        return HttpMethods.IsGet(context.Request.Method)
            && string.Equals(context.Request.Path.Value, "/ready", StringComparison.Ordinal);
    }

    private static Task WriteAsync(HttpContext context, int statusCode, byte[] body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body).AsTask();
    }
}