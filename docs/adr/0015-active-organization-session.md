# ADR 0015: Active organization session

- Status: Accepted
- Date: 2026-11-01

## Context

ADR 0004 trusts `org_id` on a DevelopmentJwt access token. ADR 0014 stores the production identity as the exact issuer and subject and lists active memberships, but it does not choose a tenant. A production cookie that only repeats issuer and subject would match a later row created for the same pair, so disabling or deleting the user would not retire an already issued ticket.

ASP.NET Core antiforgery already binds a token to a claim uid or a username when one is present. This application's provider issuer is a separate `iss` claim. The built-in extractor can pair `sub` with the claim's own issuer, which is not that provider issuer. Organization selection must not be part of that binding: switching organizations is the same person.

`CookieAuthenticationHandler.RequestRefresh` replaces `IssuedUtc` with the current time and sets `ExpiresUtc` to that time plus the original span. Calling `ShouldRenew` from validation would restart the eight-hour lifetime. The cookie remains non-sliding.

## Decision

`ApplicationUser.SessionStamp` is a server-generated identifier stored on the user and copied to the application cookie as `finopsdesk_stamp`. `OnTicketReceived` writes it only after a completed, allowed provision. `OnValidatePrincipal` reads the exact issuer, subject, and stamp, then asks Access for the current user and active memberships. The result stays in a request-scoped gate. It is not cached for a later request.

`ApplicationUser.Disable` rotates that stamp in the same change that moves the user from Active to Disabled. A second disable does not rotate it again. `Restore` changes the status and leaves the new stamp in place, so a cookie issued before the disable stays rejected even when no request arrived while the user was disabled. A new OIDC login can issue a cookie for the current stamp.

A request that finds the user still disabled retires the presented stamp only when that stamp is still current. The update is one conditional `UPDATE` on the disabled row and that stamp, so it does not replace a newer stamp or a row that is no longer disabled. The handler then rejects the principal and signs out the application-cookie scheme. A verified missing user is signed out the same way and is not recreated from the cookie. A stamp mismatch on an otherwise active user also signs out, without rotating the current stamp.

A database failure or inconsistent identity data does not sign the user out and does not become an empty membership. The gate is unavailable. `GET /api/session` and `POST /api/session/organization` return HTTP 503 `auth.membership.unavailable`. No trusted user or organization is published. A later request may validate the same unexpired cookie. Cancellation is not reported as that failure.

`finopsdesk_org` is an optional remembered selection. Login does not set it. `GET /api/session` does not set it, even when the user has one organization. `POST /api/session/organization` sets it only when the requested organization is in the current active membership list. The claim is not proof of membership. The trusted organization is the request-scoped result after Access accepts that same identifier. A missing, empty, duplicated, malformed, revoked, or disabled selection leaves no trusted organization and does not choose a different one. If the original membership and organization are active again on a later request, the same claim can become trusted again. That is remembered-selection, not a new automatic choice.

Reissue uses `SignInAsync` and copies `IssuedUtc` and `ExpiresUtc`. It does not copy `RedirectUri` and does not set `ShouldRenew`. `GET /api/session` does not reissue the application cookie.

Production `ICurrentOrganizationContext` reads only an active gate result. Production `ICurrentActorContext` returns `ApplicationUser.Id` in Guid format `D` and does not fall back to `sub`. DevelopmentJwt still reads `org_id` and `sub` and does not call Access. `FinancialApiAccessRequirement` still fails closed. `TenantMember` still requires `org_id`, which the production cookie does not carry.

`IAntiforgeryAdditionalDataProvider` adds a second identity check. An anonymous caller uses a fixed anonymous marker. An authenticated caller uses the lowercase hex digest from `ExternalIdentityKey.Compute` of the single issuer and subject. A missing or malformed authenticated identity uses a distinct invalid marker and never validates as anonymous. The organization claim is not an input, so a switch does not invalidate a token for the same identity. `ProductionCsrfMiddleware` is unchanged.

Two browser tabs can each receive a `Set-Cookie`. The cookie the browser keeps is the one it applies last. The next request validates whatever selection that cookie presents. An in-flight financial transaction is not cancelled by a concurrent revocation. Financial mutations are still closed; Phase 7D and 7E must check authorization inside the transaction when they open.

## Consequences

Positive:

- a disabled or deleted user cannot keep using the old ticket, and recreating the account does not revive it;
- a SQL outage does not look like an empty organization list and does not destroy the cookie;
- switching organizations does not extend the eight-hour lifetime;
- two issuers that share a subject stay different people for the tenant, the actor, and CSRF;
- financial routes stay forbidden.

Negative:

- the React organization selector is still Phase 7C.3, so the browser ignores `activeOrganization`;
- financial handlers still do not run, so the trusted tenant is proven only by a test-host probe;
- two tabs do not have a single winner beyond the cookie the browser stored;
- a shared Data Protection key ring is still required for more than one API instance and is not added here;
- an already authorized financial transaction, once those transactions exist, is not aborted in flight when membership changes.
