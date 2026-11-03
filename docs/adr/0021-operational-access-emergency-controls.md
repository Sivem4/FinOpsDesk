# ADR 0021: Operational access emergency controls

- Status: Accepted
- Date: 2026-11-03

## Context

ADR 0019 rechecks a ProductionCookie financial write under the organization Shared application lock and an `UPDLOCK, HOLDLOCK` on `access.ApplicationUsers`. ADR 0017 records role grant and revoke on `access.OrganizationRoleAssignments`. Neither store can say who disabled a user, revoked a membership, or disabled an organization, or whether that command changed state or found the state already applied.

`ApplicationUser.Disable`, `OrganizationMembership.Revoke`, and `Organization.Disable` already exist. Calling them from SQL skips the lock that `FinancialCommitGuard` holds and leaves no application audit row. This phase adds three console commands and one append-only audit table. It does not add an HTTP administration API, a React screen, reactivation, or membership restoration.

## Decision

### Audit record

`access.OperationalSecurityEvents` is the only new table. A row has a unique id, an operation (`DisableUser` = 1, `RevokeMembership` = 2, `DisableOrganization` = 3), an outcome (`Applied` = 1, `AlreadyApplied` = 2), the stable target ids, `DeclaredOperator`, and `OccurredAt`.

Disable-user stores `ApplicationUser.Id` and leaves the organization and membership ids null. Revoke-membership stores the user id, the organization id, and the membership id. Disable-organization stores the organization id and leaves the user and membership ids null. Email, cookies, tokens, financial bodies, and connection strings are not stored.

The application inserts a row and does not update or delete it. A repeated command inserts another `AlreadyApplied` row. There is no uniqueness constraint on the target, because a later attempt is a different event. A privileged database administrator can still change the table. The application does not claim the table is tamper-proof.

Indexes are `(TargetUserId, OccurredAt)` and `(TargetOrganizationId, OccurredAt)`, plus the foreign-key index on `TargetMembershipId`. Foreign keys use `ON DELETE NO ACTION`. Existing Access tables, role history, and `FinancialAuditEntry` are unchanged.

### Same transaction

Each command starts one SQL transaction. `Applied` writes the domain change and the audit row in that transaction's single `SaveChanges`, then commits. `AlreadyApplied` leaves the security row unchanged and commits only the new audit row. Validation failure, a missing target, a lock failure, or an exception before commit rolls back. No success token is printed before commit. The console does not retry an ambiguous transaction. A negative `sp_getapplock` code, including timeout, is `lock-not-acquired` and is not `AlreadyApplied`.

### Declared operator

`--operator` is required, nonempty after the existing `RoleActor.DeploymentBootstrap` check, without leading or trailing whitespace, and at most 128 characters. The stored field is `DeclaredOperator`. The console does not authenticate that string. Running the console is authorized by the operating system, the deployment environment, and the SQL credential. `ApplicationUser.Id` remains the trusted browser actor and is not derived from `--operator`.

### Commands

`FinOpsDesk.AccessBootstrap` keeps one parser.

- `disable-user --issuer <issuer> --subject <subject> --operator <declared-operator>`
- `revoke-membership --issuer <issuer> --subject <subject> --organization-id <guid> --operator <declared-operator>`
- `disable-organization --organization-id <guid> --operator <declared-operator>`

Success tokens, exit 0: `user-disabled`, `user-already-disabled`, `membership-revoked`, `membership-already-revoked`, `organization-disabled`, `organization-already-disabled`.

Failure tokens, exit 1, include `invalid-argument`, `invalid-operator`, `user-not-found`, `organization-not-found`, `membership-not-found`, `lock-not-acquired`, and `unavailable`. A missing target is not `AlreadyApplied`. `disabled-user` and `disabled-organization` remain the existing failure tokens of membership grant. The new success tokens are different strings.

The console does not call the OpenID Connect provider.

### Serialization

`disable-user` takes `UPDLOCK, HOLDLOCK` on the one `ApplicationUsers` row inside its transaction, then `ApplicationUser.Disable` when the row is active. `FinancialCommitGuard` already takes that same lock before commit. The winner finishes first. The command does not take an organization application lock, because a financial transaction can hold Shared on an organization and then wait for the user row. Taking Exclusive on that organization while holding the user row would deadlock.

A repeated disable does not call `Disable` again, so `SessionStamp` stays at the value written by the first transition. The old cookie no longer matches. A later request that still presents the disabled user is signed out. This phase does not add a command that sets the user back to Active.

`revoke-membership` and `disable-organization` take the existing Exclusive lock on `finopsdesk-auth:{organizationId:D}` on the same connection and transaction, then `UPDLOCK, HOLDLOCK` on the membership or organization row. The financial path takes Shared first. Exclusive first, then the row lock, is the same order role administration already uses. The lock is released when the transaction commits or rolls back.

A revoked membership stays revoked. `GrantMembership` today returns `membership-inactive` and does not call `Restore`. Role rows stay attached to that membership. A future release that calls `Restore` on the same membership would make those historical roles active again. That restoration is not implemented and must be an explicit authorization decision.

Disabling an organization leaves users, memberships, role history, and financial rows in place. The organization drops out of the trusted active list. Other organizations are not changed. There is no delete and no enable command.

### Deployment

Apply the migration with the application release. Do not run it by hand against a database this repository does not operate. Rollback of a bad release is a rollback of the application code. Do not drop `access.OperationalSecurityEvents` to roll back, because that deletes the audit history. Keep the rows for the retention period the operator chooses. This increment does not add a purge job.

Both financial activation flags stay unset. ProductionCookie financial writes stay closed in deployed configuration.

## Consequences

- An operator can stop a compromised account, one membership, or one organization without editing SQL.
- The audit row and the security change commit together, or neither does.
- A repeated command is visible as `AlreadyApplied` and does not rotate `SessionStamp` or rewrite `RevokedAt`.
- `--operator` is attribution, not an authenticated administrator id.
- Re-enabling a user, restoring a membership, and re-enabling an organization remain future work.
