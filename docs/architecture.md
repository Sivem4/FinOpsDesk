# Architecture

## Architectural style

FinOpsDesk is a modular monolith hosted by one ASP.NET Core process and backed initially by one SQL Server database.

This style is selected because the first product workflows require strong consistency across related financial records, while a single developer must still be able to run, debug, migrate, and deploy the application without distributed-system overhead.

A modular monolith does not mean one unstructured application. Modules own business capabilities, expose deliberate entry points, and hide internal implementation details.

## Current projects

```text
FinOpsDesk.Api
FinOpsDesk.SharedKernel
FinOpsDesk.Modules.Reconciliation
FinOpsDesk.UnitTests
FinOpsDesk.IntegrationTests
FinOpsDesk.ArchitectureTests
```

### FinOpsDesk.Api

The API project is the host and composition root.

It owns:

- application startup;
- ASP.NET Core middleware;
- module registration;
- authentication schemes;
- authorization policies;
- OpenAPI;
- health checks;
- global HTTP error handling;
- configuration and observability wiring.

It must not contain business rules.

### FinOpsDesk.SharedKernel

SharedKernel contains only concepts that are genuinely shared by multiple modules and stable enough to justify coupling.

It must not become a collection of generic helpers, transport DTOs, application services, or infrastructure utilities.

### Reconciliation module

The Reconciliation module owns:

- customer invoices needed by reconciliation;
- incoming bank transactions;
- import batches;
- deterministic match suggestions;
- payment allocations;
- reconciliation work queues.

Invoices, bank transactions, suggestions, and allocations initially belong to one module because confirming an allocation changes them as one consistency-sensitive workflow.

## Internal module layers

The Reconciliation module will use these namespaces and folders:

```text
Domain/
Application/
Infrastructure/
Presentation/
```

The intended dependency direction is:

```text
Presentation -> Application -> Domain
Infrastructure -> Application and Domain
```

Because the first version uses one assembly for the whole module, these boundaries are conventions enforced by architecture tests and code review rather than separate project references.

The module may be split into multiple assemblies later if it becomes large, has multiple contributors, or repeatedly violates internal dependency rules.

Phase 2A introduced the first Reconciliation Application command slice: `ConfirmAllocation`. The handler coordinates existing domain allocate operations and creates a confirmed allocation. It commits once through an Application `IUnitOfWork` port (`SaveChangesAsync` only) after those domain operations succeed. `IUnitOfWork` does not expose EF Core types, transactions, or affected-row counts.

Phase 2B.1 adds the first persistence adapter: module-owned `ReconciliationDbContext`, SQL schema `reconciliation`, specialized command repositories, and SQL Server migrations.

Phase 2B.2 adds SQL Server `rowversion` concurrency tokens as EF Core shadow properties on `Invoice` and `BankTransaction`. `IUnitOfWork.SaveChangesAsync` translates `DbUpdateConcurrencyException` into Application `ConcurrencyConflictException` and translates SQL unique-key errors 2601 and 2627 into `UniqueConstraintConflictException`. `ConfirmAllocationHandler` maps a concurrency conflict to `Result.Failure`. There is no automatic retry; a failed DbContext is discarded with its scope. `POST /api/matches/confirm` maps that conflict to HTTP 409. See ADR 0003 and ADR 0006.

Phase 2B.3 adds the first HTTP read slice, `GET /api/invoices`. The API host validates JWT Bearer access tokens and does not issue them. A fallback authorization policy requires an authenticated user. The Reconciliation route group uses a separate `TenantMember` policy: an authenticated user plus a configured organization claim that parses to a non-empty Guid. `/health` stays anonymous. The API host implements `ICurrentOrganizationContext` from that claim. `ListInvoicesHandler` reads the organization once and passes it to `IInvoiceQueries`. The query is an organization-scoped, no-tracking DTO projection with bounded pagination. The invoice endpoint maps invalid pagination to HTTP 400. See ADR 0004.

Phase 3C adds synchronous CSV import at `POST /api/imports/invoices` and `POST /api/imports/bank-transactions`. Both require `TenantMember`. The client sends `file` and `sourceSystem`. Organization id comes only from the authenticated claim. A structurally valid file commits one `ImportBatch` with its accepted financial rows and row errors. An exact file-hash replay returns the stored batch. Unique-key collisions retry on a fresh persistence context. The database uniqueness constraints remain the final authority.

Phase 4A adds pure exact-match rules in `ExactMatchRules`. The match key is the `exact-1` normalized payment reference, currency, and open balance: invoice outstanding amount against bank-transaction unapplied amount. `InvoiceNumber` is compared with `BankTransaction.Reference`. Normalization trims and applies invariant uppercase; internal spaces and punctuation stay in place. The operation does not load, save, or modify aggregates. Results are not persisted. A snapshot whose organization differs from the organization passed to `Evaluate` throws `ArgumentException` and is not classified. See ADR 0005.

