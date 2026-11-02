# ADR 0016: Server-side browser session revocation

- Status: Accepted
- Date: 2026-11-02

## Context

ADR 0015 keeps one `ApplicationUser.SessionStamp` for the person. Rotating that stamp signs out every browser that still presents the old ticket. The cookie screen says local sign-out ends the session in this browser and leaves the identity provider, and any other browser, alone. A user-wide stamp change on ordinary logout would contradict that.

Phase 7C.3 can still store a late `Set-Cookie` from `POST /api/session/organization` after another tab has signed out. The React epoch and `BroadcastChannel` hint do not stop the browser from storing that cookie. ADR 0015's note that the browser ignores `activeOrganization` was superseded by Phase 7C.3; this record does not rewrite ADR 0015. The remaining hole is server-side: a ticket that was valid when the organization response was built must not become authoritative after that browser session was revoked.

Financial routes stay fail-closed. This decision does not grant `ProductionCookie` access to them.

## Decision

Access persists one `BrowserSession` row per completed OIDC login. The row is created only after protocol validation, an exact issuer and subject, a successful application-user provision or lookup, and confirmation that the user is active. The insert happens before the application cookie is issued. A failed insert does not issue the cookie and is reported as `auth.identity.unavailable`, without SQL details. An invalid issuer, audience, signature, state, correlation, or nonce path does not insert a row.

The cookie carries `finopsdesk_sid`, the row id. It is generated with `Guid.NewGuid()` on the server. It is not an organization and not a permission. The client cannot submit a session id outside the protected ticket. A missing, empty, duplicated, or malformed `finopsdesk_sid` rejects the principal and clears the cookie. A cookie issued before this decision has no session id and is rejected the same way. Request validation never creates a row for an old cookie. The operator signs in through OIDC again.

`OnValidatePrincipal` still checks issuer, subject, and `finopsdesk_stamp` first. It then loads the row and requires the same `ApplicationUser`, a null `RevokedAt`, and `ExpiresAt` later than the current time. A valid `finopsdesk_org` does not skip those checks. A rejected or unavailable result publishes no trusted user and no trusted organization. The result stays request-scoped. There is no cross-request session cache.

`RevokedAt` is the only revocation mark. There is no second status column. Natural expiry is `ExpiresAt`. Revocation does not change `ExpiresAt`, and a revoked row is not made active again. The foreign key to `ApplicationUser` is `ON DELETE NO ACTION`. Application users and sessions are not cascade-deleted.

The row and the authentication ticket share one deadline. The callback reads the cookie handler's `TimeProvider` and `ExpireTimeSpan`, stores that `CreatedAt` and `ExpiresAt` on the row, and sets `AuthenticationProperties.IssuedUtc` and `ExpiresUtc` to the same values before sign-in. The lifetime stays eight hours, absolute, with sliding expiration off. Organization selection copies the existing session id, stamp, `IssuedUtc`, and `ExpiresUtc`. It does not insert a row, does not change `ExpiresAt`, and does not set `ShouldRenew`.

`POST /logout` revokes only the session id already validated for that cookie. It does not accept a session id from the client. The update is conditional on the id, the user id, and `RevokedAt IS NULL`. HTTP 204 is returned only after that update changed the row, or a follow-up read shows that this user's row is already revoked, and only then is the local cookie cleared. A missing row or a user mismatch is not a confirmed logout. A database failure returns HTTP 503 `auth.membership.unavailable`, leaves the cookie in place, and does not return 204. `OnSigningOut` does not force 204. Provider end-session is not called. Another active row for the same user stays valid, and logout does not rotate `SessionStamp`.

Disabling an application user still rotates `SessionStamp`. Restore does not put the old stamp back. This increment does not update every `BrowserSession` row on disable: the old ticket already fails the stamp check, including when no request arrived during the disabled period. A later OIDC login creates a new row and does not rotate the stamp again.

If SQL Server cannot be reached, the gate is unavailable. That is not the same as a missing, revoked, or expired session. Session reads and organization selection return HTTP 503 and do not clear an otherwise unexpired cookie. Cancellation is not reported as a database failure. No financial handler runs.

A callback that commits the row and then fails before the cookie is issued can leave an orphan row. The row's id was never placed in a ticket, so it cannot authorize a request. Expired orphans are inert. A cleanup job is not part of this increment.

More than one API instance must share the Access database and the ASP.NET Core Data Protection key ring. This increment does not add a distributed cache or a shared key ring.

## Consequences

- Ordinary logout ends one browser session. Other browsers for the same person keep their own rows and the same user stamp.
- A late organization `Set-Cookie` repeats the original `finopsdesk_sid`. After logout, validation finds that row revoked, returns an anonymous session, clears the resurrected cookie, and exposes no organization.
- Cookies without a server session id stop working. Users sign in again.
- `DevelopmentJwt` does not read or write `BrowserSession`.
- `FinancialApiAccessRequirement` remains fail-closed for `ProductionCookie`. `GET /api/session` still returns `financialAccess: false`.
- Phase 7 remains planned. Roles, financial reads, financial writes, and recovery schema version 2 are later increments.
