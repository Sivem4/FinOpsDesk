# ADR 0044: Rate limits and security review

- Status: Accepted
- Date: 2026-11-09

## Context

Phase 9A records request completion and readiness. Phase 9B pages the allocation-dispute queue. The remaining operability work is a small set of process-local budgets for the expensive matching reads, batch execution, and CSV imports, plus a review of the authorization boundaries those routes already have.

The budgets are an operational policy. They are not a measured capacity. Confirm, allocation, reversal, health, readiness, login, logout, session, the OpenID Connect callback, and Development OpenAPI stay outside them.

## Decision

The host registers three named policies with the built-in ASP.NET Core rate limiter. The Reconciliation module does not name the policies. A convention on the `/api` group attaches `EnableRateLimitingAttribute` only when the method and route pattern are one of the six rows below.

| Policy | Routes | Default budget for one confirmed organization |
| --- | --- | --- |
| `matching-reads` | `GET /api/matches`, `GET /api/matches/smart`, `GET /api/matches/batch/preview` | Shared fixed window, 60 permits per 60 seconds |
| `matching-execute` | `POST /api/matches/batch/execute` | Separate fixed window, 10 permits per 60 seconds |
| `imports` | `POST /api/imports/invoices`, `POST /api/imports/bank-transactions` | Shared token bucket, capacity 6, one token per minute |

Those numbers are the defaults in `RateLimitBudgetOptions`. Before it listens, the host creates one instance from the `RateLimiting` section and validates that instance once. A configuration reload does not create or validate another instance. `ConfiguredOrganizationRateLimitPartitioner` copies that snapshot into its fields and uses it for the life of the process. Changing the section requires a restart. `QueueLimit` is 0 and `AutoReplenishment` is true. Neither is a configuration value. The partition key is the policy name plus the confirmed organization id. `DevelopmentJwt` reads that id with `OrganizationClaimParser` on the authenticated principal. `ProductionCookie` reads the active organization already stored on `AccessSessionGate.Current`. The limiter does not call `ResolveAsync` or `ResolvePermissionsAsync`. A client header, query value, or body does not choose the partition. `ICurrentOrganizationContext.OrganizationId` is not used, and a missing organization does not fall back to a shared key.

The production partitioner does not count selections. A test that needs that count, a scripted delay, or a permit gate replaces `IOrganizationRateLimitPartitioner` in the test host. It does not reflect over `RateLimiterOptions`.

The pipeline order is completion log, exception handler, HTTPS redirection, routing, route capture, `/ready`, authentication, the financial anonymous-route guard, authorization, cookie CSRF, the confirmed-organization check for a limited route, `UseRateLimiter`, then the endpoint. If authorization succeeded and the limited route still has no confirmed organization, that check returns 403 with `Cache-Control: no-store` and does not select a partition.

A limiter rejection is 429 `application/problem+json` before the endpoint delegate and before a form is read. `Accept: text/plain` does not change the content type. The title is `Too many requests.` and the code is `finopsdesk.rate_limit.exceeded`. `Cache-Control` is `no-store`. `X-Request-Id` is still written only by the existing `OnStarting` callback. A positive lease `RetryAfter` is written as whole seconds rounded up. A missing, zero, or negative delay omits `Retry-After`. The response and the new completion state do not contain the partition key, the organization id, a file name, or the request body.

These counters live in the process. A restart clears them. Two instances do not share a budget. A fixed window can admit two full bursts when requests fall on either side of the window boundary. The runtime may remove an idle partition; this decision does not promise when.

Import idempotency remains the file hash, import type, and source. Batch execution idempotency remains the same `Idempotency-Key` and body. A rejected attempt does not create a second batch or allocation. The browser does not retry automatically. Exact, smart, and read-only reconciliation keep a manual refresh. The import screen keeps the captured file, type, trimmed source, and epoch for a manual retry.

## Security review