Phase 4B adds read-only `GET /api/matches` on the existing `TenantMember` route group. `GetExactMatchesHandler` reads `OrganizationId` from `ICurrentOrganizationContext` and loads candidates through `IExactMatchCandidateQueries`. Each statement filters `OrganizationId`. The integrity checks are no-tracking EF queries. The candidate reads are scalar SQL projections, and the currency column is selected as text so a non-letter `char(3)` value can be rejected before a snapshot is built. Open invoices are rows whose `TotalAmount` exceeds `AllocatedAmount`. Open bank transactions are rows whose `Amount` exceeds `AllocatedAmount`. Null and blank references stay in that set; `ExactMatchRules` classifies them. The handler then calls `ExactMatchRules.Evaluate` and maps the result to HTTP DTOs. The endpoint does not save, allocate, or persist suggestions.

The open book is capped at 5,000 invoices and 5,000 bank transactions per organization (`ExactMatchCandidateLimits.MaxPerSide`). The query reads at most one extra row on each side, ordered by id. When either side is larger, the handler returns `reconciliation.matches.candidate_limit_exceeded` and the endpoint responds with HTTP 409. A truncated set is not matched. The limit is not a request parameter. The endpoint is not paginated, because a page could hide an ambiguous counterpart.

Before the candidate reads, each table is checked with an organization-scoped `EXISTS`. A row fails that check when its original amount is less than or equal to zero, its allocated amount is negative, its allocated amount exceeds the original amount, or its original and allocated currency codes differ. Invoice columns are `TotalAmount`, `TotalCurrency`, `AllocatedAmount`, and `AllocatedCurrency`. Bank-transaction columns are `Amount`, `Currency`, `AllocatedAmount`, and `AllocatedCurrency`. One violating row fails the request with `reconciliation.matches.inconsistent_money` and HTTP 500, and the handler does not call `ExactMatchRules.Evaluate`. The check does not scan allocation history and does not repair rows. Currency equality on the same row is this stored-money invariant. Comparing an invoice with a bank transaction stays inside `exact-1`. Those currency columns are non-null `char(3)` and have no check that the value is three letters. A projected code that is not three letters fails the same inconsistent-money result when the snapshots are built, before `Evaluate`.

Those statements are separate reads. Phase 4B does not open a transaction and does not set an isolation level, so the statements are not one transaction-wide snapshot. SQL Server's default for such a statement is read committed. Locking read committed releases shared locks as each statement reads. Read committed snapshot, when a database has that option on, still versions each statement separately. Holding one view across both statements requires snapshot isolation and the database option that allows it. Migrations do not set `READ_COMMITTED_SNAPSHOT` or `ALLOW_SNAPSHOT_ISOLATION`. The test setup creates a database and does not alter those options, so the test database keeps the server model database's settings. Production database options are not defined in this repository. The invoice read and the bank-transaction read can observe different committed states. The response can be stale as soon as it is produced. It is advisory. The integrity `EXISTS` queries can use the `(OrganizationId, Id)` index and then look up amount columns. No extra index is added for this read. On a clean organization the request runs two existence checks and two bounded candidate queries.

Phase 4C confirms one explicitly selected pair at `POST /api/matches/confirm`. The client sends the invoice id, the bank transaction id, and a required `Idempotency-Key` UUID. Organization id comes only from `ICurrentOrganizationContext`. The handler reloads both aggregates for that organization. `ExactMatchConfirmation` derives the amount from the current outstanding and unapplied balances. Confirmation proceeds only when the `exact-1` normalized references are equal and present, the currencies are equal, and both open amounts are equal and positive. The JSON body accepts only `invoiceId` and `bankTransactionId`. Any other property, including `amount`, is rejected with HTTP 400 before confirmation runs. The client does not send an amount, a reference, or a classification. `GET /api/matches` is not an authorization token and is not required. A named pair is not rejected merely because another open row shares its key. Global uniqueness is not rechecked, because `rowversion` on the two selected rows does not cover competing inserts.

`AllocationConfirmation.Apply` checks `CanAllocate` on both aggregates before either `Allocate` call. A bank-side or currency failure therefore leaves the tracked invoice unchanged, and a later `SaveChangesAsync` on that context cannot persist a one-sided balance update. `ConfirmAllocationHandler` uses the same apply operation and still accepts a caller-supplied amount, including a partial amount. That command is not exposed over HTTP.

