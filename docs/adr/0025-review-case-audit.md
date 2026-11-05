# ADR 0025: Review case audit foundation

- Status: Accepted
- Date: 2026-11-05

## Context

Opening, resolving, and dismissing a review case changes durable reconciliation state. ADR 0009 left those actions actorless and without an immutable history. ADR 0011 records allocation confirmation and reversal in `FinancialAuditEntry`, which requires an allocation, an invoice, a bank transaction, a positive amount, and an idempotency key. A `NeedsInvestigation` case names one document. Opening or dismissing a case does not change a balance. Extending `FinancialAuditEntry` would invent financial facts those actions do not have.

ADR 0019 kept review open, resolve, and dismiss outside the ProductionCookie write allowlist. That allowlist is unchanged. This decision adds the write-side history those three actions were missing. It does not add a read API, a UI, or a cookie write.

## Decision

A newly executed open, resolve, or dismiss writes exactly one `ReviewCaseAuditEntry` in the Reconciliation module. The case change and the entry are added to the same scoped `ReconciliationDbContext` and commit in the same `SaveChangesAsync` and the same SQL Server transaction. The writer does not open a transaction and does not call `SaveChanges`.

`RecordedAt` is the `OpenedAt` or `ClosedAt` of that same applied decision. The entry does not read the clock again. The payload copies the decision fields only. It does not copy balances, currencies, amounts, the subject key, or the concurrency token. `ReviewCase` does not gain author fields.

The action is `Opened` (1), `Resolved` (2), or `Dismissed` (3). Every entry stores its id, organization, review case id, action, `ActorId`, and `RecordedAt`. `Opened` also stores reason, invoice id, bank transaction id, source label, and note. `Resolved` stores outcome, allocation id, and resolution note. `Dismissed` stores the resolution note. The other payload columns are null. SQL `CHECK` constraints state those required and forbidden nulls explicitly, because a comparison with NULL is UNKNOWN and a CHECK accepts UNKNOWN.

`ActorId` is the trimmed server actor, from 1 to 256 characters, using the same length rule as `FinancialAuditEntry`. The three handlers require `ICurrentActorContext` after request-shape validation and before the case lookup, the open-subject lookup, and replay classification. A missing or invalid actor, including an otherwise identical replay, returns HTTP 403 `reconciliation.actor.required` as `application/problem+json`. The body, query, and headers do not supply the actor. `HttpContextCurrentActorContext` is unchanged.

An identical replay still returns the stored result and does not append an entry, replace the original actor or time, or change the decision or `rowversion`. A later open of the same subject, after the previous case is closed, is a new case and gets its own `Opened` entry. A case that existed before this migration has no invented history. Closing that historical open case writes only the new terminal entry. Replaying a historical terminal case does not create the missing open entry.

`ReviewCase` gains alternate key `AK_ReviewCases_OrganizationId_Id`. The audit row references that key. Optional composite foreign keys reference an invoice, a bank transaction, or an allocation when that id is present. All of those relationships use `DeleteBehavior.Restrict`. `ReviewCase.AllocationId` still has no foreign key. Uniqueness is `(OrganizationId, ReviewCaseId, Action)`. A second unique index on `(OrganizationId, ReviewCaseId)` includes only `Resolved` and `Dismissed`, so one case cannot store both terminal actions and a historical case can store a terminal action without `Opened`. A violation of an audit index is not evidence that the open or the decision succeeded, and it is not classified as the open-subject conflict.

The table is append-only. Trigger `TR_ReviewCaseAuditEntries_RejectMutation` rejects `UPDATE` and `DELETE` with error 50002. The model uses `HasTrigger` and `UseSqlOutputClause(false)`. Migration `AddReviewCaseAuditEntries` creates the table, keys, checks, indexes, and trigger. Its `Down` drops that history. `Down` is not an automatic procedure for reversing a production deployment.

There is no endpoint and no UI that reads this audit. List and detail responses do not add the actor. ProductionCookie still cannot execute these three writes. The deployed financial flags, the allowlist, and `FinancialCommitGuard` stay as they are.

## Consequences

- A new review mutation and its audit entry commit or roll back together.
- Replay stays idempotent and keeps the original attribution.
- Historical rows stay without a synthesized `Opened` entry.
- Durability of this audit is visible in SQL Server and in tests. Operators cannot read it through the API yet.
- The three review routes remain outside the cookie write allowlist.
- Phase 8 stays planned. This decision is the write-side audit foundation, not the dispute workflow or the dashboard.

## Alternatives considered

### Extend `FinancialAuditEntry`

Rejected. That entry is a financial allocation record. Review open and dismiss do not have its required allocation, amount, and paired documents.

### A generic audit framework

Rejected. Three review actions need one table and one writer. A shared framework would widen the change without a second use.

### Store the actor on `ReviewCase`

Rejected. The aggregate would then have a mutable author, and a terminal action would overwrite or omit the opening actor. The history belongs in the append-only entry.

### Backfill historical cases

Rejected. The actor and the decision time of a past action are not stored. Inventing them would present a false history. A later real close writes only that new event.
