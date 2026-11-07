# ADR 0036: Allocation dispute resolution

- Status: Accepted
- Date: 2026-11-07

## Context

ADR 0032 stores an allocation dispute as a registration and leaves the challenge undecided. ADR 0033 reads that registration and does not return a resolution. ADR 0032 did not add a reject-update trigger on `AllocationDisputes`, because a later increment might record the outcome on the same row. This increment records the outcome. It does not move money, edit the registration, or connect the dispute to `ReviewCase`.

## Decision

`AllocationDisputeResolution` is a separate row in Reconciliation. One dispute has at most one terminal decision. The stored outcome is `upheld` or `not_upheld`. The absence of a row means no decision. There is no third stored outcome. The reason uses `string.Trim()` and then must contain 1 to 500 UTF-16 code units. U+FEFF is preserved. SQL checks the reason with `DATALENGTH` and `LTRIM`/`RTRIM`, which remove only U+0020.

The decision stores its own server actor, server time, and idempotency key. `developmentJwt` is the trimmed JWT `sub`. `productionCookie` is `ApplicationUser.Id`. The same text from those sources is not the same person. The registration row keeps its original statement, actor, time, and key. This operation does not add a reject-update trigger to `AllocationDisputes`.

The resolution row is the audit of the decision. The operation does not write `FinancialAuditEntry` and does not copy the registration. `TR_AllocationDisputeResolutions_RejectMutation` rejects `UPDATE` and `DELETE` with SQL error 50004. EF is configured with `HasTrigger` and `UseSqlOutputClause(false)`.

The primary key is `(OrganizationId, AllocationDisputeId)`. `AK_AllocationDisputes_OrganizationId_Id` is added so the foreign key can include `OrganizationId`. The foreign key is `FK_AllocationDisputeResolutions_AllocationDisputes_OrganizationId_AllocationDisputeId` with `DeleteBehavior.Restrict`. `UX_AllocationDisputeResolutions_OrganizationId_IdempotencyKey` is unique per organization among resolutions only.

`POST /api/allocation-disputes/{disputeId}/resolution` accepts only `outcome` and `reason`. `Idempotency-Key` is required and uses the same canonical `D` rule as registration. A new decision returns HTTP 201 with no `Location` header. A matching replay returns HTTP 200 and the original decision, including its actor and time, including when another valid actor of the same organization retries, and including after reversal. The response does not include the organization id or the idempotency key.

`GET /api/allocation-disputes/{disputeId}/resolution` is a separate contract. An existing dispute with no decision returns HTTP 200 and `decision: null`. A resolved dispute returns the decision object. An unknown dispute and a dispute owned by another organization both return HTTP 404 `reconciliation.allocation_dispute.not_found`. The six-field registration response stays unchanged.

The actor is required before the first key read, including replay. A missing actor returns HTTP 403 `reconciliation.actor.required`. Each key and dispute read is its own committed observation. The handler does not take a shared snapshot and does not loop. A key found on the first read is classified before the route dispute is treated as missing: the same dispute, outcome, and ordinal reason return HTTP 200; a difference returns HTTP 409 `reconciliation.allocation_dispute.resolution.idempotency_payload_conflict`.

When the dispute already has a decision under a different key, the handler reads the requested key once more before HTTP 409 `reconciliation.allocation_dispute.resolution.already_resolved`. A key found by that read is classified the same way. The same rule runs before `INSERT` and during recovery. Recovery runs only after the losing transaction is rolled back and ended, on a fresh context. It reads the requested key first, then the dispute decision, and repeats the one key re-read before `already_resolved`. No winner returns HTTP 409 `reconciliation.allocation_dispute.resolution.concurrency_conflict`.

SQL Server 2601 or 2627 enters that recovery only when the message names `PK_AllocationDisputeResolutions` or `UX_AllocationDisputeResolutions_OrganizationId_IdempotencyKey`. Both names use the same classification. Another index is not replay. A foreign-key failure enters recovery only for SQL 547 whose message names `FK_AllocationDisputeResolutions_AllocationDisputes_OrganizationId_AllocationDisputeId`. A still-missing dispute is HTTP 404. A dispute that still exists is `concurrency_conflict`. Another 547, including a check constraint, is not that path.

The insert does not take an allocation lock and does not read or update the allocation. A reversed allocation can still be judged. The decision and a reversal do not coordinate through an allocation lock; this decision does not claim that every SQL lock is disjoint.

The write permission is `allocation-disputes.resolve`, `FinancialWriteOperation.ResolveAllocationDispute` value 15, granted only to `Operator`. The read permission is `allocation-disputes.resolution.read`, also only `Operator`. Neither permission is in `ProductionFinancialWriteActivation` or session contract v2. `OrderedReads` stays unchanged. `DevelopmentJwt` can call the classified routes without reading Access roles. GET does not require `sub`. POST requires an actor. `ProductionCookie` denies the POST when both financial flags are enabled.

The new migration adds the alternate key, the table, and the trigger. It does not backfill decisions. `Down` drops the trigger, the table, and the alternate key. It is not a production rollback.

## Consequences

- A dispute can be resolved after reversal without confirming the current financial state.
- The registration contract used by Phase 8P and Phase 8Q stays six fields.
- The decision cannot be edited or reopened in this increment.
- The resolution screen, settlement correction, delegation, and the operational dashboard stay later.
- Phase 8 stays planned.

## Alternatives considered

### Outcome columns on AllocationDispute

Rejected. ADR 0032 allowed a later update of the registration row, but protecting the original statement, actor, time, and key would need a selective trigger. A separate insert keeps that row untouched, gives the decision its own key and attribution, and makes the row itself the audit.