| Boundary | Code | Control | Result | Evidence |
| --- | --- | --- | --- | --- |
| JWT organization | `OrganizationClaimParser`, `TenantMemberAuthorizationHandler` | Authenticated non-empty `org_id` | The partition is that claim. A client-supplied organization does not move it. | `Named_policies_are_shared_isolated_and_ignore_client_organization`. This does not prove a future claim type or a second issuer. |
| Cookie organization | `AccessSessionGate.Current` | Active snapshot from the cookie validation already performed for the request | The partition follows `POST /api/session/organization`. | `Cookie_partition_follows_the_active_organization`. The limiter does not resolve membership again. |
| Read and write flags | `FinancialReadAuthorization`, `FinancialWriteAuthorization` | Cookie reads can be enabled alone. A cookie write also needs the write flag. | Both flags still do not activate import or batch execute. | `Authorization_and_csrf_win_and_do_not_select_a_partition`. DevelopmentJwt does not consult either flag; that branch is `FinancialApiAccessHandler`. |
| Roles and allowlist | `FinancialApiAccessHandler`, `ProductionFinancialWriteActivation` | Cookie write needs the write flag, the three-operation allowlist, and the exact permission. | Operator with both flags receives 403 for both imports and batch execute, and no batch or allocation is inserted. | The same authorization test asserts 403 and zero `ImportBatches`, `Allocations`, and `BatchExecutions`. Cookie unlisted probe: `FinancialWriteAuthorizationTests.Unlisted_write_probe_stays_denied_when_writes_are_enabled`. |
| CSRF | `ProductionCsrfMiddleware` | Multipart without `X-CSRF-Token` fails before the form is read. | `400 auth.csrf.invalid` occurs before the limiter and does not take a permit. | `Authorization_and_csrf_win_and_do_not_select_a_partition`. The selector count is the test host's `SelectorCounter`. Authorization 401 and 403 still occur before CSRF. |
| Financial commit guard | `FinancialCommitGuard` | Rechecks an allowlisted write inside the commit transaction. | Import and batch execute stay outside that guard. | Inspection of `FinancialCommitGuard` and `ProductionFinancialWriteActivation`. This phase adds no new guard test. |
| BOLA and cursors | Organization predicate on tenant queries; dispute cursor is a position | A foreign id compares as not found. The cursor does not look up an organization. | Unchanged by the limiter. | Inspection of the tenant query filters and `AllocationDisputeQueueCursorEndpointTests`. This phase adds no new cursor or cross-tenant test. |
| Idempotency | Import file hash; batch `Idempotency-Key` | Replay returns the stored batch with `replayed` or the stored allocation ids. | A 429 adds no effect. A lost response can still replay the first commit. | `Lost_import_response_retries_the_same_batch_after_the_permit_returns` and `Lost_execute_response_retries_the_same_allocations_after_the_permit_returns`. The hold that drops the body is test infrastructure. |
| Classification probes | `FinancialClassificationProbeEndpoints` | The seven probe routes are mapped only when `Authentication:FinancialClassificationProbes` is exactly `Enabled`. | `POST /api/test/financial-write-unlisted` is 403 on a cookie host. With the probes enabled and a valid DevelopmentJwt, that route can return `ran` because JWT checks a consistent classification and does not apply the cookie allowlist. | `Authorization_and_csrf_win_and_do_not_select_a_partition` asserts the seven probes are not 429. Cookie 403 is `Unlisted_write_probe_stays_denied_when_writes_are_enabled`. The JWT `ran` body is the existing probe handler after `FinancialApiAccessHandler`; this phase does not add an assertion of that body. |
| Readiness | `ReadinessMiddleware`, `DatabaseReadinessProbe` | Anonymous `GET /ready` runs `SELECT 1` before authentication. | `/ready` and `/health` stay outside the limiter. | `Named_policies_are_shared_isolated_and_ignore_client_organization` and `Cookie_partition_follows_the_active_organization`. `SELECT 1` is `DatabaseReadinessProbe`. A ready result is not proof of schema or financial correctness. |
| Event 9101 | `RequestCompletionLogMiddleware` | One completion record: request id, safe problem trace id, method, route template, status, elapsed milliseconds, and outcome. | `X-Request-Id` matches the request id. The 429 trace id is that same id. | `Rejection_is_problem_json_before_the_handler_and_form_read`. The event does not record the partition, organization, file name, or body, and it does not prove the client received the body. |

No authorization defect inside this scope required a behavior change. Anonymous `/ready` remains deliberately unlimited and still opens SQL Server with `SELECT 1`.

## Consequences

Clients that send the six routes can receive 429 and an optional `Retry-After`. The React query client keeps `retry: false`. The configured budget is read once at startup. Changing `RateLimiting` requires a process restart. A fixed-window boundary can double the short-term rate. More than one API process multiplies the budget. A shared limiter store remains a Phase 10 decision.
