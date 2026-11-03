# ADR 0019: Transactional financial write authorization

- Status: Accepted
- Date: 2026-11-03

## Context

ADR 0018 authorizes ProductionCookie financial reads when `Authentication:FinancialReadAuthorization` is exactly `Enabled`. Every financial POST stayed forbidden. A request-scoped role snapshot is not a commit-time decision: a role or session can change after the handler starts and before `SaveChanges`.

Phase 7D.4a adds the backend write check only. It does not mount cookie write UI, does not publish write capabilities on the session contract, and does not enable the write flag in deployed configuration. Identity-bound recovery remains later work.

## Decision

### Permission matrix

`review-cases.open` is a new fixed permission. Role values stay 1–4. There is no migration for a code-defined permission.

| Route | Permission |
| --- | --- |
| `POST /api/imports/invoices` | `imports.create` |
| `POST /api/imports/bank-transactions` | `imports.create` |
| `POST /api/matches/confirm` | `reconciliation.confirm` |
| `POST /api/matches/batch/execute` | `reconciliation.confirm` |
| `POST /api/allocations` | `allocations.create` |
| `POST /api/allocations/{allocationId}/reverse` | `allocations.reverse` |
| `POST /api/review-cases` | `review-cases.open` |
| `POST /api/review-cases/{id}/resolve` | `review-cases.resolve` |
| `POST /api/review-cases/{id}/dismiss` | `review-cases.resolve` |

Viewer has no write permission. Operator has the operational writes, including `review-cases.open` and `review-cases.resolve`, and does not have `allocations.reverse`. ReversalApprover has `allocations.reverse` and does not have the Operator-only writes. OrganizationAdmin has no financial write permission. Multiple roles in one organization are the existing union. A role in another organization grants nothing.

A write classification must name one permission from that write set. A read permission cannot classify a write, and a write permission cannot classify a read. A missing, duplicate, unknown, or method-mismatched classification fails closed. `DevelopmentJwt` still accepts any consistent classification from `org_id` and does not read Access roles.

### Activation

ProductionCookie write authorization requires both ordinal values:

- `Authentication:FinancialReadAuthorization` = `Enabled`
- `Authentication:FinancialWriteAuthorization` = `Enabled`

| Read | Write | GET | POST |
| --- | --- | --- | --- |
| off | off | denied | denied |
| on | off | exact read permission | denied |
| off | on | denied | denied |
| on | on | exact read permission | only an allowlisted operation, then its exact write permission, rechecked in the committing transaction |

Any other string is off. The flags do not skip authentication, membership, CSRF, or the commit-time check. There is no third activation flag. Deployed `appsettings` leave both keys unset. Setting them does not by itself authorize production use: cookie write UI and identity-bound recovery are still required before an operator enables the write key. `DevelopmentJwt` ignores both flags and the cookie allowlist.

### Cookie write allowlist

Both flags open an activation boundary. They do not open every classified POST. The boundary reads `FinancialWriteOperation` from the endpoint metadata. It does not read a permission name, a client operation name, a header, a query string, or a path substring. `POST /api/matches/confirm` and `POST /api/matches/batch/execute` both require `reconciliation.confirm` and are different operations.

When both flags are `Enabled`, ProductionCookie may pass the boundary only for:

- `POST /api/matches/confirm` (`ConfirmExactMatch`, `reconciliation.confirm`)
- `POST /api/allocations` (`CreateAllocation`, `allocations.create`)
- `POST /api/allocations/{allocationId}/reverse` (`ReverseAllocation`, `allocations.reverse`)

These three still require the current exact permission, a valid browser session, the trusted organization, CSRF, and the commit-time Shared lock. The allowlist does not grant that permission.

The other six financial POSTs stay forbidden for ProductionCookie, including for a role that holds their permission:

- `POST /api/imports/invoices`
- `POST /api/imports/bank-transactions`
- `POST /api/matches/batch/execute`
- `POST /api/review-cases`
- `POST /api/review-cases/{id}/resolve`
- `POST /api/review-cases/{id}/dismiss`

Their classifications and permission requirements stay. A future write endpoint is denied until its operation is added to this same set. Denial is the authorization requirement, before the endpoint handler. A denied cookie POST does not insert, update, or delete financial rows and does not write an idempotency record, import batch, batch receipt, review case, or audit row. Invalid CSRF remains HTTP 400 `auth.csrf.invalid`. An authenticated cookie request for an operation outside the set remains HTTP 403.

