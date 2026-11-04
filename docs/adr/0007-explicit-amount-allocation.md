# ADR 0007: Explicit amount allocation

- Status: Accepted
- Date: 2026-10-26
- Follow-up: [ADR 0010](0010-batch-auto-matching.md) adds batch execution, [ADR 0011](0011-financial-audit-foundation.md) adds confirmation audit, and [ADR 0012](0012-allocation-reversal.md) adds persisted reversal. The explicit-amount decision below remains in force.

## Context

ADR 0006 keeps `POST /api/matches/confirm` as a full open-amount exact match. Operators also need to allocate part of an open balance, apply several payments to one invoice, and apply one payment across several invoices. Those are separate financial operations. They are not a second mode of the exact-match route.

`ConfirmAllocationHandler` can already apply a caller-supplied amount, and it does not store an idempotency key. Phase 4C stores one key on `Allocation` under the filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey`. One key cannot be copied onto many allocation rows. A batch receipt would need its own identity and is outside this decision.

## Decision

`POST /api/allocations` confirms one allocation for the authenticated organization:

- one invoice;
- one bank transaction;
- one explicit positive amount and currency.

The JSON body contains only `invoiceId`, `bankTransactionId`, and `amount` with `amount` and `currency`. Unknown properties are rejected. The amount must be a JSON number. A string is rejected. `JsonElement.GetDecimal()` can drop nonzero digits that do not fit in a .NET `decimal`. The endpoint compares the original numeric token with the parsed decimal and rejects the request when those values differ. `ExplicitAmountValidation` then requires the value to be greater than zero, below 10^15, and equal to its `decimal(19,4)` rounding. Trailing zeros such as `400.00000` stay valid. Exact exponent notation such as `4e2` and `1e-5` is accepted only when the value is exact; `1e-5` is then rejected by the scale rule. The request body is limited to 4096 bytes.

The invoice number and bank reference do not have to match, and the bank reference may be missing. The currency must match both aggregates. The amount must not exceed the invoice outstanding amount or the bank transaction unapplied amount. Cross-currency allocation is outside this decision.

Each request is one database transaction: both balance updates and the allocation insert commit in one `SaveChangesAsync`, or the request writes nothing. A validation failure runs `CanAllocate` on both aggregates before either `Allocate`, so the tracked entities stay unchanged. Several requests in sequence are not one atomic batch. One invoice can receive many payments. One payment can cover many invoices. The same pair can receive another allocation under a different idempotency key. A later request that exceeds the remaining balance leaves the earlier commits in place. There is no compensation and no automatic reversal.

The required `Idempotency-Key` header is one UUID in `D` format. `OrganizationId` comes only from `ICurrentOrganizationContext`. The key is stored on the allocation with `IdempotencyOperation` `ExplicitAmount` (2). Exact-match confirmation stores `ExactMatch` (1). The fingerprint for an explicit-amount replay is the operation, the pair, the amount, and the currency. An exact-match replay compares the operation and the pair; the amount was derived by the server. The same key with a different operation, pair, amount, or currency returns `reconciliation.confirmation.idempotency_payload_conflict`. A replay returns the original allocation id and does not require the original open balance to remain available. Keys are unique per organization, so the same key in another organization is a different operation.

Migration `20261026155535_AddAllocationIdempotencyOperation` adds the nullable operation column, backfills existing keyed Phase 4C rows to `ExactMatch`, and adds `CK_Allocations_IdempotencyOperation`. A row is valid only when the key and the operation are both null, or the key is present and the operation is 1 or 2. Keyless rows, including those created by `ConfirmAllocationHandler`, stay outside the filtered unique index. The Phase 4C migration is not changed. The filtered unique index remains the authority for a repeated key.

`Invoice` and `BankTransaction` keep their SQL Server `rowversion` tokens. This decision does not give `Allocation` one. ADR 0012 later adds that token for persisted reversal. A stale version or a unique-key conflict fails the whole `SaveChanges`. The handler reads the winner through `IAllocationIdempotencyLookup` on a new context and does not save the failed context again. A matching fingerprint becomes a replay. A different fingerprint becomes a payload conflict. If that fresh read does not find the key, the result is `reconciliation.concurrency_conflict`.

The same recovery runs when a balance check fails after the first lookup missed. That covers a winner that committed the same aggregates before this request finished validation. There is a remaining window after the second lookup: the winner may still be uncommitted, and this request can return the balance or concurrency error without storing the key. A client retry with the original key and payload then takes the lookup path and returns the original allocation id. No lock and no automatic financial retry are added.

The route uses the existing `TenantMember` policy. Any member of the organization can call it. A foreign invoice or bank transaction is not found, and the response does not include the other tenant's amounts, references, or organization id. A client-supplied organization id is not accepted. The endpoint does not record the acting user.

This decision does not add `POST /api/allocations/batch`, automatic batch reconciliation, persisted reversal, or an immutable audit record. Those later arrived in ADR 0010, ADR 0011, and ADR 0012. Finer authorization remained Phase 7 at this decision and followed there. See ADR 0019. Deployed financial flags stay unset. `ConfirmAllocationHandler` stays unregistered for HTTP. `POST /api/matches/confirm` is unchanged.

## Alternatives considered

### One atomic batch request

Rejected for this phase. A correctly implemented atomic batch could commit every financial change and one idempotency receipt in a single SQL transaction. A failure before that commit would leave no partial batch, so compensation would not be required. The additional complexity is cumulative balance validation, coordinating multiple aggregates, and storing one idempotency identity for multiple allocations. The current key is unique on each allocation row and cannot name many rows. Callers send one atomic request at a time.

### Accepting the rounded .NET decimal

Rejected. A JSON literal such as `1.0000000000000000000000000000000000000001` becomes `1` in `decimal`, and the scale check would then accept it. The original token has to match the parsed value.

### A separate idempotency table for explicit amounts

Rejected for the same reason as ADR 0006. The allocation row stores the operation and the fingerprint. The existing unique index remains the authority.

## Consequences

Positive:

- partial and full remaining amounts use one route, including when references differ;
- one invoice can be settled by several payments, and one payment can settle several invoices, as separate commits;
- a lost response retries to the original allocation;
- an inexact JSON number cannot be stored under a rounded amount;
- Phase 4C rows remain valid after the operation backfill.

Negative:

- separate requests are not all-or-nothing as a group;
- the second idempotency lookup can still miss a commit that has not finished, so the first response may be a balance or concurrency conflict and the retry is the replay;
- two explicit amounts that share an invoice or bank transaction can still lose to `rowversion` even when both amounts would fit;
- `AllocatedAmount` is still not constrained to equal the sum of allocation rows;
- the acting user is not written to an audit log;
- any tenant member can allocate.