The idempotency key is stored on `Allocation`. The filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey` is the authority for a repeated key inside one organization. The allocation insert and both balance updates commit in the existing single `SaveChangesAsync`. A replay after a committed success returns the original allocation id and does not allocate again. The same key with a different pair returns `reconciliation.confirmation.idempotency_payload_conflict`. After a unique-key or `rowversion` failure, the handler reads the winner through `IAllocationIdempotencyLookup` on a new `DbContext` and does not call `SaveChanges` again on the failed context. A missing invoice or bank transaction, including an id owned by another organization, uses the existing not-found results. See ADR 0006.

Phase 4D adds `POST /api/allocations` on the same `TenantMember` route group. One request allocates one explicit positive amount from one bank transaction to one invoice. The references do not have to match. The body is `invoiceId`, `bankTransactionId`, and `amount` with a JSON number and a three-letter currency. Unknown properties, a string amount, and a JSON number that is not exactly the parsed `decimal` are rejected before a command runs. `ExplicitAmountValidation` requires a value greater than zero, below 10^15, that already fits `decimal(19,4)`. The body is limited to 4096 bytes. `Content-Type` must be `application/json`. The required `Idempotency-Key` header is one UUID in `D` format.

The handler is `ConfirmExplicitAmountHandler`. It uses `AllocationConfirmation.Apply`, so a failed check does not leave a tracked aggregate dirty. The first success returns HTTP 201. A replay returns HTTP 200 with the original allocation id and amount. Currency mismatch, either open-balance overflow, an idempotency payload conflict, and a `rowversion` conflict return HTTP 409. A missing or foreign id returns HTTP 404 without the other tenant's financial details. Organization id is taken only from `ICurrentOrganizationContext`.

Each request commits alone. Repeating it allocates one invoice across several payments, or one payment across several invoices. Those calls are not an atomic batch, and there is no batch route. A later request that does not fit leaves the earlier allocations committed and does not store its idempotency key. There is no compensation, persisted reversal, or audit row. Any tenant member can call the route. The acting user is not recorded. Those gaps stay in Phases 5 and 7.

The allocation stores `IdempotencyOperation`: `ExactMatch` is 1 and `ExplicitAmount` is 2. Migration `20261026155535_AddAllocationIdempotencyOperation` backfills keyed Phase 4C rows to `ExactMatch` and adds `CK_Allocations_IdempotencyOperation`. Key and operation are both null, or the key is present and the operation is 1 or 2. The filtered unique index is unchanged. An explicit-amount replay matches the operation, the pair, the amount, and the currency. An exact-match replay matches the operation and the pair.

After the first idempotency lookup misses, a concurrent commit can still win. A balance failure or a unique-key/`rowversion` failure reads the key again on a new context. A matching fingerprint returns the original allocation. A different fingerprint returns the payload conflict. The second read can also miss while the winner has not committed. That response does not store the key. A retry of the original key and payload, once the winner is visible, returns the original allocation id and does not allocate again. There is no lock and no automatic retry. See ADR 0007.

## Request flow

A command should follow this conceptual flow:

```text
HTTP endpoint
  -> authentication and authorization
  -> request mapping
  -> application command
  -> validation
  -> handler
  -> domain behaviour
  -> persistence transaction
  -> audit record
  -> result mapped to HTTP
```

A query should follow:

```text
HTTP endpoint
  -> authentication and authorization
  -> application query
  -> organization-scoped, no-tracking DTO projection
  -> paginated response
