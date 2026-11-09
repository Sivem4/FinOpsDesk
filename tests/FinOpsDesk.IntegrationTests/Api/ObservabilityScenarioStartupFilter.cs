using System.Diagnostics;
using System.Text.Json;

using FinOpsDesk.Modules.Reconciliation.Application;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

internal sealed class ObservabilityProbeGate
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public ObservabilityProbeGate()
    {
        Reset();
    }

    public TaskCompletionSource Entered { get; private set; } = null!;

    public TaskCompletionSource Release { get; private set; } = null!;

    public void Reset()
    {
        Entered = NewSource();
        Release = NewSource();
    }

    private static TaskCompletionSource NewSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// Test host only. Sets a route endpoint before routing so the real pipeline
// still performs correlation, exception handling, and request logging.
internal sealed class ObservabilityScenarioStartupFilter : IStartupFilter
{
    public const string SecretMarker = "finopsdesk-secret-marker";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (HttpContext context, RequestDelegate requestDelegate) =>
            {
                if (TryCreateEndpoint(context, out Endpoint? endpoint))
                {
                    context.SetEndpoint(endpoint);
                }

                await requestDelegate(context);
            });
            next(app);
        };
    }

    private static bool TryCreateEndpoint(HttpContext context, out Endpoint? endpoint)
    {
        endpoint = null;
        if (!HttpMethods.IsGet(context.Request.Method)
            || !Scenarios.TryGetValue(context.Request.Path.Value ?? string.Empty, out RequestDelegate? handler))
        {
            return false;
        }

        string path = context.Request.Path.Value!;
        endpoint = new RouteEndpoint(
            handler,
            RoutePatternFactory.Parse(path),
            order: 0,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            path);
        return true;
    }

    private static readonly Dictionary<string, RequestDelegate> Scenarios = new(StringComparer.Ordinal)
    {
        ["/test/observability/bad-request"] = context => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Probe request.").ExecuteAsync(context),
        ["/test/observability/conflict"] = context => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Probe conflict.").ExecuteAsync(context),
        ["/test/observability/missing"] = context => Results.NotFound().ExecuteAsync(context),
        ["/test/observability/boom"] = Boom,
        ["/test/observability/boom-after-start"] = BoomAfterStartAsync,
        ["/test/observability/financial-unavailable"] = FinancialUnavailable,
        ["/test/observability/internal-cancel"] = InternalCancel,
        ["/test/observability/child-activity"] = ChildActivityAsync,
        ["/test/observability/hierarchical"] = HierarchicalActivityAsync,
        ["/test/observability/redirect"] = context => Results.Redirect("/after").ExecuteAsync(context),
        ["/test/observability/abort-for-499"] = AbortFor499Async,
        ["/test/observability/abort-without-status"] = AbortWithoutStatusAsync,
        ["/test/observability/abort-after-start"] = AbortAfterStartAsync,
        ["/test/observability/incomplete-over-abort"] = IncompleteOverAbortAsync,
    };

    private static Task Boom(HttpContext httpContext)
    {
        throw new InvalidOperationException(SecretMarker);
    }

    private static Task FinancialUnavailable(HttpContext httpContext)
    {
        throw FinancialCommitAuthorizationException.Unavailable();
    }

    private static Task InternalCancel(HttpContext httpContext)
    {
        throw new OperationCanceledException("Probe cancellation.");
    }

    private static async Task BoomAfterStartAsync(HttpContext httpContext)
    {
        await httpContext.Response.WriteAsync("started");
        await httpContext.Response.Body.FlushAsync();
        throw new InvalidOperationException(SecretMarker);
    }

    private static async Task ChildActivityAsync(HttpContext httpContext)
    {
        Activity activity = new("finopsdesk.observability.child");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Child activity.")
                .ExecuteAsync(httpContext);
        }
        finally
        {
            activity.Stop();
        }
    }

    private static async Task HierarchicalActivityAsync(HttpContext httpContext)
    {
        Activity activity = new("finopsdesk.observability.hierarchical");
        activity.SetIdFormat(ActivityIdFormat.Hierarchical);
        activity.SetParentId("|" + SecretMarker + ".");
        activity.AddTag("secret", SecretMarker);
        activity.AddBaggage("secret", SecretMarker);
        activity.TraceStateString = "secret=" + SecretMarker;
        activity.Start();
        try
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Hierarchical activity.")
                .ExecuteAsync(httpContext);
        }
        finally
        {
            activity.Stop();
        }
    }

    private static async Task AbortFor499Async(HttpContext httpContext)
    {
        ObservabilityProbeGate gate = httpContext.RequestServices.GetRequiredService<ObservabilityProbeGate>();
        gate.Entered.TrySetResult();
        await Task.Delay(ObservabilityProbeGate.WaitTimeout, httpContext.RequestAborted);
    }

    private static async Task AbortWithoutStatusAsync(HttpContext httpContext)
    {
        ObservabilityProbeGate gate = httpContext.RequestServices.GetRequiredService<ObservabilityProbeGate>();
        gate.Entered.TrySetResult();
        await gate.Release.Task.WaitAsync(ObservabilityProbeGate.WaitTimeout);
    }

    private static async Task AbortAfterStartAsync(HttpContext httpContext)
    {
        await httpContext.Response.WriteAsync("started");
        await httpContext.Response.Body.FlushAsync();
        ObservabilityProbeGate gate = httpContext.RequestServices.GetRequiredService<ObservabilityProbeGate>();
        gate.Entered.TrySetResult();
        await Task.Delay(ObservabilityProbeGate.WaitTimeout, httpContext.RequestAborted);
    }

    private static async Task IncompleteOverAbortAsync(HttpContext httpContext)
    {
        await httpContext.Response.WriteAsync("started");
        await httpContext.Response.Body.FlushAsync();
        ObservabilityProbeGate gate = httpContext.RequestServices.GetRequiredService<ObservabilityProbeGate>();
        gate.Entered.TrySetResult();
        await gate.Release.Task.WaitAsync(ObservabilityProbeGate.WaitTimeout);
        throw new InvalidOperationException(SecretMarker);
    }
}

internal sealed class NewlineTraceIdProblemDetailsWriter : IProblemDetailsWriter
{
    public const string TraceId =
        "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\n";

    public bool CanWrite(ProblemDetailsContext context)
    {
        return true;
    }

    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        JsonSerializerOptions serializer = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .SerializerOptions;
        string key = serializer.PropertyNamingPolicy?.ConvertName("traceId") ?? "traceId";
        context.ProblemDetails.Extensions[key] = TraceId;
        context.HttpContext.RequestServices
            .GetRequiredService<IOptions<ProblemDetailsOptions>>()
            .Value
            .CustomizeProblemDetails
            ?.Invoke(context);

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(context.ProblemDetails);
    }
}