# ADR 0004: Trusted OrganizationId from authenticated claims

- Status: Accepted
- Date: 2026-10-08

## Context

Reconciliation commands and queries must run inside one organization. Phase 2A introduced `ICurrentOrganizationContext` as the application port for that organization, and persistence already scopes reads and writes by `OrganizationId`. The first HTTP use case, `GET /api/invoices`, needs a production source for that port.

The API is called with access tokens. It must not invent users, issue tokens, or treat a client-supplied organization selector as authority. A later browser backend-for-frontend may replace the token transport. The tenant boundary should stay stable when that happens.

## Decision

FinOpsDesk.Api validates JWT Bearer access tokens. It does not issue them.

Validation covers issuer or authority, audience, signing credentials or OpenID Connect metadata, token lifetime, and HTTPS metadata. `MapInboundClaims` is disabled. The organization claim type is configuration and defaults to `org_id`. No signing keys or other secrets are stored in the repository.

Authentication and tenant authorization are different decisions.

- A fallback policy may require an authenticated user.
- The named `TenantMember` policy requires an authenticated user and an organization claim that parses to a non-empty Guid.
- That policy is applied to the Reconciliation route group. `/health` stays anonymous.
- A missing or invalid token is HTTP 401. A valid token with a missing, malformed, or empty organization claim is HTTP 403.

`RequireClaim` is not sufficient, because the claim must also be a non-empty Guid. One API-host parser implements that rule for both the authorization handler and the organization context.

The production `ICurrentOrganizationContext` lives in the API host. It reads `HttpContext.User` after authentication. OrganizationId is not taken from the query string, route, body, `X-Organization-Id`, or any other request value. If the context is used without a valid organization claim, it throws. It does not substitute a default organization.

The configured token issuer is trusted to put the organization claim on a token only when the caller is allowed to act in that organization. FinOpsDesk does not keep a membership store in this slice.

`ListInvoicesHandler` reads that organization once and passes it into `IInvoiceQueries`. Query infrastructure does not receive `ICurrentOrganizationContext`.

A future backend-for-frontend or cookie session can replace JWT Bearer without changing this tenant abstraction. The host would still implement `ICurrentOrganizationContext`, and Reconciliation would still receive `OrganizationId` explicitly.

## Alternatives considered

### Client-supplied organization header or query value

Rejected. The caller could select another tenant. Request values are ignored even when a valid token is present.

### Fallback policy that requires an organization claim

Rejected. General authentication and tenant authorization are separate. Endpoints that are only authenticated must be able to exist without an organization claim. Reconciliation opts into `TenantMember`.

### `RequireClaim` only

Rejected. It checks presence, not that the value is a non-empty Guid. An empty Guid would otherwise pass authorization and then fail inside `OrganizationId`, or worse, be mishandled.

### Organization context inside the Reconciliation module

Rejected for the production adapter. The module must not reference the API host, and it must not read HTTP. The host adapts the authenticated principal. The module keeps the port.

### Inject `ICurrentOrganizationContext` into query classes

Rejected. The query port takes `OrganizationId` explicitly, so tenant scope stays visible at the call and cannot depend on ambient HTTP inside Infrastructure.

## Consequences

Positive:

- invoice reads are scoped by the authenticated organization;
- a client cannot switch tenant with a query string or header;
- claim parsing has one implementation;
- the module stays independent of the API host;
- a later browser session can reuse the same organization port.

Negative:

- the API trusts the token issuer to assert organization membership correctly;
- there is still no user directory, role model, or token issuance in this service;
- HTTP 409 for allocation conflicts is not part of this decision.
