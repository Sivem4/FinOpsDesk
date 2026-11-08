# ADR 0038: Allocation dispute correction

- Status: Accepted
- Date: 2026-11-08

## Context

ADR 0036 records an upheld or not-upheld decision and does not move money. A later settlement correction was still open. This increment adds one conscious full reversal for an upheld dispute and stores the link with that financial effect. The decision itself still does not change balances. An earlier independent reversal does not gain a dispute link afterwards.

## Decision

`AllocationDisputeCorrection` is a separate append-only row in Reconciliation. One dispute has at most one correction. The command requires an `upheld` decision. No decision returns HTTP 409 `reconciliation.allocation_dispute.correction.decision_required`. `not_upheld` returns HTTP 409 `reconciliation.allocation_dispute.correction.not_upheld`. Neither path calls `AllocationReversal.Apply`.

The correction reuses `AllocationReversal.Apply` for the full original amount. The same transaction writes the reversal idempotency key, one reversal audit, the invoice and bank balance changes, and the correction row. The correction UUID is also `Allocation.ReversalIdempotencyKey`. `UX_Allocations_OrganizationId_ReversalIdempotencyKey` remains the financial key space. The correction table does not create a second one. `ReverseAllocationHandler` is unchanged: after a correction, the same key, allocation, and reason replay as a financial reversal and do not read the dispute.

The owner of the explicit transaction commits it and rolls it back on every refusal, recognized persistence conflict, other exception, cancellation, and commit failure. `SaveChangesAsync` does not commit that transaction. In `DevelopmentJwt`, `RequiresCommitAuthorization` is false, so `SaveChangesAsync` does not roll the explicit transaction back either. An EF savepoint rollback is not the end of the transaction and does not release the allocation `UPDLOCK`. Recovery reads a fresh context only after `RollbackAsync` returns. A failed rollback does not classify the command. The handler does not call `Apply` again. A unique violation enters that recovery only when SQL 2601 or 2627 names `UX_Allocations_OrganizationId_ReversalIdempotencyKey`, `UX_AllocationDisputeCorrections_OrganizationId_IdempotencyKey`, or `PK_AllocationDisputeCorrections`. Any other unique conflict ends the transaction and propagates. `rowversion` and the two named correction foreign keys keep their own paths.

Classification does not reuse an earlier negative read. A correction found by dispute id with the caller's key uses the same payload and evidence check as a correction found by key. A different key for that dispute is `reconciliation.allocation_dispute.correction.already_corrected`. A later `Reversed` status reads the key, the reversal key, and the dispute correction once more before `reconciliation.allocation.already_reversed`. The caller's key with a different payload stays `reconciliation.allocation_dispute.correction.idempotency_payload_conflict`. There is no write loop.

Replay evidence requires the correction row, the reversed allocation, and exactly one reversal audit to agree on organization, dispute, allocation, invoice, bank transaction, key, ordinal reason, amount, currency, time, and `FinancialAuditEntry.ActorId`. `ActorSource` stays on the correction row. It is not added to the financial audit. The actor retrying the command is not compared with the stored actor and does not replace it. A missing or contradictory audit or actor is `reconciliation.allocation_dispute.correction.integrity_conflict`. The handler does not repair history and does not move balances again.

`AK_AllocationDisputes_OrganizationId_Id_AllocationId` is unique because `(OrganizationId, Id)` is already unique. The correction foreign key `(OrganizationId, AllocationDisputeId, AllocationId)` references that key, so the stored allocation is the dispute's allocation. A second foreign key references the allocation `(OrganizationId, Id)`. Both use `DeleteBehavior.Restrict`. A foreign key to the dispute alone does not prove the allocation id. `TR_AllocationDisputeCorrections_RejectMutation` rejects `UPDATE` and `DELETE` with SQL error 50005.

`POST /api/allocation-disputes/{disputeId}/correction` accepts only `reason`, normalized with `string.Trim()` to 1–500 UTF-16 code units. `Idempotency-Key` uses the canonical `D` rule. A new commit and a replay both return HTTP 200. `replayed` distinguishes them. `amount` is `MoneyDto` and a JSON number. The body names the dispute and the allocation. `GET /api/allocation-disputes/{disputeId}/correction` is a separate read. `correction: null` means this dispute has no correction. Unknown and foreign disputes both return HTTP 404 `reconciliation.allocation_dispute.not_found`.

The financial permission is `allocations.reverse`. The operation is `FinancialWriteOperation.CorrectAllocationDispute`, value 16. It is not in `ProductionFinancialWriteActivation`. Value 6 stays on the existing reversal route. GET uses `allocation-disputes.resolution.read`. Roles, flags, the three-operation cookie allowlist, and session contract v2 stay unchanged. `DevelopmentJwt` still does not read Access roles. Phase 8 stays Planned.

The migration adds the alternate key, the table, the foreign keys, and the trigger. It does not backfill. `Down` drops the trigger, the table, and the alternate key. It is not a production rollback.

## Consequences

- A second dispute of an allocation that was fully reversed by the first correction does not reverse the amount again and does not receive a link.
- A plain reversal can still replay the financial key after a correction. That replay is not a dispute correction.
- The correction screen, its descriptor, and the financial cache refresh are a later increment. This ADR does not add them.
- Phase 8 remains Planned.
