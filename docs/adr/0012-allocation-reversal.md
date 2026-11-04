# ADR 0012: Allocation reversal

- Status: Accepted
- Date: 2026-10-29
- Amends: [ADR 0003](0003-optimistic-concurrency-for-reconciliation.md) for the `Allocation` concurrency token
- Extends: [ADR 0010](0010-batch-auto-matching.md) for batch recovery after reversal, and [ADR 0011](0011-financial-audit-foundation.md) for the reversal audit action

## Context

Phases 4C, 4D, and 4G confirm allocations. Phase 5B records those confirmations in an append-only `FinancialAuditEntry`. A confirmed allocation must be reversible without deleting the row or the confirmation audit.

ADR 0003 deferred an `Allocation` `rowversion` until a persisted update needed it. Reversal is that update. Invoice and bank-transaction tokens do not detect a stale allocation row.

ADR 0010 freezes a Completed batch receipt. It did not say how a Running receipt should treat an allocation that was confirmed by the batch and later reversed. The approved policy is historical confirmation, called Option A below.

This record is not a general ledger and it is not event sourcing.

## Decision

### Reversal command

`POST /api/allocations/{allocationId}/reverse` reverses one allocation for its full original `Amount`. Partial reversal is not supported. The route requires `TenantMember`.

Organization id comes only from the authenticated organization claim. Actor id comes only from the JWT `sub` claim. A missing, blank, whitespace-only, or overlong `sub` returns HTTP 403 `reconciliation.actor.required` before an idempotency lookup and before any financial write. That was the `DevelopmentJwt` source for this decision. `ProductionCookie` later uses `AccessSessionGate` and `ApplicationUser.Id`. See ADR 0019.

The JSON body accepts only `reason`. The server trims it. An empty reason returns HTTP 400 `reconciliation.allocation.reversal_invalid_request`. A reason longer than 500 UTF-16 code units returns HTTP 400 `reconciliation.allocation.reversal_reason_too_long` and is not truncated. `ReversedAt` is the server clock. The client does not send an amount, a timestamp, an actor, or an organization.

The `Idempotency-Key` header is one UUID in `D` format. A missing header returns HTTP 400 `reconciliation.confirmation.idempotency_key_required`. A header that is not a UUID returns HTTP 400 `reconciliation.confirmation.idempotency_key_invalid`. Those codes are shared with confirmation. The reversal key is still a separate namespace.

A missing allocation and an allocation owned by another organization both return HTTP 404 `reconciliation.allocation.not_found`. The lookup is organization-scoped. A reversal key is not read across tenants.

`AllocationReversal.Apply` validates the allocation, invoice, and bank transaction before it changes any of them. The allocation row remains. Status changes from Confirmed to Reversed. Invoice allocated amount and bank allocated amount decrease by the full original amount. `Amount`, `ConfirmedAt`, `IdempotencyKey`, and `IdempotencyOperation` stay unchanged.

The handler then stores the reversal key, appends one reversal audit entry, and calls `SaveChangesAsync` once. There is no second financial save. A failed save rolls back the allocation update, both balance updates, and the audit insert.

### Idempotency

Confirmation uses `IdempotencyKey` with `UX_Allocations_OrganizationId_IdempotencyKey`. Reversal uses nullable `ReversalIdempotencyKey` with `UX_Allocations_OrganizationId_ReversalIdempotencyKey`, filtered to non-null keys. The same UUID may be used once for a confirmation and once for a reversal. That is not an identity collision.

`CK_Allocations_IdempotencyOperation` still allows only confirmation operations 1, 2, and 3. Operation 4 is audit-only.

A fresh reversal returns HTTP 200 with `replayed` false. The same key and the same normalized reason for the same allocation return HTTP 200. Replay returns the original `reversedAt` and reason with `replayed=true`. The original reversal actor remains preserved in the immutable audit entry and is not part of the reversal response. It does not write another audit row.

The same reversal key with a different allocation id or a different normalized reason returns HTTP 409 `reconciliation.allocation.reversal_idempotency_payload_conflict`. A different key for an allocation that is already reversed returns HTTP 409 `reconciliation.allocation.already_reversed`.

Replay requires the persisted allocation and exactly one immutable reversal audit entry to agree on allocation id, invoice id, bank transaction id, amount, currency, action, operation, reversal key, reason, and `RecordedAt`. A key without that complete evidence, or evidence that disagrees, returns HTTP 409 `reconciliation.allocation.reversal_integrity_conflict`. The handler does not repair it and does not treat the key alone as success.

