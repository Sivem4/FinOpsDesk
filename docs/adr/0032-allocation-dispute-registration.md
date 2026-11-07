# ADR 0032: Allocation dispute registration

- Status: Accepted
- Date: 2026-11-06

## Context

An operator can receive a counterparty challenge outside FinOpsDesk. ReviewCase records a manual matching exception. A comment records a note on that case. Neither record is a challenge against one confirmed allocation, and resolving a case does not change invoice or bank balances. The first dispute increment needs a durable registration without deciding the challenge or moving money.

## Decision

`AllocationDispute` belongs to Reconciliation. The row stores `Id`, `OrganizationId`, `AllocationId`, `Statement`, `ActorSource`, `ActorId`, `RecordedAt`, and `IdempotencyKey`. It has no outcome, no `rowversion`, and no `ReviewCaseId`. One allocation can have many disputes. The same statement can be stored again when the operator sends a new key. There is no uniqueness constraint on the allocation itself.

`POST /api/allocations/{allocationId}/disputes` accepts only `statement`. Normalization is `string.Trim()`. After that, the length must be 1 to 500 UTF-16 code units. U+FEFF is preserved. A JSON object that omits `statement` returns `reconciliation.allocation_dispute.statement_required`, as does text that is empty after trim. A duplicate `statement`, another property, a non-string value, or a string or property name that cannot be decoded as Unicode returns `reconciliation.allocation_dispute.invalid_request`. A well-formed surrogate pair is stored. `Idempotency-Key` is required and must be one non-empty UUID in `D` format with no surrounding `Char.IsWhiteSpace`, because `Guid.TryParseExact` trims those characters. After that parse succeeds, the raw header must equal `Guid.ToString("D")` under `StringComparison.OrdinalIgnoreCase`, so a `+` or `0x` alias of the same value is rejected. The organization and the actor come from the server. The actor is checked before the first key read, including replay. A missing actor returns the existing HTTP 403 `reconciliation.actor.required`.

The handler stores `ReviewCaseActorSource` and the actor id with the server time. `developmentJwt` is the trimmed JWT `sub`. `productionCookie` is `ApplicationUser.Id`. The same text from those sources is not the same person. A new dispute returns HTTP 201 with `disputeId`, `allocationId`, `statement`, `actorSource`, `actorId`, and `recordedAt`, and with no `Location` header. A matching replay returns HTTP 200 and that original confirmation, including when another valid actor of the same organization retries and including after the allocation has been reversed.

The key is unique per organization on `UX_AllocationDisputes_OrganizationId_IdempotencyKey`. The first read is a short committed statement, outside the allocation transaction. An existing row is compared by allocation id and normalized statement with `StringComparison.Ordinal`. A difference returns HTTP 409 `reconciliation.allocation_dispute.idempotency_payload_conflict`, including when the route names another allocation or an unknown id. That classification wins over not-found and not-confirmed.

A missing key starts a SQL Server transaction and reads `Allocations.Status` for the server organization and route id with `UPDLOCK, ROWLOCK`. The lock stays until that transaction commits or rolls back. When the status is `Confirmed`, the handler inserts only the dispute and calls `SaveChangesAsync` in the same transaction. It does not update the allocation, so its status, amounts, keys, and `rowversion` stay unchanged. Invoice and bank balances and existing `FinancialAuditEntry` rows stay unchanged. The registration does not write a financial audit row.

When the allocation is missing or is not `Confirmed`, the handler rolls the transaction back and ends it before returning the refusal. It then reads the key again on a new context. A row found there is classified as a replay or a payload conflict. Only a still-missing key returns HTTP 404 `reconciliation.allocation.not_found` or HTTP 409 `reconciliation.allocation_dispute.allocation_not_confirmed`.

A SQL Server 2601 or 2627 is replay only when the message names `UX_AllocationDisputes_OrganizationId_IdempotencyKey`. The handler rolls that transaction back and ends it before reading the winner on a new context. A match returns HTTP 200. A different payload returns the payload conflict. A missing winner returns HTTP 409 `reconciliation.allocation_dispute.concurrency_conflict`. The failed context is not saved again. Another index, the primary key, or the foreign key is not treated as replay.

Rollback proves only that this attempt did not commit. Another request may already have stored the key. Losing the response around commit leaves the result uncertain. The client repeats the original key and body. A timeout by itself does not prove that the row is absent.

The foreign key is `(OrganizationId, AllocationId)` to `AK_Allocations_OrganizationId_Id`, with `DeleteBehavior.Restrict`. SQL checks reject an empty idempotency key, an actor source other than the two stored values, a blank or over-long actor id, and a statement whose `DATALENGTH` is outside 2 to 1000 after `LTRIM` and `RTRIM`. Those SQL functions remove only U+0020. There is no reject-update trigger, because a later increment may record the outcome on the same row.

The route stays under `TenantMember`. The permission is `allocation-disputes.register`, granted only to `Operator`, and `FinancialWriteOperation.RegisterAllocationDispute` is 14. The permission is in the write catalog, so `DevelopmentJwt` can post without reading Access roles. It is not in `ProductionFinancialWriteActivation`. `ProductionCookie` denies the POST when both financial flags are enabled. The three-operation allowlist, `FinancialCommitGuard`, and session contract v2 stay unchanged. `DevelopmentJwt` does not enter the commit guard.

## Consequences

- A dispute can exist without a `ReviewCase`.
- Registration does not decide the challenge and does not move money.
- Reversal can commit while a registration is still deciding, and the second key read distinguishes a stored key from a reversed allocation that has none.
- Down drops `AllocationDisputes` and therefore deletes stored disputes. It is not a production rollback.
- Dispute read, list, UI, resolution, settlement correction, delegation, and the operational dashboard stay outside this increment.
- Phase 8 stays planned.

## Alternatives considered

### A separate Disputes module

Rejected. The confirmed check and the insert must share one SQL Server transaction and the allocation lock. A second module would split that operation without a new business capability.

### One dispute per allocation

Rejected. The operator can receive the same challenge again and must be able to record it with a new key. The idempotency key prevents a lost response from becoming a second row. It does not limit the allocation to one challenge.

### An outcome column and an update trigger

Rejected for this increment. Resolution is a later operation. A trigger that rejects every update would block that operation.
