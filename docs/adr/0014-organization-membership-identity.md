# ADR 0014: Organization membership identity

- Status: Accepted
- Date: 2026-11-01

## Context

ADR 0004 trusts a JWT issuer to place `org_id` on a DevelopmentJwt access token. ProductionCookie does not carry that claim. A signed-in operator still needs a stable application identity and a list of organizations the application itself has granted. The provider's email, display name, and `org_id` are not membership.

SQL Server compares `nvarchar` values as equal when they differ only by trailing spaces. That comparison remains true under the `Latin1_General_100_BIN2` collation. A unique index on issuer and subject would merge those identities.

`OpenIdConnectHandler` raises `OnTokenValidated` before `ProtocolValidator.ValidateTokenResponse` checks the nonce on the authorization-code flow. A user row created in that earlier event could survive a callback that never becomes a session.

## Decision

The Access module owns `ApplicationUser`, `Organization`, and `OrganizationMembership` in schema `access`. Its EF Core history table is `access.__EFMigrationsHistory`. Reconciliation tables do not reference Access organizations.

The external identity is the validated issuer and subject, stored exactly. The unique lookup key is SHA-256 over a big-endian length prefix and the UTF-8 bytes of the issuer, followed by the same prefix and bytes of the subject. A stored row is that identity only when the digest matches and both strings also compare equal with ordinal comparison. A digest collision with different strings does not merge the users.

The application creates an active user with no membership from `OnTicketReceived`, after remote validation has succeeded and before the cookie handler signs in. Disabled users, invalid identities, and database errors do not receive that cookie. Sign-in does not create organizations, memberships, or roles.

`GET /api/session` lists active memberships of active organizations for an active user. An empty list is a successful read. A failed read is HTTP 503 `auth.membership.unavailable`. The endpoint does not choose an active organization and does not reissue the cookie. `financialAccess` stays false. `DevelopmentJwt` is unchanged and still follows ADR 0004.

## Consequences

Positive:

- two issuer and subject pairs that differ by case or trailing spaces stay distinct;
- a failed OIDC callback does not leave a user row behind the nonce check;
- financial routes stay fail-closed;
- existing reconciliation rows do not need a parent organization row.

Negative:

- active organization selection, per-request revocation, and identity-scoped recovery remain later Phase 7C work;
- a cookie issued before a user is disabled is not revoked by this decision; ADR 0015 adds that request-time check;
- more than one API instance still needs a shared Data Protection key ring, which this decision does not add.