`FinancialCommitGuard` uses the same operation set. A write that is outside it does not take the organization application lock and does not commit. The three allowed operations still revalidate the user, `BrowserSession`, membership, organization, and role inside the committing transaction. Removing `Authentication:FinancialWriteAuthorization` stops new cookie writes and does not invalidate `BrowserSession`.

### Application lock

The resource name is `finopsdesk-auth:{organizationId:D}` in invariant lowercase. `sp_getapplock` is scoped by database id, principal, and resource name. Every participant uses the FinOpsDesk catalog, principal `public`, and `LockOwner = Transaction`. A matching string in another database does not coordinate.

Financial writes take `Shared`. Access mutations that change organization-scoped write eligibility take `Exclusive` on that organization before their existing `Serializable` and `UPDLOCK`/`HOLDLOCK` protocol, and they hold it until commit. The wait is `SqlApplicationLockPolicy.DefaultWaitTimeoutMilliseconds` (10000). A session `LOCK_TIMEOUT` of zero or a positive value that is shorter than the policy wins, so `SET LOCK_TIMEOUT 0` still fails immediately.

A nonnegative return code is acquisition. `-1` is timeout, `-2` is cancellation, `-3` is deadlock, and `-999` is any other procedure failure. A negative code is never success. The caller rolls the transaction back and does not mutate financial rows. Lock failure is HTTP 503 `auth.authorization.lock_unavailable`. It is not an empty permission set and it is not retried. A role value outside 1–4 or a SQL failure while reading authorization state is HTTP 503 `auth.authorization.unavailable`. A verified missing permission is HTTP 403 `auth.commit.forbidden`. A missing, disabled, or revoked user or browser session is HTTP 401 `auth.commit.unauthenticated`. The response does not include the SQL return code or exception text.

The Shared lock is taken on the same SQL transaction that commits the financial change. Reconciliation does not read `access` tables. The API guard runs the Access SQL on the caller's connection and transaction. There is no `TransactionScope` and no in-memory cross-instance lock. CSV parsing and batch matching stay outside that transaction.

### Commit-time revalidation

After the Shared lock, the same transaction reads and locks, with `UPDLOCK` and `HOLDLOCK`:

1. the application user row for the cookie identity key;
2. the presented `BrowserSession`;
3. the active membership in the trusted organization;
4. that organization row;
5. the membership's active role rows.

The check uses the server-side issuer, subject, session stamp, browser-session id, and active organization. It does not accept a user id, organization id, role, or actor id from a header or body, and it does not use the request-scoped `AccessSessionGate` snapshot. Logout and the conditional stamp update lock the session row or the user row. They do not take the organization application lock, so they do not deadlock with the order above. A revocation is ordered with the financial commit only when its writer blocks on one of those locked rows or on the organization application lock.

`BrowserSession.ExpiresAt` stays the original absolute eight-hour deadline. The comparison reads `TimeProvider` only after that session row has been read under the Shared lock. A timestamp taken before `sp_getapplock`, or before the blocking user and session reads, is not used. A session that expires while the transaction waits for the lock is HTTP 401 and does not commit. A missing or non-integer `sp_getapplock` result is treated as `-999` and does not count as acquisition. `Convert.ToInt32(null)` is 0, so it is not used.

### Who takes the Exclusive lock

These paths take the organization Exclusive lock and hold it through commit:

- `OrganizationRoleStore.GrantAsync`
- `OrganizationRoleStore.RevokeAsync`
- `OrganizationRoleStore.GrantForAdministratorAsync`
- `OrganizationRoleStore.RevokeForAdministratorAsync`
- `DeploymentRoleBootstrap.CreateOrganizationAsync`
- `DeploymentRoleBootstrap.GrantMembershipAsync`
- `DeploymentRoleBootstrap.GrantRoleAsync`
- `DeploymentRoleBootstrap.RevokeRoleAsync` through the role store

The last-OrganizationAdmin rule and the existing administrator, membership, and assignment lock order stay after the application lock.

There is no production HTTP or bootstrap command that revokes a membership, disables an organization, or disables an application user. Those domain methods exist. A test or an operator who updates those rows outside this protocol is outside the guarantee. The database has no trigger that takes this application lock.

### Handlers

Ordinary single-commit writes (`confirm`, explicit allocation, reversal, review open, resolve, and dismiss) keep their current `SaveChanges` behavior. When no transaction is open, `ReconciliationDbContext.SaveChangesAsync` begins one, authorizes it, saves, and commits. A caller that already began a transaction authorizes that transaction and still owns the commit. A failed lock rolls that transaction back before a unique-constraint recovery query.

