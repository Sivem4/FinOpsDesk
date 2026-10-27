# ADR 0010: Operator-approved batch exact matching

- Status: Accepted
- Date: 2026-10-27

## Context

Operators can already confirm one exact pair or one explicit amount. Repeating those calls is not a batch. A book of unique `exact-1` pairs still needs one approved request that reports a mixed result and can be retried without allocating twice.

`smart-1` remains an explanation of possible matches. Its score is a point total, not a decision. A suggestion identifier is not an allocation instruction. ADR 0008 keeps confirmation on an explicit route. ADR 0009 keeps review cases as a manual workflow and leaves batch confirmation for this phase.

The open book, stored-money checks, and 5,000-row cap already exist. Review cases can name an invoice, a bank transaction, or both. Allocation confirmation already knows how to apply one positive amount without leaving a one-sided balance update.

## Decision

Batch matching is two synchronous HTTP operations on the existing `TenantMember` route group. There is no background worker and no unattended execution.

`GET /api/matches/batch/preview` is read-only. It classifies the complete current open book with `exact-1`, then applies open review blocks, then pages the eligible pairs. Page defaults to 1. Page size defaults to 50 and cannot exceed 100. Pagination does not change which pairs are eligible. Currency totals describe the full eligible set. The response includes the algorithm version and whether the input was complete. A truncated book is not returned as a complete classification. The preview is not persisted. It does not create a `BatchExecution`, an allocation, or a review case, and it does not authorize a later execution.

`POST /api/matches/batch/execute` is operator-approved. The caller submits 1 to 25 distinct pairs and one `Idempotency-Key` UUID in `D` format. The JSON object accepts only `pairs`. Each item accepts only `invoiceId` and `bankTransactionId`. The body is at most 8192 bytes and is rejected before parsing when it is larger. The client does not send an organization, amount, currency, reference, score, allocation id, actor, or fingerprint. Submitting those pairs is approval to attempt confirmation. An earlier preview is not that approval. The application revalidates the current rows.

Only a unique `exact-1` pair whose open amounts are equal and positive can be confirmed. The confirmed amount is the full current remainder on both sides, including a remainder left by an earlier partial allocation. Ambiguous references, coverage-limited probes, open review blocks, stale balances, and missing rows are item outcomes. They are not allocations. `smart-1` scores and `MatchSuggestion` identifiers cannot start this command.

### Request identity

The application sorts the pairs by invoice id and then bank transaction id. That order and the pair set are the canonical request. The receipt stores a fingerprint of that canonical form. The same key and the same pairs replay the stored result, including when the client reorders the pairs. The same key with a different pair set returns `reconciliation.batch.idempotency_payload_conflict`. The client does not choose the fingerprint.

### Receipt

`BatchExecution` is the receipt for one organization and one idempotency key. It is inserted as `Running` before the first item allocation. A `Completed` receipt stores result document version 1: requested, confirmed, rejected, and interrupted counts, `allConfirmed`, `hasRejections`, `partial`, and the canonical items. A confirmed item records the allocation id, amount, and currency. A rejected or interrupted item does not. Completing a receipt checks that the result's pairs still match the stored fingerprint. A completed result is frozen. Later changes to balances or review cases do not rewrite it, and a replay does not rename an outcome to `replayed`.

`allConfirmed` is true only when every item is confirmed. A batch whose items are all rejected is still a completed attempt. `partial` is true when at least one item is confirmed and at least one is rejected or interrupted. HTTP 200 is the success status for every completed attempt. The route does not use 201 or 207.

### Ownership and item transactions

