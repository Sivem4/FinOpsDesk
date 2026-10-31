# ADR 0013: Allocation history is an aggregate-state read model

- Status: Accepted
- Date: 2026-10-31
- Extends: [ADR 0011](0011-financial-audit-foundation.md) for the separate audit read, and [ADR 0012](0012-allocation-reversal.md) for reversed rows remaining in place

## Context

Operators need a tenant-scoped list of allocations that were actually persisted: confirmed rows, reversed rows, and older rows that have no confirmation audit. Phase 5B did not backfill audit entries. `ConfirmAllocationHandler` can also persist an allocation without an idempotency key and without an audit row. A history list that inner-joins `FinancialAuditEntry` would hide those allocations.

The audit read already returns actor, batch, action, and operation for one allocation. The allocation row already stores status, the original amount, confirmation time, reversal time, and reversal reason. Invoice number and bank reference live on the current source rows. Restrict foreign keys and the absence of a delete API keep those parents in place.

`GET /api/allocations` must not become a second write protocol. Recovery of an explicit-amount attempt stays on `POST /api/allocations` with the original idempotency key.

## Decision

### Read model

`GET /api/allocations` reads `Allocation` and inner-joins the current `Invoice` and `BankTransaction` on `(OrganizationId, Id)`. It does not join `FinancialAuditEntry`. An allocation without a confirmation audit is still returned.

The item contains the allocation id, invoice id, bank transaction id, current invoice number, current bank reference, original amount and currency, status (`confirmed` or `reversed`), `confirmedAt`, `reversedAt`, `reversalReason`, and `idempotencyOperation` (`exactMatch`, `explicitAmount`, `batchExactMatch`, or JSON null). Reversed rows stay in the default list. The amount is the stored allocation amount and is not negated.

The response does not include organization id, idempotency keys, `rowversion`, actor, batch id, audit payloads, or current invoice and bank balances. Actor and batch stay on `GET /api/allocations/{allocationId}/audit`. A history row is not a concurrency token for reversal.

Invoice number is not unique. Bank reference is nullable and not unique. The identifiers disambiguate rows that share a label. Those labels are current source values, not snapshots taken at confirmation.

### Tenant, filters, and order

The route requires `TenantMember`. `OrganizationId` comes only from `ICurrentOrganizationContext` and is a SQL predicate before pagination. Optional `invoiceId` and `bankTransactionId` are additional equality predicates. Supplying both means AND. Omitting both returns the tenant history. A filter that matches nothing, including an id owned by another organization, returns HTTP 200 with an empty page and `totalCount` 0. It does not return HTTP 404 and it does not return the other tenant's rows.

There is no global EF query filter on `ReconciliationDbContext`. The organization predicate in this query is the security boundary.

Ordering is `ConfirmedAt` descending, then allocation id descending. Page defaults to 1. Page size defaults to 50 and cannot exceed 100. The offset is checked with 64-bit arithmetic before it becomes a 32-bit skip. Invalid pagination returns HTTP 400 `reconciliation.allocation_history.invalid_pagination`. An empty, empty-GUID, malformed, repeated, or unknown query parameter, including `organizationId`, returns HTTP 400 `reconciliation.allocation_history.invalid_request`. Those rejections happen before the history SQL. The problem response does not echo the raw query value.

`totalCount` and the page use the same tenant and optional-id predicate. They are two statements. They do not share a transaction or a snapshot isolation level. A concurrent insert can make the count and the page observe different moments.

### Index

`IX_Allocations_OrganizationId_ConfirmedAt_Id` is `(OrganizationId ASC, ConfirmedAt DESC, Id DESC)`. It has no included columns. `ConfirmedAt` does not change when a row is reversed, so reversal does not move the row inside this index. The index is not a measured latency claim. Filters on invoice id or bank transaction id continue to use the existing foreign-key indexes.

### Recovery

This GET does not clear `finopsdesk.explicit-amount-attempt`, does not mint an idempotency key, and does not call POST. A history row with the same pair and amount does not prove that a particular idempotent attempt committed.

## Consequences

The history screen can be added later against this contract without changing confirmation or reversal. Drill-in to the existing audit endpoint remains the way to see actor and batch. Phase 6 stays planned. The reversal screen remains a separate increment and must read a fresh `rowversion` at reversal time, not a token from this list.
