# ADR 0003: Optimistic concurrency for Reconciliation

- Status: Accepted
- Date: 2026-09-05

## Context

ConfirmAllocation loads an Invoice and BankTransaction, applies domain allocate operations, inserts an Allocation, and commits once through `IUnitOfWork.SaveChangesAsync`. Two requests can load the same financial state, both pass domain invariants in memory, and only one should commit against that original state.

Phase 2B.1 deferred SQL Server `rowversion` to this decision.

## Decision

Use SQL Server `rowversion` as EF Core shadow properties on `Invoice` and `BankTransaction` only.

`ReconciliationDbContext` implements `IUnitOfWork` directly. The explicit `SaveChangesAsync` port catches `DbUpdateConcurrencyException` and throws Application `ConcurrencyConflictException`, preserving the EF exception as the inner exception. `ConfirmAllocationHandler` maps that abstraction to `Result.Failure` with `reconciliation.concurrency_conflict`.

`IUnitOfWork` remains a `Task` commit port. It does not return `Result` and does not expose EF types.

A concurrency failure does not retry `SaveChangesAsync`, reload tracked entities, or reuse the current DbContext. The scoped context is discarded.

`Allocation` does not receive a concurrency token. ConfirmAllocation inserts Allocation; token protection is needed on the aggregates it updates. Allocation concurrency can be revisited when a persisted reversal updates an existing allocation.

HTTP 409 mapping is not part of this decision.

## Alternatives considered

### Domain `RowVersion` properties

Rejected. Repositories already load tracked aggregates. The token can stay in the EF change tracker until a client ETag or similar use case exists.

### `Result` on `IUnitOfWork`

Rejected. That would mix a generic commit port with use-case error handling.

### Automatic retry

Rejected. A correct retry needs a new DbContext, fresh reads, and re-execution of domain invariants. Blindly retrying `SaveChanges` on the failed context would reuse stale original versions or persist a lost update.

### Allocation `rowversion`

Deferred. There is no persisted Allocation update use case yet.

## Consequences

Positive:

- stale ConfirmAllocation commits fail instead of overwriting newer financial state;
- Application stays EF-agnostic at the `IUnitOfWork` boundary;
- Domain entities stay free of persistence tokens;
- the implicit single-`SaveChanges` transaction still rolls back the losing attempt.

Negative:

- Domain and Application remain in the same assembly as Infrastructure, so EF isolation stays a convention and review concern;
- a future HTTP layer must map `reconciliation.concurrency_conflict` deliberately;
- callers must reload and re-run the use case after a conflict.
