# ADR 0020: Identity-bound financial recovery

- Status: Accepted
- Date: 2026-11-03

## Context

Phase 7D.4b1 allows ProductionCookie to reach only exact confirmation, explicit allocation, and reversal when both financial activation flags are `Enabled`. The React client still rejected every cookie financial POST, and exact confirmation kept its idempotency key only in component memory.

## Decision

Session contract v2 keeps the six read capabilities in their existing order. When `AuthenticationCompositionState.ProductionFinancialWritesEnabled` is true, it appends only the permissions of the operations in `ProductionFinancialWriteActivation`: `reconciliation.confirm`, `allocations.create`, and `allocations.reverse`. `imports.create`, `review-cases.open`, and `review-cases.resolve` are not advertised. `financialAccess` is true only when at least one enabled read is present. Contract v1 still omits `permissions` and keeps `financialAccess` false. A role-table failure remains HTTP 503 `auth.authorization.unavailable`.

A new attempt cannot replace an unresolved descriptor for the same operation. Manual recovery reuses the stored key, target and body. The descriptor is removed only after the response body has been read and the originating identity, organization, epoch, readiness and capability are still current. A success body must match that operation's DTO. The fingerprint detects accidental corruption. It is not a trusted MAC.

The cookie client recognizes those three write strings and ignores every other string. A write string does not make `financialAccess` true by itself. Capability changes still rotate the security epoch. A CSRF-only refresh does not.

A cookie financial POST for one of the three operations persists a schema version 2 descriptor in `sessionStorage` before `fetch`. The descriptor stores the verified issuer, subject, active organization, operation, idempotency key, exact body, and a fingerprint of that body. It does not store a cookie, a bearer token, or a CSRF token. Storage failure means the POST is not sent. The descriptor is not an authorization credential. The server still checks the current session, CSRF, the allowlist, the exact permission, and the commit-time guard. Idempotency keys stay organization-scoped.

A descriptor starts as `dispatched` before `fetch`. A response that the server proves happened before commit, on that first dispatch, is stored as `rejected`. The operator can discard that record and send a corrected request with a new key. HTTP 401, HTTP 403, invalid CSRF, and the known validation or domain-conflict codes are in that set. An idempotency payload conflict is not, because a stored operation may already exist. A lost response, HTTP 5xx, or an unreadable success body becomes `uncertain` and is not discarded. A later HTTP 403 or HTTP 409 does not erase that uncertainty and does not mint a new key. Failure to delete the local record after a verified success is reported as a cleanup failure. It is not described as "not sent" and it is not a rollback.

Manual retry sends the stored key and body. There is no automatic retry and no new key after an uncertain outcome, including HTTP 5xx, a lost response, or a later HTTP 403. A descriptor for another issuer, subject, or organization is not shown and is not rebound. DevelopmentJwt keeps recovery descriptor v1.

Invoice import, bank import, batch execution, and ReviewCase writes stay outside the allowlist and have no cookie mutation UI. Batch receipt finalization and a general operation-audit table stay deferred. Deployed configuration leaves both activation flags unset. Removing the write flag stops new cookie writes and does not revoke `BrowserSession` or delete a stored descriptor.

## Consequences

- An old bundle ignores unknown write strings and stays read-only.
- A new bundle against a backend that does not advertise writes stays read-only.
- Enabling the write flag is still an operational step after this increment is reviewed and deployed with a compatible bundle.