CSV import still parses outside the transaction. `CsvImportStore.CommitAsync` authorizes inside the commit `SaveChanges`. A deadlock retry opens a new scope, a new transaction, and a new lock. Hash idempotency is unchanged.

Batch execution is not one atomic financial transaction. The batch-owner connection keeps the existing batch-row `UPDLOCK` and does not hold the organization application lock across items. Each item authorizes its own transaction. A role revoked after one item commits prevents later items. Earlier allocations stay committed. The batch row stays `Running`: the HTTP request has stopped, the completed receipt was not written, and the committed item was not rolled back. There is no batch-status GET. The caller sees the authorization error from the POST and can see the committed allocation through an authorized financial GET.

A later POST with the same organization-scoped batch key and the same pairs resumes a `Running` row while it is inside the existing 15-minute resume window. An item whose allocation idempotency key is already committed is recovered and is not inserted again. A pending item executes once. When the role is still absent, that retry is HTTP 403, the row stays `Running`, and no further item is written. When the write permission is restored before the retry, the remaining items run and a successful resume marks the row `Completed`. A third POST with the same key returns that stored receipt and does not allocate again. This increment adds no batch status and no migration.

The receipt `UPDATE` runs as raw SQL on the owner transaction after the items. It does not pass through `SaveChangesAsync` and does not take a fresh organization application lock. Item allocations do. A revocation that lands after the last item commit and before that receipt update can still mark the batch completed. Phase 7D.4b1 does not restructure that owner lease. Releasing the `UPDLOCK` before a later Shared lock would open an ownership race, so receipt finalization stays as it is. ProductionCookie cannot reach that path: batch execution is outside the cookie allowlist and is rejected before the handler. `DevelopmentJwt` keeps the current Running, Completed, 15-minute resume, item idempotency, and receipt behavior. A later batch increment has to design ownership transfer, stale-owner rejection, conditional receipt persistence, and commit-time authorization together before that route can be added to the allowlist. No new batch status and no migration are added now.

### Replay and idempotency

A financial POST requires the current write permission whether it would insert or return a stored result. After the permission is revoked the POST is HTTP 403. It does not return the stored body, delete the idempotency row, or create a replacement key. An earlier commit is not rolled back. A later GET can still show current state when the caller has the read permission.

Idempotency keys stay organization-scoped. They are not actor authentication. Organization-scoped replay is not an identity-bound recovery descriptor.

The existing keys stay as they are: import hash, confirmation and explicit-allocation keys, reversal key, and batch plus item keys. Review open still uses the open-subject uniqueness rule. Review resolve and dismiss still use the row version and the closed-state checks. They do not replay a stored success document. A second committed financial effect is still rejected by the existing unique constraints and domain checks.

### Audit

Exact confirmation, explicit allocation, reversal, and each committed batch item still write `FinancialAuditEntry`. The cookie actor is `ApplicationUser.Id`. The JWT actor is the validated `sub`. `audit.read` is unchanged.

CSV import, the batch execution header, and review open, resolve, and dismiss do not write that entry. `FinancialAuditEntry` requires an allocation, an invoice, and a bank transaction, so those operations cannot be attributed through it without a schema change. This increment does not add a general operation-audit table. The later model, when one of those routes is considered for the allowlist, is a small reconciliation record written in the same transaction as the protected effect: organization, operation, subject, server-side actor, and time. It does not replace allocation audit rows. Until that record and a reviewed recovery path exist, those six routes stay outside the cookie allowlist. The cookie actor for an allocation audit remains `ApplicationUser.Id`. The JWT actor remains the validated `sub`.

### Browser contract

Session contract v1 and v2 are unchanged. Contract v2 still returns only enabled read capabilities. The React client still rejects cookie financial mutations. Recovery v1 is unchanged. Recovery v2 is not part of this increment.

## Consequences

- Deployed ProductionCookie financial POSTs stay denied until a later reviewed increment enables the write flag together with cookie write UI and identity-bound recovery. The allowlist does not authorize that operational step. Phase 7D.4b2/c still has to ship before the write key is enabled.
- Enabling the read flag alone does not enable writes. Enabling both flags opens only confirm, explicit allocation, and reversal for ProductionCookie.
- Two API instances coordinate only when they use the same database and the same principal. Out-of-band SQL does not.
- Import and batch execution can leave a partial committed result. The HTTP error does not claim that the earlier item or a previous import was rolled back.
- JWT financial behavior, including idempotency and recovery v1, stays on the existing path and does not take this lock.
