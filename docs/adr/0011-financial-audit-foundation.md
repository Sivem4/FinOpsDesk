# ADR 0011: Financial audit foundation

- Status: Accepted
- Date: 2026-10-28
- Follow-up: [ADR 0012](0012-allocation-reversal.md) adds the reversal action and its read mapping. The confirmation-audit decision below remains the foundation. [ADR 0025](0025-review-case-audit.md) adds a separate review-case audit entry. It does not extend `FinancialAuditEntry`.

## Context

Exact-match confirmation, explicit-amount allocation, and operator-approved batch exact matching already change invoice balances, bank-transaction balances, and allocations. Those commits need a durable record of who confirmed the amount and which financial operation produced it. A later reversal will need to sit beside that record rather than erase it.

The authenticated organization already comes from the trusted `org_id` claim. The acting user is a separate fact. `TenantMember` checks the organization claim and does not identify the person. Review cases and batch receipts were intentionally actorless when they were introduced. ADR 0025 later attributes a newly executed review open, resolve, or dismiss without adding those facts to `FinancialAuditEntry`.

`MatchSuggestion` is still an unmapped domain type. Confirming an allocation is not a suggestion state transition. Persisting suggestions remains a separate, deferred decision.

This record is not a general event log, an accounting ledger, or a compliance certification.

## Decision

Financial confirmation writes append one `FinancialAuditEntry` in the same SQL Server transaction as the allocation and the two balance updates. The entry is derived from the confirmed `Allocation`. The application does not reconstruct an audit row from `Allocation.ConfirmedAt`, and it does not backfill allocations that were committed before this table existed.

### Entry

`FinancialAuditEntry` lives in the Reconciliation module and in the `reconciliation` schema. This decision implements `Confirmation` (1). `AllocationIdempotencyOperation` on a confirmation is `ExactMatch` (1), `ExplicitAmount` (2), or `BatchExactMatch` (3). The action says what happened to the allocation. The operation says which confirmation command produced it. ADR 0012 adds `Reversal` (2) and audit operation 4.

The factory copies organization, allocation, invoice, bank transaction, amount, currency, `RecordedAt`, idempotency operation, and idempotency key from the allocation. `RecordedAt` is `Allocation.ConfirmedAt`. `BatchExecutionId` is required for `BatchExactMatch` and must be absent for the other two operations, because an allocation does not store the batch receipt id. The actor is a separate argument. For this decision it is the trimmed JWT `sub` claim, from 1 to 256 characters, and it is not accepted from the body, the query string, or a custom header. Phase 7 later records `ApplicationUser.Id` for `ProductionCookie`. See ADR 0019.

A confirmation audit can be created only for an allocation that is currently `Confirmed`.

### Actor

`ICurrentActorContext` is the application port. The API host implements it with `HttpContextCurrentActorContext`. `MapInboundClaims` stays disabled, so the claim type remains `sub`. Exact confirmation, explicit-amount confirmation, and batch execution validate the actor before idempotency lookup and before financial writes. A missing, blank, or overlong actor returns `reconciliation.actor.required` and HTTP 403. A replay also requires a valid actor. Replay does not replace the actor stored on the original entry.

Batch execution reads the actor once on the outer request and passes that value into the item executor. The item scope does not read `HttpContext`. When a later request resumes a `Running` receipt, newly committed items use that later actor. Entries that were already committed keep the actor of the request that committed them. `BatchExecution` does not gain an `InitiatedBy` or `ExecutedBy` column.

### Tenant consistency

Organization id on the audit row comes from the allocation, which came from `ICurrentOrganizationContext`. The foreign keys are composite `(OrganizationId, Id)` references to `Allocation`, `Invoice`, `BankTransaction`, and, when present, `BatchExecution`. Delete behavior is `Restrict`. Those keys stop a row from pointing at another organization's allocation, invoice, bank transaction, or receipt.

They do not prove that the invoice and bank transaction on the audit row are the same pair as the allocation. A same-organization raw insert can name a different invoice that belongs to that organization. The application factory copies the pair from the allocation. The database constraint is tenant consistency, not pair equality.

`ActorId` is checked with `DATALENGTH`, because SQL `LEN` and ordinary equality do not reject trailing spaces. The check allows 1 to 256 UTF-16 code units and rejects leading or trailing `U+0020`. It does not reject every .NET whitespace character. The domain factory still rejects those.