A `rowversion` conflict returns HTTP 409 `reconciliation.concurrency_conflict` and is not retried. A deadlock or another infrastructure failure has no new automatic retry policy. A caller that is unsure whether the first attempt committed retries the same key and the same payload.

### Concurrency

`Invoice`, `BankTransaction`, and `Allocation` each have a SQL Server `rowversion` shadow property. The allocation token exists so a stale reversal cannot overwrite a reversal that already committed, and so a reversal cannot commit against an allocation row that another writer changed.

The token does not replace domain validation. SQL Server rejects the stale write. Independent financial pairs can still commit concurrently. There is no distributed lock and no generic transaction coordinator.

### Audit

A reversal audit entry uses `FinancialAuditAction.Reversal` (2) and `AllocationIdempotencyOperation.Reversal` (4). It stores the original full amount and currency, the actor id, the normalized reason, and `RecordedAt` equal to `Allocation.ReversedAt`. `BatchExecutionId` is null.

Confirmation entries stay on operations 1, 2, and 3. Their `Reason` is null. A confirmation audit is not rewritten when the allocation is reversed. A historical allocation can have a reversal audit and no confirmation audit. Historical confirmation rows are not backfilled.

`CK_FinancialAuditEntries_Action` allows actions 1 and 2. `CK_FinancialAuditEntries_ActionOperation` allows operation 4 only with action 2. `CK_FinancialAuditEntries_Reason` requires a reversal reason whose trimmed length is between 1 and 500 characters, stored so `DATALENGTH` is between 2 and 1000. `CK_FinancialAuditEntries_BatchReference` requires a null batch id for operations 1, 2, and 4.

`TR_FinancialAuditEntries_RejectMutation` still rejects `UPDATE` and `DELETE`. `GET /api/allocations/{allocationId}/audit` can return both actions. The audit trail records financial events. `Allocation.Status` is the current state.

### Batch recovery

A `BatchExecution` receipt records the historical execution outcome of the batch command. A later reversal changes the allocation's current state and does not rewrite the earlier successful confirmation result. No item outcome named `ConfirmedThenReversed` is introduced.

For a Running receipt, recovery uses the original derived confirmation idempotency key. It may recover an allocation that is currently Reversed. The recovered item remains historically Confirmed. The original allocation id and confirmation amount remain. No new allocation and no new confirmation audit are created. Matching does not run again for that recovered item. The receipt may become Completed.

For a Completed receipt, `ResultPayload`, `ResultVersion`, and `CompletedAt` stay frozen. Replay returns that historical result. A later reversal does not alter it.

The confirmation actor, the reversal actor, and the recovery operator may be three different identities. Recovery does not overwrite either historical audit actor. A historical allocation that never had a confirmation audit is not backfilled with a fabricated confirmation.

Released balances can be allocated by a separate new confirmation with a new idempotency key, subject to the normal confirmation checks.

### Migration

`20261028221955_AddAllocationReversalPersistence` adds `Allocations.RowVersion`, nullable `Allocations.ReversalIdempotencyKey`, the filtered reversal index, `FinancialAuditEntries.Reason`, and the expanded audit checks. It does not truncate `Allocations.ReversalReason`, which remains `nvarchar(max)`.

Down throws SQL error 50002 when a reversal audit row (`Action` 2) or a non-null `ReversalIdempotencyKey` exists, and it does so before dropping those columns. Existing confirmation rows and historical allocations remain valid after Up. This migration does not change `20261028002321_AddFinancialAuditEntries`.

## Alternatives considered

### Rewriting a Completed batch receipt after reversal

Rejected. ADR 0010 treats the receipt as the historical outcome of that command. A later financial change is a new fact, recorded by the reversal audit and by `Allocation.Status`.

### A `ConfirmedThenReversed` batch outcome

Rejected. It would mix the current allocation state into a frozen execution document and would require a new result version.

### Treating a reversal key as proof of success without the audit row

Rejected. A unique-key conflict or a stored key can exist without a consistent reversal audit. Replay requires both.

### Automatic retry of `rowversion` conflicts or deadlocks

Rejected. ADR 0003 leaves retry to the caller. A deadlock is not classified as a business conflict.

## Consequences

Full reversal is available to any tenant member. Finer authorization remained Phase 7 at this decision and followed there. See ADR 0019. Deployed financial flags stay unset. Partial reversal, multi-allocation reversal, and `MatchSuggestion` lifecycle changes are not part of this decision.

Expected failures use the stable codes above and do not return SQL text. The append-only trigger still stops ordinary updates and deletes. It is not a tamper-proof control.
