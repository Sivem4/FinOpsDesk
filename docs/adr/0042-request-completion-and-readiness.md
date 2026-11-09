# ADR 0042: Request completion and readiness

- Status: Accepted
- Date: 2026-11-09

## Context

Operators need a server-generated identifier for one HTTP attempt and a short record of how that attempt finished. The host already writes Problem Details `traceId` from `Activity.Current?.Id ?? HttpContext.TraceIdentifier` and exposes anonymous `GET /health`. That health response does not open SQL Server. Access and Reconciliation share one configured database and two EF Core contexts.

An operational log is not a financial audit and is not proof that the client received the response. A readiness result is not proof that migrations, schema, or financial operations are correct.

## Decision

`RequestCompletionLogMiddleware` is the outermost middleware. Before it calls the rest of the pipeline, it registers `Response.OnStarting` and stores `HttpContext.TraceIdentifier`. The callback sets `X-Request-Id` to that stored value, replacing any value already present. Client headers do not choose it. The callback remains after `Response.Clear()`, so a handled 503 or 500 still receives the header when the response starts.

The middleware emits one Information event per request through `RequestLog.Write`. The category is `FinOpsDesk.Api.Requests`, the event is `EventId(9101, "RequestCompleted")`, and the message is `HTTP request completed.`. The state object implements `IReadOnlyList<KeyValuePair<string, object?>>` and its `ToString()` returns the same message. The logger call passes `exception: null`.

The state has exactly these fields:

| Field | Meaning |
| --- | --- |
| `requestId` | Server `TraceIdentifier` captured on entry |
| `problemTraceId` | Safe copy of the Problem Details `traceId`, or null |
| `httpMethod` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS`, `TRACE`, or `OTHER` |
| `route` | `RouteEndpoint.RoutePattern.RawText`, or `ready`, `oidc-callback`, or `unmatched` |
| `statusCode` | Integer from the completion table |
| `elapsedMilliseconds` | `Stopwatch` time from entry to `finally` |
| `outcome` | `completed`, `cancelled`, or `incomplete` |

`CustomizeProblemDetails` reads the extension key produced by the same JSON naming policy the Problem Details writer uses. It stores only a value that is ordinally equal to the server request id or matches the entire lowercase W3C form `2-32-16-2`, anchored at both ends. A valid identifier with an extra trailing newline is rejected. The callback does not change the Problem Details object, so the response keeps the writer value. `finally` does not read `Activity.Current` again. Responses that never reach the writer, and values outside those two forms, store JSON null. `X-Request-Id` remains the correlation key.

Completion priority is:

| Condition | `outcome` | `statusCode` |
| --- | --- | --- |
| Any exception left the pipeline | `incomplete` | Sent status, or `0` when the response has not started |
| The pipeline returned, `RequestAborted` is set, and the response started | `cancelled` | Sent status |
| The pipeline returned, `RequestAborted` is set, and the response has not started | `cancelled` | `499` only when the platform assigned it; otherwise `0` |
| Any other return | `completed` | Final HTTP status, including handled errors |

An exception that leaves the pipeline is logged and then propagated. `incomplete` wins over `RequestAborted`. A normal `OperationCanceledException` without client abort stays on the existing error path and can finish as handled 500. A readiness timeout while the client is still connected finishes as handled 503.

The pipeline order is completion logging, exception handler, HTTPS redirection, routing, route-template capture, readiness, authentication, the anonymous financial guard, authorization, cookie CSRF when enabled, then endpoints. The route template is copied after routing and before the exception handler clears the endpoint. The label `oidc-callback` is used only when the active composition registers OpenID Connect and the effective `OpenIdConnectOptions.CallbackPath` equals `Request.Path` through `PathString` equality. `DevelopmentJwt` does not register that handler, so `/signin-oidc` stays `unmatched`. Every other unrecognized request uses `unmatched`.

`GET /ready` ends the pipeline before authentication when the method is GET and the path equals `/ready` by `StringComparison.Ordinal`. Query text does not widen the match. `/Ready`, `/ready/`, and other methods continue through the normal pipeline. An HTTP request that HTTPS redirection answers does not run the probe. `ProductionCookie` refuses to start when the effective callback path equals `/ready` under the OIDC path comparison, including `/Ready`. `GET /health` stays anonymous and does not open SQL Server. Vite and the Chromium journey keep using `/health`.

The production probe implements `IDatabaseReadinessProbe` with one `OpenAsync` and one `ExecuteScalarAsync` of `SELECT 1`. A two-second cancellation source linked to `RequestAborted` covers both operations. The probe connection string sets `Connect Timeout=2` and `Connect Retry Count=0`, and `CommandTimeout` is 2. The cancellation budget is the moment cancellation is signaled to the driver. `Application Name` is `FinOpsDesk.Readiness`, or `FinOpsDesk.Readiness.Probe` when the configured name is already the first value, so the SqlClient pool key differs from the module connection string even when the timeouts already match. Module connection strings are unchanged. `PoolBlockingPeriod` stays at the provider default. The application does not remember a failed probe and does not clear the pool. Readiness returns on the first later probe that opens and runs `SELECT 1`, which can wait out the provider's blocking period after a connection error.

After the SQL call returns, the middleware checks client cancellation before budget expiry. A successful result that arrives after the budget expired is HTTP 503 while the client is still connected. `SqlException`, `TimeoutException`, and cancellation raised by that budget map to HTTP 503 `{"status":"not-ready"}`. An unexpected probe exception, and any failure while writing the response, propagate to the existing exception handler. Success is HTTP 200 `{"status":"ready"}`. Both JSON responses use `Cache-Control: no-store`. Client abort does not write the not-ready body. Event 9101 with status 503 is the failed-probe record. The body and the event do not include credentials, the connection string, or exception text.

Console logging uses the built-in JSON formatter with `IncludeScopes` false, UTC timestamps, and no indentation, in base and Development configuration. The safety guarantee applies to event 9101 and that serialization. Framework, EF Core, and exception diagnostics stay in place.

The host does not map test-only observability routes. A configuration key cannot turn those routes on. Tests that need a failing endpoint register it only on the test host, before routing, so the request still passes through correlation, exception handling, and event 9101.

## Consequences

Operators correlate a received response through `X-Request-Id`. They use `problemTraceId` only when the Problem Details writer selected a safe value. Status `0` means a cancellation or an escaped exception before a response started, not HTTP 200. Status `499` is reported only when the platform assigned it. Elapsed time is server handling time through `finally`, not proof of client delivery.

`GET /ready` can succeed while the module pools are blocked or a financial write would fail. After a SQL connection error, SqlClient can reject the next open from the readiness pool until its blocking period ends. The next successful probe is the recovery signal.

Phase 9 remains Planned. Query plans, indexes, keyset pagination, rate limits, and the broader security review are later increments.
