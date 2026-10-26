# ADR 0006: Exact match confirmation

- Status: Accepted
- Date: 2026-10-25

## Context

`GET /api/matches` returns an advisory `exact-1` snapshot. The snapshot can be stale before a user confirms it. `ConfirmAllocationHandler` can already allocate a caller-supplied amount, including a partial amount, but a failed bank allocation leaves the tracked invoice modified. The handler also generates a new `AllocationId` on every call, so a retry after a successful commit can insert another allocation while open balances still allow it.

`Invoice` and `BankTransaction` already have SQL Server `rowversion` tokens. One `SaveChangesAsync` already commits both balance updates and the allocation insert. Those tokens do not cover rows the command never loaded, and they do not identify a logical retry.

## Decision

`POST /api/matches/confirm` confirms one pair named by the client.

The request body contains only `invoiceId` and `bankTransactionId`. Any other JSON property, including `amount`, is rejected with HTTP 400 and does not create an allocation. The required `Idempotency-Key` header is a UUID in `D` format. The client does not send an amount, a reference, a classification, or an organization id. `OrganizationId` comes from `ICurrentOrganizationContext`.

The handler reloads both aggregates in that organization. `ExactMatchConfirmation.TryDeriveAmount` returns the open amount only when:

- the `exact-1` normalized invoice number and bank reference are present and equal;
- the open-amount currencies are equal;
- both open amounts are equal and greater than zero.

That amount is allocated in full. A partial amount is not accepted on this route. `ConfirmAllocationHandler` still accepts a caller-supplied amount for a later partial-allocation workflow, and it is not registered for HTTP.

A named pair is not rejected because another open record shares its key. `GET /api/matches` reports ambiguity. Confirmation does not scan or lock the rest of the book. `rowversion` on the two selected rows does not prove the pair is still the only exact match.

`Invoice.CanAllocate` and `BankTransaction.CanAllocate` perform the existing balance and currency checks without changing `AllocatedAmount`. `AllocationConfirmation.Apply` runs both checks before either `Allocate`. A validation failure leaves both tracked aggregates unchanged.

The idempotency key is a nullable column on `Allocation`. The filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey` covers non-null keys and is scoped by `OrganizationId`. The key, the allocation, and both balance updates commit in the same `SaveChangesAsync`. A failed validation or a rolled-back conflict does not store the key, so the client can retry it.

A committed key is read through `IAllocationIdempotencyLookup` on a new context. The same key and the same pair return the original allocation id with `replayed: true`, including after the open balances have changed. The same key and a different pair return `reconciliation.confirmation.idempotency_payload_conflict`. SQL errors 2601 and 2627 become `UniqueConstraintConflictException` at the `IUnitOfWork` boundary. The handler does not call `SaveChanges` again on the context that failed. If the fresh read does not find the key, the result is `reconciliation.concurrency_conflict`.

Missing and cross-tenant ids use the existing not-found results. Composite foreign keys remain the database guard against an allocation pointing at another organization's parent.

There is no serializable transaction, no extra lock, and no generic idempotency store.

## Alternatives considered

### Requiring the pair to still be the unique exact match

Rejected for this phase. Uniqueness is a predicate over every eligible competitor. The normalized reference is not stored, so a lock on the raw invoice number or bank reference misses case differences. Read committed allows a competing import to commit after a uniqueness read. Making that guarantee durable needs a stored match key and predicate locking. Two commands that name different partners for the same invoice still collide on that invoice's `rowversion`.

### Accepting a client amount on this route

Rejected. That would turn exact confirmation into partial allocation. Explicit-amount allocation is `POST /api/allocations`, recorded in ADR 0007. `ConfirmAllocationHandler` keeps its amount argument and is not an HTTP command.

### Undoing a failed bank allocation with `ReverseAllocation`

Rejected. Reversal is a different business operation. If its rules diverge, the undo can fail and the tracked invoice stays dirty. Prevalidation does not mutate either aggregate.

### A separate idempotency table

Rejected. The allocation row already stores the pair that is the request fingerprint. A second row would have to commit in the same transaction and would not say anything the allocation does not already say.

### An existence check without a unique index

Rejected. Two concurrent requests can both observe no row and both insert. The filtered unique index is the authority. The lookup is only the fast path and the recovery read.

## Consequences

Positive:

- a bank-side validation failure cannot be persisted by a later save on the same context;
- a lost HTTP response can be retried without a second allocation;
- the same key in another organization is a different operation;
- balance races still fail the whole `SaveChanges`, including the allocation insert;
- partial allocation is not exposed by this endpoint. Explicit-amount allocation is the separate route `POST /api/allocations`, recorded in ADR 0007.

Negative:

- two different keys for one user action are two operations, and the second fails the balance or exact-pair check;
- an explicitly named ambiguous pair can be confirmed;
- optimistic concurrency can reject a second allocation that would still have fit, and the client must reload;
- `AllocatedAmount` is still not constrained to equal the sum of allocation rows.
