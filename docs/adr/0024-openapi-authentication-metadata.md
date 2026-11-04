# ADR 0024: OpenAPI authentication metadata

- Status: Accepted
- Date: 2026-11-04

## Context

`/openapi/v1.json` is generated only when the host environment is Development. The process registers one authentication composition. `DevelopmentJwt` and `ProductionCookie` are not alternatives inside one process, and a Development environment may run either composition.

The document previously described business schemas without saying which of those compositions was active. It also described some organization sources as an access token or a raw claim, and it described authorization as unfinished Phase 7 work after that authorization already existed.

## Decision

The generated document describes the composition registered by the running host. It publishes one security scheme and does not publish bearer and cookie together.

`DevelopmentJwt` is HTTP bearer with JWT format. `ProductionCookie` is an API key in the `__Host-finopsdesk-session` cookie. Anonymous browser routes stay without that requirement. The document does not set a document-level security requirement.

Small document and operation transformers live in the API host. They read `AuthenticationCompositionState`, `FinancialEndpointClassification`, `IAllowAnonymous`, explicit `IAuthorizeData`, the host `FallbackPolicy`, and `ProductionCsrfMetadata`. An endpoint with `IAllowAnonymous` stays anonymous. Explicit authorization metadata requires the active scheme. An endpoint with neither metadata uses the fallback policy, which requires an authenticated caller and does not add `TenantMember`. Financial capability text is added only when the route carries `FinancialEndpointClassification`. Reconciliation keeps its business schema transformers and does not reference the host or the cookie types.

`POST /logout` has neither `AllowAnonymous` nor its own authorization policy, so the ProductionCookie document requires the session cookie. It does not describe a financial capability. Authorization can reject that request before CSRF middleware. `POST /login` and `GET /api/session` remain anonymous.

A security scheme is not a financial capability. The description separates authentication, the active organization, and the classified permission. Cookie reads also name the closed `Authentication:FinancialReadAuthorization` switch. Cookie writes name `Authentication:FinancialWriteAuthorization` and the three-operation allowlist. A write outside that allowlist stays in the document and is described as HTTP 403 before CSRF. The description states the deployed switches are unset. It does not change when a test host enables them.

CSRF parameters appear only in the ProductionCookie document. OpenAPI cannot represent a valid token in either the header or a form field as one required constraint. The document therefore marks each alternative transport optional and says that validation still requires a valid token. JSON writes that are on the allowlist require `X-CSRF-Token` because their body is JSON. The DevelopmentJwt document does not describe CSRF as required.

A request-time authentication or authorization denial has no body. Exact confirmation, explicit allocation, reversal, and batch execution can also return handler `application/problem+json` with code `reconciliation.actor.required`. On ProductionCookie, batch execution is denied before that handler. An allowlisted cookie write can additionally receive `application/problem+json` from `FinancialCommitGuard` for both HTTP 401 and HTTP 403. The document names these variants and does not claim that every denial has a body or that every denial is empty.

## Consequences

- One Development document follows the host that generated it.
- Changing the composition changes the scheme, the CSRF parameters, and whether the browser session routes exist.
- Business request and response schemas, idempotency, and runtime authentication stay unchanged.
- OpenAPI remains unpublished outside Development.
- Alternative CSRF transports remain a documented limitation of the schema, not a second validation rule.