```

`GET /api/matches` uses the same authorization and organization path. Its response is the advisory exact-match result for the open book, bounded by the fixed candidate limit. `POST /api/matches/confirm` uses that same authorization. It reloads the named invoice and bank transaction and confirms only the current exact open amount. `POST /api/allocations` uses that same authorization and confirms one caller-supplied amount. It does not derive the amount from the open balances and it does not require the references to match.

## Data consistency

A transaction boundary follows one business operation.

Confirming an allocation must update the invoice financial state, bank transaction financial state, allocation record, relevant suggestion state, and audit record atomically.

Confirmed financial operations are reversed rather than deleted.

`GET /api/matches` does not open a transaction or request an isolation level. Invoice candidates and bank-transaction candidates are separate statements, so they are not one transaction-wide snapshot. Locking read committed releases shared locks as each statement reads. A database with read committed snapshot enabled still gives each statement its own versioned view. Phase 4B does not request snapshot isolation. Migrations and the test database setup do not enable `READ_COMMITTED_SNAPSHOT` or `ALLOW_SNAPSHOT_ISOLATION`, and production database options are not defined in this repository. The advisory result can be stale as soon as it is returned. Phase 4C reloads both aggregates and checks the current reference, currency, and open amounts before allocating. It does not take a serializable lock over other rows that might share the match key.

## Multi-tenancy

The MVP uses shared-database multi-tenancy. Tenant-owned rows share tables and are distinguished by `OrganizationId`. This is not a schema-per-tenant model.

The `reconciliation` SQL schema is module ownership of Reconciliation tables, not tenant isolation.

The effective organization is the organization claim on the authenticated `ClaimsPrincipal`. The configured token issuer is trusted to issue that claim only for an organization the caller may act within. A client-supplied organization selector is never authoritative. The API host adapts the claim to `ICurrentOrganizationContext`. Application commands and queries receive `OrganizationId` explicitly. That identifier is applied to:

- queries;
- commands;
- entity creation;
- authorization;
- unique constraints;
- audit records;
- idempotency keys;
- tests.

A client-supplied organization identifier is never authoritative.

Entity identifiers such as `InvoiceId`, `BankTransactionId`, and `AllocationId` are globally unique within FinOpsDesk and represent entity identity `OrganizationId` represents tenant ownership and is not part of entity identity.

Persistence operations are nevertheless scoped by `OrganizationId` to enforce tenant isolation. Composite `(OrganizationId, Id)` keys and foreign keys provide database-level defence in depth for tenant-owned relationships.

## Persistence

The write side uses tracked aggregates and explicit business transaction boundaries.

Reconciliation persistence is owned by `ReconciliationDbContext` in the Reconciliation module. Tables live in SQL schema `reconciliation`. Migrations are owned by the same module. The API host supplies `ConnectionStrings:FinOpsDesk` and does not own the model.

Command repositories load aggregates tracked by the scoped `ReconciliationDbContext`. `ReconciliationDbContext` implements Application `IUnitOfWork`. Confirming an allocation persists invoice, bank transaction, and allocation changes with one `SaveChangesAsync` call. EF Core/SQL Server supplies the transaction for that single SaveChanges. Explicit `BeginTransaction` is not used for this use case. Exact-match confirmation and explicit-amount allocation both store the client idempotency key on that same allocation insert, together with `IdempotencyOperation`. `IAllocationIdempotencyLookup` reads a committed key from a separate context so a failed `SaveChanges` is not queried through the tracked context that just rolled back.

The read side uses direct EF Core projections to endpoint-specific DTOs, normally with `AsNoTracking`. `GET /api/invoices` is the first such query: tenant-scoped, ordered by invoice id, and limited to a page size of 1 through 100. `GET /api/matches` reads open invoices and open bank transactions for the authenticated organization. Its integrity checks are no-tracking EF queries. Its candidate rows are scalar SQL projections of the stored amount and currency columns, capped at 5,000 rows on each side. Those candidate rows are not tracked.

A generic repository is not planned. A second unit-of-work wrapper over `DbContext` is not planned.

SQL Server-specific behaviour, including `rowversion`, provider constraints, and locking, must be tested against SQL Server rather than replaced with SQLite.

`Invoice` and `BankTransaction` use SQL Server `rowversion` as EF Core shadow properties. Confirmation updates those aggregates and inserts `Allocation` in one `SaveChangesAsync`. A stale version fails the whole commit. `Allocation` does not have a concurrency token. It has a filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey` on `(OrganizationId, IdempotencyKey)` where the key is not null. Rows created without a key, including allocations from `ConfirmAllocationHandler`, are outside that index. A keyed row also stores `IdempotencyOperation`, and `CK_Allocations_IdempotencyOperation` requires the operation when the key is present. `ExactMatch` is 1 and `ExplicitAmount` is 2. Domain entities do not expose `RowVersion`. The `rowversion` tokens protect the two updated rows. They do not stop another organization member from inserting a competing invoice or bank transaction, and they do not make a retry idempotent. The unique index does that for one confirmation key. Two explicit amounts that share an invoice or bank transaction can still fail on `rowversion` even when both amounts would fit.

## Authentication direction

The API currently validates JWT Bearer access tokens. It does not issue tokens, refresh tokens, or manage users. The host configures issuer or authority, audience, signing credentials or OpenID Connect metadata, lifetime, and HTTPS metadata. `MapInboundClaims` is disabled. The organization claim type defaults to `org_id`.

The planned browser architecture is still a backend-for-frontend pattern with OpenID Connect. That can replace token transport without changing the tenant port: the host still implements `ICurrentOrganizationContext`, and modules still receive `OrganizationId` explicitly.

In that future browser flow, the backend acts as the confidential OIDC client and maintains the authenticated browser session in a secure HTTP-only cookie. The React application calls same-origin API endpoints. Mutating cookie-authenticated requests require CSRF protection.

Future machine-to-machine endpoints may use a separate JWT bearer scheme with narrow scopes.

## Infrastructure triggers

RabbitMQ or another broker is considered only when a reliable event must be consumed by an independently deployed process. An outbox is introduced before broker publication.

Redis is considered after measurement demonstrates a distributed caching or shared-state requirement.

SignalR is considered when users need live import progress or collaborative work-queue updates and polling is no longer appropriate.

Background jobs are considered when imports or matching exceed a reasonable HTTP request duration or require durable scheduling and retries.
