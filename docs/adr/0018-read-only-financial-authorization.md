# ADR 0018: Read-only financial authorization

- Status: Accepted
- Date: 2026-11-02

## Context

ADR 0017 stores organization roles and a fixed permission map, but it does not authorize financial HTTP. `ProductionCookie` still fails closed. `DevelopmentJwt` still authorizes the whole Reconciliation group from a validated `org_id` claim and does not read Access.

Opening those reads in the same change as an incompatible browser session contract would break already-open tabs. The authorization mechanism and the production activation are therefore separate, and the session JSON shape is negotiated.

## Decision

Phase 7D.3 is delivered in two increments.

7D.3a adds endpoint classification and evaluates it on the existing `TenantMember` policy. Deployed `ProductionCookie` stays fail-closed. Financial writes stay forbidden for the cookie.

7D.3b publishes the session capability contract, mounts the read-only React experience, and documents the operational activation of `Authentication:FinancialReadAuthorization`. It does not replace this authorization model. Writes remain forbidden there too.

### Classification

Every route mapped by `ReconciliationEndpoints` carries `ReconciliationRouteBoundary` and exactly one `FinancialEndpointClassification`.

Reads:

- `invoices.read`: `GET /api/invoices`
- `bank-transactions.read`: `GET /api/bank-transactions`
- `reconciliation.read`: `GET /api/matches`, `GET /api/matches/smart`, `GET /api/matches/batch/preview`
- `allocations.read`: `GET /api/allocations`, `GET /api/allocations/context`, `GET /api/allocations/{allocationId}/reversal-context`
- `audit.read`: `GET /api/allocations/{allocationId}/audit`
- `review-cases.read`: `GET /api/review-cases`, `GET /api/review-cases/{id}`

Every current financial POST is classified as a write. A write classification has no permission that a cookie can satisfy. `DevelopmentJwt` still accepts a consistent write classification together with `org_id`.

Missing metadata, more than one classification, an unknown permission, a write permission used as a read, or an HTTP method that does not match the classification does not succeed. A new financial route therefore stays denied until it is classified. The marker is read by `FinancialApiAccessHandler`. It is not documentation beside a policy that ignores it.

`AllowAnonymous` on a classified route, or on any route inside the Reconciliation boundary, is rejected by `FinancialAnonymousRouteGuardMiddleware` before the authorization middleware can skip the policy. Health remains anonymous because it has neither marker.

An integration test reads `EndpointDataSource`. It fails when a Reconciliation handler is mapped outside the boundary, when the classified set differs from the inventory above, when `TenantMember` is missing or joined by another policy, role, or scheme, or when `IAllowAnonymous` is present. Scanning only endpoints that already have a financial marker would not see an unmarked Reconciliation handler. The test also requires the handler `MethodInfo` to come from the Reconciliation assembly. A route mapped in a different assembly, with no boundary and no Reconciliation handler, is not identifiable as financial from metadata. The module mapping method is the boundary that makes the check possible.

### Policy

The `/api` group keeps one policy, `TenantMember`: authenticated user, `FinancialApiAccessRequirement`, and `TenantMemberRequirement`. Endpoint metadata does not replace that policy.

`DevelopmentJwt` ignores Access, `BrowserSession`, and `Authentication:FinancialReadAuthorization`. A consistent classification plus the validated `org_id` claim preserves the existing reads, writes, and idempotency. The cookie CSRF middleware is not registered.

`ProductionCookie` with the configuration absent, empty, or any value other than the ordinal string `Enabled` keeps `ProductionFinancialAccessFailClosed` true. Every financial GET and POST is then forbidden. The handler does not read roles and does not run the financial delegate. This is the deployed default. `appsettings.json` does not set the key.

`Enabled` is the operational activation switch after compatible API and frontend code are deployed. It does not grant blanket access. A cookie read succeeds only when all of the following hold:

- the application cookie authenticated;
- `OnValidatePrincipal` accepted the server `BrowserSession` and the application user;
- the trusted snapshot is active and has an active organization membership;
- the route is one consistent read classification;
- `ResolvePermissionsAsync` returns that exact permission for the active organization.

No active organization, a membership with no role, `OrganizationAdmin` alone, a revoked role, or a revoked membership is HTTP 403. `org_id` is not added to the cookie. `X-Organization-Id` is not a tenant. Role and permission claims are not written on the cookie and are not consulted.

A classified write, including a request with a valid CSRF token and an idempotency key, is HTTP 403 for every cookie role. The financial delegate does not run. `DevelopmentJwt` writes are unchanged.

### Permission resolution and HTTP status

`ResolvePermissionsAsync` publishes one shared `Task` for the current gate. A lock covers only that publication, so the role query starts once and is not held inside the lock. `LazyInitializer.EnsureInitialized` is not used: its factory may run more than once before one reference is stored. Callers wait with `WaitAsync` on their own cancellation token. That waiter token is not passed to the query. An HTTP request supplies `HttpContext.RequestAborted` for the shared read; without an HTTP context the query uses `CancellationToken.None`. Cancelling one waiter does not cancel the query while another caller of the same gate still needs it. Aborting the request does. The task is not stored for another request. There is no distributed cache.