The unique index `UX_FinancialAuditEntries_OrganizationId_AllocationId_Action` allows one confirmation row per allocation in an organization. A unique violation on that index is not treated as proof that the allocation commit succeeded. Callers distinguish it from `UX_Allocations_OrganizationId_IdempotencyKey`.

### Writes

`IFinancialAuditWriter.AppendConfirmation` only adds the entry to the scoped `ReconciliationDbContext`. It does not call `SaveChanges` and it does not open a transaction.

`ConfirmExactMatchHandler` and `ConfirmExplicitAmountHandler` validate the actor, apply the existing financial rules, add the allocation, append the audit entry with a null batch id, and call the existing `SaveChanges`. One failed statement rolls the financial changes and the audit insert back together.

Batch execution keeps one transaction per item. The item executor appends the audit entry with the persisted `BatchExecution.Id` and commits only after that item's `SaveChanges`. A failure in one item does not roll back an earlier item. A completed receipt stays immutable. Recovery of a committed item, including a historical allocation that has no audit row, does not append a confirmation.

### Read API

`GET /api/allocations/{allocationId}/audit` is a query. It uses `TenantMember` and `ICurrentOrganizationContext`. It does not require `sub`. Under the current authorization model, any tenant member can read `ActorId`. Finer permission to see the actor is future work, not a second policy invented here.

The query checks that the allocation belongs to the authenticated organization and then reads that organization's audit rows for that allocation. Both checks are parameterized EF Core predicates. An unknown allocation and another organization's allocation both return HTTP 404. The response does not reveal that the other allocation exists. A historical allocation with no audit row returns HTTP 200, page 1, page size 50, `totalCount` 0, and an empty `items` array. The read does not synthesize an actor or a confirmation time.

Page size is 1 through 100. Ordering is `RecordedAt` ascending, then audit entry id ascending. The current model can store only one confirmation per allocation, so a single allocation cannot legally supply two confirmation rows to prove that order. The query still orders by both columns so a later reversal row can sort after the confirmation without a contract change. The response action for a confirmation is the string `confirmation`. ADR 0012 adds `reversal`. A confirmation operation is `exactMatch`, `explicitAmount`, or `batchExactMatch`. The idempotency key stays in the table and is not part of this response. `BatchExecutionId` is null except for batch confirmation.

### Append-only trigger

`TR_FinancialAuditEntries_RejectMutation` rejects `UPDATE` and `DELETE` on `FinancialAuditEntries`. EF Core is configured with `UseSqlOutputClause(false)` because the trigger is present. The trigger is ordinary database DDL. A principal that can alter or disable the trigger, or that can write through an account the trigger does not cover, can change history. The trigger is not a tamper-proof control and it is not a regulator-certified audit facility.

### What this is not

This is not event sourcing. The allocation and the two balances remain the current financial state. The audit table is an append-only history of the financial writes that the application integrated explicitly. It is not a complete ledger of every balance change, import, review decision, or future command.

Existing confirmation paths append an audit record. A new financial write path does not become audited by sharing the table. It needs its own call to the writer, the same transaction, tests, and review. ADR 0012 is that path for reversal. It appends a reversal action without deleting the confirmation and without backfilling a confirmation that was never written. This decision does not change the confirmation writers.

## Alternatives considered

### Reconstructing history from `Allocation.ConfirmedAt`

Rejected. An allocation does not store the actor or, for batch confirmation, the receipt id. Inventing either value would present a guess as a financial record.

### One audit transaction for a whole batch

Rejected. ADR 0010 keeps item commits independent so an earlier allocation survives a later item failure. The audit entry belongs in the item transaction, not in a second transaction after it.

### Event sourcing or an outbox

Rejected. No consumer needs a broker event for this history. The current balances are still stored on the invoice and the bank transaction.

### Hiding `ActorId` from every tenant member

Rejected for this phase. The only approved role is `TenantMember`. A separate audit-reader role would be a new authorization policy. Phase 7 owns finer roles.

## Consequences

Confirmation of an exact pair, an explicit amount, or a new batch exact-match item now has an actor and a durable confirmation record. Historical allocations remain valid and simply have an empty audit page. Idempotent replay does not duplicate that record.

The read contract returns `confirmation` and, under ADR 0012, `reversal`. Database immutability stops ordinary `UPDATE` and `DELETE`. It does not certify the history. Composite foreign keys stop cross-tenant references. They do not prove that the invoice and bank transaction are the allocation's own pair.

`MatchSuggestion` stays unpersisted. Allocation reversal and its concurrency token are specified in ADR 0012. Audit for financial commands other than confirmation and reversal remains open.