One SQL connection takes `UPDLOCK, ROWLOCK` on the receipt and holds it through completion. A second request that finds the row locked returns `reconciliation.batch.execution_in_progress`. The lock and the SQL session id are not part of the HTTP response. The receipt insert and each item allocation are separate commits. An item uses `AllocationConfirmation`, so a rejected item does not leave a one-sided balance. The item's idempotency key is derived from the organization, the batch key, the invoice, and the bank transaction. It is stored on the allocation with `IdempotencyOperation.BatchExactMatch` (3). A recovered item with that same identity is reported as `confirmed`. A stored row with the same key but a different operation or pair stops the attempt with `reconciliation.batch.allocation_integrity_conflict` and does not publish the other row's financial details.

If the process stops after an item commits, a later request with the same key recovers the committed items and continues the rest. Recovery reads those allocations before it reads the current book. A fully recovered receipt completes without loading the book. New items may start only while the receipt is younger than 15 minutes. The clock is read again immediately before each new item. Items that have not started when that limit is reached are `interrupted`. An item transaction that has already started may still commit. Previously committed allocations stay committed. Request cancellation is not a successful completed result.

### Book, probes, and review

The open book is capped at 5,000 invoices and 5,000 bank transactions. Either side over that cap returns `reconciliation.batch.candidate_limit_exceeded` for a new request and does not insert a receipt. The same failure on a running receipt that still has new work marks the uncommitted items `skippedCoverage` only when the attempt is still inside the 15-minute window and the book is actually loaded. Inconsistent stored money returns `reconciliation.matches.inconsistent_money`. It does not repair rows and it does not confirm a pair.

Competitor probes read at most 101 other open rows. One hundred rows can still show a unique pair. The extra row means the probe is coverage-limited. An ambiguous set of competitors wins over that coverage limit. Open `CompetingMatch` blocks both named records. Open `NeedsInvestigation` blocks the named record. Resolved and dismissed cases do not. Confirming an allocation does not close a review case.

### Tenancy and authorization

Both routes use the existing `TenantMember` policy. `OrganizationId` comes only from `ICurrentOrganizationContext`. A query value, a JSON field, or `X-Organization-Id` does not select the tenant. An idempotency key is unique per organization. Organization A's receipt and allocations are not returned to Organization B.

`TenantMember` is enough to start a financial batch today. The application does not have a separate approval role. Finer financial authorization is Phase 7. The generated OpenAPI document still has incomplete Bearer security metadata for these routes, as it does for the other reconciliation routes. That metadata gap is also a Phase 7 follow-up. The runtime policy is present.

### Failures that remain accepted

A competitor or a review case can appear after the item has checked them and before its commit. The open book can grow after the new-request gate. Two new requests can both pass that gate; the unique receipt index admits one. A `Running` receipt that nobody resumes stays `Running` until a later request with the same key continues it. There is no allocation fingerprint column beyond the derived item key. This phase does not add a regulatory-grade or immutable financial audit record, and it does not record the acting user.

## Alternatives considered

### Confirming the preview automatically

Rejected. A preview is a stale read. The operator must submit the pairs that may be confirmed, and the server still reloads the current rows.

### Allocating from `smart-1`

Rejected. A score is not a financial decision. Changing `smart-1` weights is outside this phase.

### One database transaction for the whole batch

Rejected. A 25-pair transaction would hold both aggregates for every pair until the last one finished. Independent item commits keep a successful allocation durable when a later item fails. The receipt and the item key make the retry safe.

### HTTP 207 or a per-item success status

Rejected. The command is one idempotent attempt with a mixed result document. Callers distinguish invalid input, an idempotency conflict, an execution in progress, the candidate limit, inconsistent money, an allocation identity conflict, and an infrastructure failure by the ProblemDetails code and status. An unexpected infrastructure exception is not reported as HTTP 200 or as a skipped item.

## Consequences

`GET /api/matches`, `GET /api/matches/smart`, `POST /api/matches/confirm`, `POST /api/allocations`, and the review-case routes keep their contracts. Batch confirmation does not change `exact-1` or `smart-1`. Phase 5 still owns persisted reversal and audit records for financial changes that do not yet have them. Phase 7 owns resource-based financial authorization and the missing OpenAPI Bearer metadata.