HTTP 401 remains the authentication result: missing cookie, rejected identity, or a revoked or expired `BrowserSession`. Those paths sign the caller out as they do today.

HTTP 403 is an authenticated refusal: no selected organization, no active membership, no required permission, fail-closed configuration, a write, or invalid classification.

HTTP 404 stays the existing handler result when an authorized caller asks for an identifier outside the active organization. Lists stay empty. The handler filters on `ICurrentOrganizationContext` inside SQL.

HTTP 503 `auth.authorization.unavailable` is only the explicit marker set when the snapshot or the role read is unavailable, including a role value outside 1–4. `FinOpsDeskAuthorizationResultHandler` converts that marker and delegates every other result to the framework handler, so a normal 401 or 403 is not turned into 503. The response does not include the provider exception. The cookie is not cleared. A successful read with zero roles is 403, not 503.

The financial delegate does not run for 401, 403, or 503.

### Session contract negotiation

`GET /api/session` keeps `Cache-Control: no-store` and sets `Vary: X-FinOpsDesk-Session-Contract`.

Contract v1 is the default whenever the request header is missing, empty, or not exactly `2`. The JSON shape stays the pre-7D.3b body: `financialAccess` is always `false`, and there is no `permissions` field. Contract v1 does not read the role table for capability discovery. This remains true after financial reads are activated, so an old browser bundle never receives `financialAccess: true`.

Contract v2 is selected only by the exact header value `X-FinOpsDesk-Session-Contract: 2`. Every successful v2 body includes `permissions: string[]`. The array contains only the currently enabled financial read capabilities, in catalog order, without duplicates or write permissions:

- `invoices.read`
- `bank-transactions.read`
- `reconciliation.read`
- `allocations.read`
- `audit.read`
- `review-cases.read`

`financialAccess` is `true` only when that filtered list is nonempty for the trusted active organization. OrganizationAdmin alone, a roleless membership, and a missing active organization return `financialAccess: false` with `permissions: []`.

When the activation key is unset, contract v2 still returns `financialAccess: false` and `permissions: []` without a role-table lookup, and financial routes stay fail-closed.

When the activation key is `Enabled`, an active organization is present, and contract v2 is requested, a role-table failure or an inconsistent role returns HTTP 503 `auth.authorization.unavailable`. Contract v1 continues to return HTTP 200 with the basic snapshot in that same outage.

The React client always requests contract v2. It accepts a v1 body as a legacy restricted session. Unknown permission strings never enable an action. Consistency between `financialAccess` and recognized capabilities is checked after unknown entries are filtered. Canonical capability comparison participates in the existing security epoch together with identity and active organization. Authorization readiness is separate from the epoch: an unavailable transition clears financial queries and blocks protected observers without inventing a second store.

Cookie financial screens mount only the registers and read-only matching and allocation history for verified capabilities. Imports, confirmation, allocation, reversal, and recovery hooks are not mounted. The HTTP boundary rejects cookie financial mutations before `fetch`. Backend write denial remains authoritative. Phase 7D.4b2/c later mounted the three allowlisted writes and identity-bound recovery. See ADR 0020. That delivery does not set the deployed flags.

Deployed configuration still leaves `Authentication:FinancialReadAuthorization` unset. The compatible read artifacts have since shipped. Activation remains an operational step because the deployed key is unset. Removing the key disables reads again without invalidating browser sessions.

### Known limits

Endpoint enumeration cannot name a future financial route that is mapped outside the Reconciliation assembly and carries neither the boundary nor a classification. The architecture test locks the module reference direction. A review of a new module mapping is still required.

OpenAPI Bearer security metadata for the existing routes remains incomplete. Runtime authorization does not wait for that document. That was the state of this decision. Phase 7G later described the active host composition. See ADR 0024. The deployed read flag remains unset.

Batch preview remains a full-book read. It runs only after authorization. This increment does not add a batch-preview UI, a review-case screen, an index, or a migration.

`Authentication:FinancialClassificationProbes` is a test mapping switch. It is unset in deployed configuration. When its value is exactly `Enabled`, the host adds routes under `/api/test/financial-*` so integration tests can prove that missing, unknown, and inconsistent classifications are denied. Those routes are not a product feature and must not be enabled in deployment.

After activation, an authorized direct financial GET can succeed for a caller who still runs an old JavaScript bundle. That is server-side authorization, not a client bypass. The old bundle continues to receive contract v1 and does not mount finance.

## Consequences

- Cookie financial writes stay unavailable to Operator and ReversalApprover.
- Identity-bound recovery and public role administration stay out of this increment.
- Phase 7 remains Planned.
- 7D.3b activates the prepared read path without a second authorization design. It still does not authorize writes.
