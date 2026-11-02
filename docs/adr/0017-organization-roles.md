# ADR 0017: Organization-scoped roles

- Status: Accepted
- Date: 2026-11-02

## Context

ADR 0014 identifies a person by issuer and subject and stores organization membership. ADR 0015 trusts `finopsdesk_org` only after that membership check. ADR 0016 revokes one browser session without rotating the user stamp. None of those records decides what the person may do inside an organization.

A configurable IAM store would let operators invent permissions that no endpoint implements. The financial surface is already fixed. Identity-provider groups, roles, and `org_id` are not financial authority: the production cookie does not carry them, and DevelopmentJwt must keep working without Access.

The first administrator cannot be created by a rule that requires `roles.manage`, because nobody has that permission yet. An anonymous HTTP seed would make that gap reachable from the network.

## Decision

Access stores assignments, not role definitions. `OrganizationRole` is a `byte` with explicit values: `Viewer` 1, `Operator` 2, `ReversalApprover` 3, `OrganizationAdmin` 4. A membership may have several active assignments. Effective permissions are the union of the fixed sets below. No assignment is an empty set. `OrganizationAdmin` receives `membership.manage` and `roles.manage` only.

`Viewer`: `invoices.read`, `bank-transactions.read`, `reconciliation.read`, `allocations.read`, `review-cases.read`.

`Operator`: the Viewer set plus `imports.create`, `reconciliation.confirm`, `allocations.create`, `review-cases.resolve`, `audit.read`.

`ReversalApprover`: the Viewer set plus `allocations.reverse`, `audit.read`.

There is no `imports.read`. No import-history read exists. Opening, resolving, and dismissing a review case share `review-cases.resolve`. Those routes are not authorized in this increment.

`OrganizationRoleAssignment` belongs to one membership. It does not copy `OrganizationId`. `RevokedAt` is the only revocation mark. A revoked row stays revoked. A later grant inserts a new row. The filtered unique index `UX_OrganizationRoleAssignments_MembershipId_Role_Active` allows one active row per membership and role. Check constraints limit `Role` to 1–4, require complete grant provenance, and require complete revoke provenance with `RevokedAt` strictly after `GrantedAt`. Foreign keys use `ON DELETE NO ACTION`. A shadow `rowversion` is present. Revocation uses a conditional update, not a tracked stale entity.

Grant provenance is either an `ApplicationUser` id with no operator name, or deployment bootstrap with no user id and a nonblank operator name of at most 128 characters. The same pair is required when the row is revoked. Bootstrap is not recorded as an ordinary user grant.

`FinOpsDesk.AccessBootstrap` is a separate console. It is not registered in the API host and has no HTTP route. It can create an organization, grant a membership to an existing issuer and subject, and grant or revoke a role. It does not create an `ApplicationUser`. A revoked membership is not silently restored. `grant-role` and `revoke-role` require `--operator`. That value is stored as deployment audit metadata. It is not a credential, and the process does not substitute `Environment.UserName`. Permission to run the console is the deployment environment together with the SQL credential. The connection string is read from the environment and is not written to stdout. Invalid arguments return before any SQL call. SQL failures print `result=unavailable` without the provider message.

Ordinary grant and revoke go through `IOrganizationRoleAdministration`. The store opens one serializable transaction, locks the organization's active `OrganizationAdmin` rows with `UPDLOCK, HOLDLOCK`, and only then reads the actor and the target. The actor must have an active user, an active membership in the named organization, an active organization, and `roles.manage` from a locked administrator row. The target membership must belong to that same organization. A grant or revoke that has not taken those locks cannot commit after the actor's administrator row was already revoked. A transaction that already holds the locks makes a conflicting administrator revocation wait until it completes. The same administrator-row lock is the first lock for every change of an `OrganizationAdmin` assignment, including bootstrap. A repeated grant of an active role returns `AlreadyActive` and does not insert another row.

Revocation updates one `OrganizationRoleAssignment.Id` whose `RevokedAt` is still null, and it compares the expected `rowversion` when the caller supplies one. Zero rows do not select a replacement assignment. A revoked row stays revoked with its original grant and revocation metadata. A later grant is a new id. Revoking the last active `OrganizationAdmin` assignment in an organization returns `LastOrganizationAdmin`. Bootstrap uses that same safeguard. An organization may start with none. A new grant repairs that. Disabling a membership or user does not delete role rows. Those rows stop being effective until the membership and user are active again. `Restore` therefore makes a still-unrevoked role effective again.

Basic cookie validation resolves the application user, `SessionStamp`, `BrowserSession`, and memberships. It does not read `OrganizationRoleAssignments`. `GET /api/session`, login, logout, and organization selection stay available when only that table is unavailable. A separate request-scoped operation resolves permissions from the trusted snapshot for the active organization. It is not a claim, a cookie field, or a property of `GET /api/session`. The result is memoized for that request and is not reused by the next request. The next request sees a grant or a revocation without a new OIDC login and without rotating `SessionStamp` or `finopsdesk_sid`. A role value outside 1–4 fails that operation as `inconsistent-role`. A SQL failure fails it as `database`. Neither becomes an empty permission set. A successful read with no rows is an empty set. A rejected session or a revoked browser session does not resolve trusted permissions. Cancellation is not that failure.

`FinancialApiAccessRequirement` still fails closed for `ProductionCookie` and does not read the permission set. `TenantMember` still requires `org_id`, which the cookie does not carry. `GET /api/session` still returns `financialAccess: false`. DevelopmentJwt does not read or write role rows.

`datetimeoffset` compares at 100-nanosecond precision. If the revocation clock is equal to `GrantedAt`, the stored `RevokedAt` is one tick later so the check constraint can hold. An earlier clock value is rejected.

## Consequences

- Different organizations can assign different roles to the same person.
- Historical assignments remain after revocation.
- `Down` on this migration drops that history. Production rollback is a forward fix.
- Financial HTTP authorization waits for Phase 7D.3. Phase 7 stays planned.
- More than one API instance still needs the shared Access database. This record does not add a cache.
