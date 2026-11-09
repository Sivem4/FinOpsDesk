using System.Diagnostics;

using FinOpsDesk.Api.Authentication;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Observability;

internal sealed class RequestCompletionLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;

    public RequestCompletionLogMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _next = next;
        _logger = loggerFactory.CreateLogger(RequestLog.Category);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string requestId = context.TraceIdentifier;
        Stopwatch stopwatch = Stopwatch.StartNew();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestLog.HeaderName] = requestId;
            return Task.CompletedTask;
        });

        Exception? escaped = null;
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            escaped = exception;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            (string outcome, int statusCode) = RequestLog.Classify(
                escaped,
                context.RequestAborted.IsCancellationRequested,
                context.Response.HasStarted,
                context.Response.StatusCode);
            RequestLog.Write(
                _logger,
                new RequestCompletedLog(
                    requestId,
                    ReadProblemTraceId(context),
                    RequestLog.NormalizeMethod(context.Request.Method),
                    ResolveRoute(context),
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    outcome));
        }
    }

    private static string? ReadProblemTraceId(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestLog.ProblemTraceIdItemKey, out object? stored)
            && stored is string traceId)
        {
            return traceId;
        }

        return null;
    }

    private static string ResolveRoute(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestLog.RouteItemKey, out object? stored)
            && stored is string route
            && route.Length > 0)
        {
            return route;
        }

        if (IsOidcCallback(context))
        {
            return RequestLog.OidcCallbackRoute;
        }

        return RequestLog.UnmatchedRoute;
    }

    private static bool IsOidcCallback(HttpContext context)
    {
        AuthenticationCompositionState? composition = context.RequestServices
            .GetService<AuthenticationCompositionState>();
        if (composition is not { UsesProductionCookie: true })
        {
            return false;
        }

        IOptionsMonitor<OpenIdConnectOptions>? monitor = context.RequestServices
            .GetService<IOptionsMonitor<OpenIdConnectOptions>>();
        if (monitor is null)
        {
            return false;
        }

        return monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).CallbackPath
            == context.Request.Path;
    }
}