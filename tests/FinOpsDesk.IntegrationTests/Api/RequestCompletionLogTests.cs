using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.Observability;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class RequestCompletionLogTests : IDisposable
{
    private const string Marker = ObservabilityScenarioStartupFilter.SecretMarker;

    private const string UnusedConnection =
        "Server=(localdb)\\mssqllocaldb;Database=FinOpsDesk_IT_observability_unused;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly Regex W3C = new(
        @"\A[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RequestLogCollector _collector = new();
    private readonly ScriptedReadinessProbe _probe = new();
    private readonly FinOpsDeskApiFactory _factory;

    public RequestCompletionLogTests()
    {
        _factory = new FinOpsDeskApiFactory(
            UnusedConnection,
            services =>
            {
                services.RemoveAll<IDatabaseReadinessProbe>();
                services.AddSingleton<IDatabaseReadinessProbe>(_probe);
                services.AddSingleton<ILoggerProvider>(_collector);
                services.AddSingleton<ObservabilityProbeGate>();
                services.AddSingleton<IStartupFilter, ObservabilityScenarioStartupFilter>();
                services.AddSingleton<IExceptionHandler, SelectiveRethrowExceptionHandler>();
            },
            financialClassificationProbesEnabled: true);
    }

    [Fact]
    public async Task Health_and_ready_each_emit_one_completed_event()
    {
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();

        HttpResponseMessage health = await client.GetAsync("/health");
        RequestLogEntry healthLog = AssertSingle(200, RequestLog.OutcomeCompleted, "/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", (await health.Content.ReadAsStringAsync()).Trim());
        AssertHeader(health, healthLog);

        _collector.Clear();
        HttpResponseMessage ready = await client.GetAsync("/ready?q=" + Marker);
        string readyPayload = await ready.Content.ReadAsStringAsync();
        RequestLogEntry readyLog = AssertSingle(200, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("no-store", ready.Headers.CacheControl?.ToString());
        using JsonDocument readyBody = JsonDocument.Parse(readyPayload);
        Assert.Equal("ready", readyBody.RootElement.GetProperty("status").GetString());
        AssertHeader(ready, readyLog);
        AssertNoMarker(readyLog);
        Assert.DoesNotContain(Marker, readyPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_matrix_keeps_one_event_and_the_request_header()
    {
        using HttpClient client = _factory.CreateApiClient();
        await AssertCompleted(client, "/test/observability/redirect", HttpStatusCode.Redirect, 302, "/test/observability/redirect");
        await AssertCompleted(client, "/test/observability/bad-request", HttpStatusCode.BadRequest, 400, "/test/observability/bad-request", problem: true);
        await AssertCompleted(client, "/api/invoices", HttpStatusCode.Unauthorized, 401, "/api/invoices", bearer: false);
        await AssertCompleted(client, "/api/test/financial-allow-anonymous", HttpStatusCode.Forbidden, 403, "/api/test/financial-allow-anonymous", bearer: false);
        await AssertCompleted(client, "/test/observability/missing", HttpStatusCode.NotFound, 404, "/test/observability/missing");
        await AssertCompleted(client, "/test/observability/conflict", HttpStatusCode.Conflict, 409, "/test/observability/conflict", problem: true);
        await AssertCompleted(client, "/test/observability/financial-unavailable", HttpStatusCode.ServiceUnavailable, 503, "/test/observability/financial-unavailable", problem: true);
        await AssertCompleted(client, "/test/observability/boom", HttpStatusCode.InternalServerError, 500, "/test/observability/boom", problem: true);
    }

    [Fact]
    public async Task Empty_401_and_403_have_no_body()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage unauthorized = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(string.Empty, await unauthorized.Content.ReadAsStringAsync());

        HttpResponseMessage forbidden = await client.GetAsync("/api/test/financial-allow-anonymous");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(string.Empty, await forbidden.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Child_activity_trace_is_captured_before_it_stops()
    {
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/test/observability/child-activity");
        RequestLogEntry entry = AssertSingle(400, RequestLog.OutcomeCompleted, "/test/observability/child-activity");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string traceId = body.RootElement.GetProperty("traceId").GetString()!;
        Assert.Equal(traceId, Assert.IsType<string>(entry.State["problemTraceId"]));
        Assert.Matches(W3C, traceId);
        Assert.NotEqual(entry.State["requestId"], traceId);
        AssertHeader(response, entry);
    }

    [Fact]
    public async Task Text_accept_skips_the_problem_writer()
    {
        using HttpClient client = _factory.CreateApiClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/test/observability/boom");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        _collector.Clear();
        HttpResponseMessage response = await client.SendAsync(request);
        RequestLogEntry entry = AssertSingle(500, RequestLog.OutcomeCompleted, "/test/observability/boom");
        Assert.Null(entry.State["problemTraceId"]);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertHeader(response, entry);
    }

    [Fact]
    public async Task Hierarchical_trace_id_stays_out_of_the_event()
    {
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/test/observability/hierarchical");
        string body = await response.Content.ReadAsStringAsync();
        RequestLogEntry entry = AssertSingle(400, RequestLog.OutcomeCompleted, "/test/observability/hierarchical");
        Assert.Contains(Marker, body, StringComparison.Ordinal);
        Assert.Null(entry.State["problemTraceId"]);
        AssertNoMarker(entry);
        AssertHeader(response, entry);
        Assert.DoesNotContain(Marker, response.Headers.GetValues(RequestLog.HeaderName).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_requests_keep_separate_request_ids()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore.Hosting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        Task<HttpResponseMessage> first = SendAsync(client);
        Task<HttpResponseMessage> second = SendAsync(client);
        HttpResponseMessage[] responses = await Task.WhenAll(first, second);
        IReadOnlyList<RequestLogEntry> entries = await _collector.WaitForCountAsync(2);
        Assert.Equal(2, entries.Count);
        string[] requestIds = entries.Select(entry => Assert.IsType<string>(entry.State["requestId"])).ToArray();
        Assert.NotEqual(requestIds[0], requestIds[1]);
        foreach (HttpResponseMessage response in responses)
        {
            string header = response.Headers.GetValues(RequestLog.HeaderName).Single();
            RequestLogEntry entry = Assert.Single(entries, candidate => (string?)candidate.State["requestId"] == header);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(body.RootElement.GetProperty("traceId").GetString(), entry.State["problemTraceId"]);
        }
    }

    [Fact]
    public async Task Client_abort_before_the_response_reports_platform_499()
    {
        await AssertAbortAsync("/test/observability/abort-for-499", 499, RequestLog.OutcomeCancelled, "/test/observability/abort-for-499");
    }

    [Fact]
    public async Task Client_abort_without_a_platform_status_reports_zero()
    {
        await AssertAbortAsync("/test/observability/abort-without-status", 0, RequestLog.OutcomeCancelled, "/test/observability/abort-without-status", release: true);
    }

    [Fact]
    public async Task Client_abort_after_the_response_started_keeps_the_sent_status()
    {
        await AssertAbortAsync("/test/observability/abort-after-start", 200, RequestLog.OutcomeCancelled, "/test/observability/abort-after-start");
    }

    [Fact]
    public async Task Escaped_exception_before_the_response_is_incomplete()
    {
        using HttpClient client = _factory.CreateApiClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/test/observability/boom");
        request.Headers.TryAddWithoutValidation(SelectiveRethrowExceptionHandler.HeaderName, "1");
        _collector.Clear();
        try
        {
            await client.SendAsync(request);
        }
        catch (HttpRequestException)
        {
        }

        RequestLogEntry entry = AssertSingle(0, RequestLog.OutcomeIncomplete, "/test/observability/boom");
        AssertNoMarker(entry);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Escaped_exception_after_the_response_outranks_request_abort()
    {
        using HttpClient client = _factory.CreateApiClient();
        using CancellationTokenSource cancellation = new();
        _collector.Clear();
        ObservabilityProbeGate gate = _factory.Services.GetRequiredService<ObservabilityProbeGate>();
        gate.Reset();
        Task<HttpResponseMessage> pending = client.GetAsync("/test/observability/incomplete-over-abort", cancellation.Token);
        await gate.Entered.Task.WaitAsync(ObservabilityProbeGate.WaitTimeout);
        cancellation.Cancel();
        gate.Release.TrySetResult();
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        IReadOnlyList<RequestLogEntry> entries = await _collector.WaitForCountAsync(1);
        RequestLogEntry entry = Assert.Single(entries);
        AssertCompletedShape(entry, 200, RequestLog.OutcomeIncomplete, "/test/observability/incomplete-over-abort");
        AssertNoMarker(entry);
    }

    [Fact]
    public async Task Internal_cancellation_is_a_handled_500()
    {
        using HttpClient client = _factory.CreateApiClient();
        await AssertCompleted(client, "/test/observability/internal-cancel", HttpStatusCode.InternalServerError, 500, "/test/observability/internal-cancel", problem: true);
    }

    [Fact]
    public async Task Probe_timeout_is_a_handled_503()
    {
        _probe.Next = cancellation => Task.Delay(Timeout.Infinite, cancellation);
        using HttpClient client = _factory.CreateApiClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/ready");
        RequestLogEntry entry = AssertSingle(503, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not-ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        AssertHeader(response, entry);
    }

    [Fact]
    public async Task Probe_failure_and_recovery_have_no_application_latch()
    {
        int failures = 1;
        _probe.Next = _ =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
            {
                throw new TimeoutException(Marker);
            }

            return Task.CompletedTask;
        };
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage failed = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain(Marker, await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoMarker(AssertSingle(503, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute));

        _collector.Clear();
        HttpResponseMessage recovered = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        AssertSingle(200, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute);
    }

    [Fact]
    public async Task Ready_matching_is_exact_and_https_redirect_does_not_probe()
    {
        _probe.Calls = 0;
        using HttpClient client = _factory.CreateApiClient();
        HttpResponseMessage wrongCase = await client.GetAsync("/Ready");
        HttpResponseMessage slash = await client.GetAsync("/ready/");
        using HttpRequestMessage post = new(HttpMethod.Post, "/ready");
        HttpResponseMessage posted = await client.SendAsync(post);
        Assert.NotEqual(HttpStatusCode.OK, wrongCase.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, slash.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(0, _probe.Calls);

        using FinOpsDeskApiFactory httpsFactory = new(
            UnusedConnection,
            services =>
            {
                services.RemoveAll<IDatabaseReadinessProbe>();
                services.AddSingleton<IDatabaseReadinessProbe>(_probe);
                services.AddSingleton<ILoggerProvider>(_collector);
                services.PostConfigure<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>(
                    options => options.HttpsPort = 443);
            });
        using HttpClient http = httpsFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });
        int calls = _probe.Calls;
        _collector.Clear();
        HttpResponseMessage redirect = await http.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal(calls, _probe.Calls);
        RequestLogEntry entry = AssertSingle(307, RequestLog.OutcomeCompleted, RequestLog.UnmatchedRoute);
        AssertHeader(redirect, entry);
    }

    [Fact]
    public async Task Secret_markers_do_not_enter_the_request_event()
    {
        using HttpClient client = _factory.CreateApiClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/carrier-" + Marker + "?q=" + Marker)
        {
            Content = new StringContent(Marker),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Marker);
        request.Headers.TryAddWithoutValidation("Cookie", "session=" + Marker);
        request.Headers.TryAddWithoutValidation("User-Agent", Marker);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.5");
        _collector.Clear();
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoMarker(AssertSingle(401, RequestLog.OutcomeCompleted, RequestLog.UnmatchedRoute));
    }

    [Fact]
    public async Task Probe_success_after_the_budget_is_not_ready()
    {
        _probe.Next = async cancellation =>
        {
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellation.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                completed);
            await completed.Task;
        };
        using HttpClient client = _factory.CreateApiClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/ready");
        string payload = await response.Content.ReadAsStringAsync();
        RequestLogEntry entry = AssertSingle(503, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(payload);
        Assert.Equal("not-ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        AssertHeader(response, entry);
    }

    [Fact]
    public async Task Client_abort_during_the_probe_is_cancellation()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.Next = async cancellation =>
        {
            entered.TrySetResult();
            await Task.Delay(ObservabilityProbeGate.WaitTimeout, cancellation);
        };
        using HttpClient client = _factory.CreateApiClient();
        using CancellationTokenSource cancellation = new();
        _collector.Clear();
        Task<HttpResponseMessage> pending = client.GetAsync("/ready", cancellation.Token);
        await entered.Task.WaitAsync(ObservabilityProbeGate.WaitTimeout);
        cancellation.Cancel();
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        AssertSingle(499, RequestLog.OutcomeCancelled, RequestLog.ReadyRoute);
    }

    [Fact]
    public async Task Unexpected_probe_exception_uses_host_error_handling()
    {
        _probe.Next = _ => throw new InvalidOperationException(Marker);
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/ready");
        string payload = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("not-ready", payload, StringComparison.Ordinal);
        RequestLogEntry entry = AssertSingle(500, RequestLog.OutcomeCompleted, RequestLog.ReadyRoute);
        AssertNoMarker(entry);
        AssertHeader(response, entry);
    }

    [Fact]
    public async Task Development_jwt_leaves_signin_oidc_unmatched()
    {
        using HttpClient client = _factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/signin-oidc?code=" + Marker + "&state=" + Marker);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        AssertNoMarker(AssertSingle(401, RequestLog.OutcomeCompleted, RequestLog.UnmatchedRoute));
    }

    [Fact]
    public async Task Ordinary_host_has_no_observability_test_routes()
    {
        await AssertOrdinaryHostAsync(null);
        await AssertOrdinaryHostAsync(new Dictionary<string, string?>
        {
            ["Authentication:ObservabilityProbes"] = "Enabled",
        });
    }

    [Fact]
    public async Task Trailing_newline_keeps_problem_details_and_logs_null()
    {
        Assert.Null(RequestLog.AcceptProblemTraceId(
            NewlineTraceIdProblemDetailsWriter.TraceId,
            "server-request"));
        using FinOpsDeskApiFactory factory = CreateFactory(
            services =>
            {
                services.RemoveAll<IProblemDetailsWriter>();
                services.AddSingleton<IProblemDetailsWriter, NewlineTraceIdProblemDetailsWriter>();
            },
            scenarios: true);
        using HttpClient client = factory.CreateApiClient();
        _collector.Clear();
        HttpResponseMessage response = await client.GetAsync("/test/observability/bad-request");
        string payload = await response.Content.ReadAsStringAsync();
        RequestLogEntry entry = AssertSingle(400, RequestLog.OutcomeCompleted, "/test/observability/bad-request");
        using JsonDocument body = JsonDocument.Parse(payload);
        Assert.Equal(
            NewlineTraceIdProblemDetailsWriter.TraceId,
            body.RootElement.GetProperty("traceId").GetString());
        Assert.Null(entry.State["problemTraceId"]);
        Assert.NotEqual(entry.State["requestId"], NewlineTraceIdProblemDetailsWriter.TraceId);
        AssertHeader(response, entry);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task AssertOrdinaryHostAsync(IReadOnlyDictionary<string, string?>? overrides)
    {
        using FinOpsDeskApiFactory factory = CreateFactory(scenarios: false, overrides: overrides);
        using HttpClient client = factory.CreateApiClient();
        foreach (string path in new[] { "/test/observability/boom", "/test/observability/missing" })
        {
            _collector.Clear();
            HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
            AssertNoMarker(AssertSingle(401, RequestLog.OutcomeCompleted, RequestLog.UnmatchedRoute));
        }
    }

    private FinOpsDeskApiFactory CreateFactory(
        Action<IServiceCollection>? configure = null,
        bool scenarios = false,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        return new FinOpsDeskApiFactory(
            UnusedConnection,
            services =>
            {
                services.RemoveAll<IDatabaseReadinessProbe>();
                services.AddSingleton<IDatabaseReadinessProbe>(_probe);
                services.AddSingleton<ILoggerProvider>(_collector);
                if (scenarios)
                {
                    services.AddSingleton<IStartupFilter, ObservabilityScenarioStartupFilter>();
                }

                configure?.Invoke(services);
            },
            configurationOverrides: overrides);
    }

    private async Task AssertCompleted(
        HttpClient client,
        string path,
        HttpStatusCode expected,
        int statusCode,
        string route,
        bool problem = false,
        bool bearer = true)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (!bearer)
        {
            request.Headers.Authorization = null;
        }

        _collector.Clear();
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        RequestLogEntry entry = AssertSingle(statusCode, RequestLog.OutcomeCompleted, route);
        AssertHeader(response, entry);
        if (!problem && path != "/test/observability/financial-unavailable")
        {
            return;
        }

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (problem)
        {
            Assert.Equal(entry.State["problemTraceId"], body.RootElement.GetProperty("traceId").GetString());
        }

        if (path == "/test/observability/financial-unavailable")
        {
            Assert.Equal("Financial authorization could not be completed.", body.RootElement.GetProperty("title").GetString());
            Assert.Equal("auth.authorization.unavailable", body.RootElement.GetProperty("code").GetString());
        }
    }

    private async Task AssertAbortAsync(string path, int statusCode, string outcome, string route, bool release = false)
    {
        using HttpClient client = _factory.CreateApiClient();
        using CancellationTokenSource cancellation = new();
        ObservabilityProbeGate gate = _factory.Services.GetRequiredService<ObservabilityProbeGate>();
        gate.Reset();
        _collector.Clear();
        Task<HttpResponseMessage> pending = client.GetAsync(path, cancellation.Token);
        await gate.Entered.Task.WaitAsync(ObservabilityProbeGate.WaitTimeout);
        cancellation.Cancel();
        if (release)
        {
            gate.Release.TrySetResult();
        }

        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        IReadOnlyList<RequestLogEntry> entries = await _collector.WaitForCountAsync(1);
        AssertCompletedShape(Assert.Single(entries), statusCode, outcome, route);
    }

    private RequestLogEntry AssertSingle(int statusCode, string outcome, string route)
    {
        RequestLogEntry entry = Assert.Single(_collector.Entries);
        AssertCompletedShape(entry, statusCode, outcome, route);
        return entry;
    }

    private static void AssertCompletedShape(RequestLogEntry entry, int statusCode, string outcome, string route)
    {
        Assert.Equal(9101, entry.EventId);
        Assert.Equal("RequestCompleted", entry.EventName);
        Assert.Equal(RequestLog.Category, entry.Category);
        Assert.Equal(RequestLog.Message, entry.Message);
        Assert.Null(entry.Exception);
        Assert.Equal(
            ["elapsedMilliseconds", "httpMethod", "outcome", "problemTraceId", "requestId", "route", "statusCode"],
            entry.State.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal(statusCode, Assert.IsType<int>(entry.State["statusCode"]));
        Assert.Equal(outcome, entry.State["outcome"]);
        Assert.Equal(route, entry.State["route"]);
        Assert.IsType<string>(entry.State["requestId"]);
        Assert.IsType<string>(entry.State["httpMethod"]);
        Assert.IsType<long>(entry.State["elapsedMilliseconds"]);
    }

    private static void AssertHeader(HttpResponseMessage response, RequestLogEntry entry)
    {
        Assert.Equal(entry.State["requestId"], response.Headers.GetValues(RequestLog.HeaderName).Single());
    }

    private static void AssertNoMarker(RequestLogEntry entry)
    {
        Assert.DoesNotContain(Marker, entry.Message, StringComparison.Ordinal);
        foreach (object? value in entry.State.Values)
        {
            if (value is string text)
            {
                Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
            }
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/test/observability/conflict");
        request.Headers.TryAddWithoutValidation(
            "traceparent",
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        return client.SendAsync(request);
    }
}

internal sealed class ScriptedReadinessProbe : IDatabaseReadinessProbe
{
    public int Calls { get; set; }

    public Func<CancellationToken, Task> Next { get; set; } = _ => Task.CompletedTask;

    public Task ProbeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Next(cancellationToken);
    }
}

internal sealed class SelectiveRethrowExceptionHandler : IExceptionHandler
{
    public const string HeaderName = "X-Test-Escape";

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.Headers.ContainsKey(HeaderName))
        {
            throw new InvalidOperationException("The test asked the exception handler to fail.");
        }

        return ValueTask.FromResult(false);
    }
}