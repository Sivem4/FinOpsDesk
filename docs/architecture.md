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
FinOpsDesk.E2ETests
```

`FinOpsDesk.E2ETests` is a local Chromium journey. It is not a project in `FinOpsDesk.slnx`, so `dotnet test FinOpsDesk.slnx` does not install a browser or start the journey. The project creates one `FinOpsDesk_E2E_<guid>` database, applies the existing Reconciliation migrations, and starts a loopback API process plus the Vite development server. It does not target the Development database `FinOpsDesk`. See `docs/e2e-testing.md`.

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

Phase 2B.2 adds SQL Server `rowversion` concurrency tokens as EF Core shadow properties on `Invoice` and `BankTransaction`. `IUnitOfWork.SaveChangesAsync` translates `DbUpdateConcurrencyException` into Application `ConcurrencyConflictException` and translates SQL unique-key errors 2601 and 2627 into `UniqueConstraintConflictException`. `ConfirmAllocationHandler` maps a concurrency conflict to `Result.Failure`. There is no automatic retry; a failed DbContext is discarded with its scope. `POST /api/matches/confirm` maps that conflict to HTTP 409. ADR 0012 later adds the same kind of token on `Allocation` for persisted reversal. See ADR 0003 and ADR 0006.

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

Each request commits alone. Repeating it allocates one invoice across several payments, or one payment across several invoices. Those calls are not an atomic batch. Operator-approved batch execution is the separate Phase 4G route. A later request that does not fit leaves the earlier allocations committed and does not store its idempotency key. A committed allocation is reversed only by the separate full-reversal command in ADR 0012. Phase 5B appends one confirmation audit entry in the same `SaveChangesAsync` as the allocation. The actor is the authenticated JWT `sub` claim. Any tenant member can call the route. Finer authorization remains Phase 7. See ADR 0011.

The allocation stores `IdempotencyOperation`: `ExactMatch` is 1 and `ExplicitAmount` is 2. Migration `20261026155535_AddAllocationIdempotencyOperation` backfills keyed Phase 4C rows to `ExactMatch` and adds `CK_Allocations_IdempotencyOperation`. That migration requires the key and operation to be both null, or the key present with operation 1 or 2. Phase 4G later allows operation 3 on the same check. The filtered unique index is unchanged. An explicit-amount replay matches the operation, the pair, the amount, and the currency. An exact-match replay matches the operation and the pair.

After the first idempotency lookup misses, a concurrent commit can still win. A balance failure or a unique-key/`rowversion` failure reads the key again on a new context. A matching fingerprint returns the original allocation. A different fingerprint returns the payload conflict. The second read can also miss while the winner has not committed. That response does not store the key. A retry of the original key and payload, once the winner is visible, returns the original allocation id and does not allocate again. There is no lock and no automatic retry. See ADR 0007.

Phase 4E adds read-only `GET /api/matches/smart` on the same `TenantMember` route group. `GetSmartMatchesHandler` uses the same `IExactMatchCandidateQueries` as exact matching, including the organization filter, the 5,000-row probe, and the stored-money checks. It then calls `SmartMatchRules.Evaluate`. The algorithm version is `smart-1`. A suggestion score is a sum of points. It is not a probability. The winning reference signal is `ReferenceExact` at 70 or one qualified signal at 60. Qualified signals do not stack: token-set equality, then compact form, then a shared numeric identifier of at least six digits. An equal open amount adds 25. A different open amount adds nothing. Currency mismatch produces no suggestion. The suggested amount is the smaller positive open amount and is not an allocation. `GET /api/matches`, `POST /api/matches/confirm`, and `POST /api/allocations` are unchanged. A smart suggestion cannot confirm an allocation. Query parameters are rejected with HTTP 400 `reconciliation.matches.smart.unsupported_query`. The candidate-limit and inconsistent-money codes stay `reconciliation.matches.candidate_limit_exceeded` and `reconciliation.matches.inconsistent_money`. On this route their ProblemDetails text says smart matching was not run. The exact-match route keeps the original text. See ADR 0008.

Phase 4G adds operator-approved batch exact matching on the same `TenantMember` route group. `GET /api/matches/batch/preview` classifies the complete open book with `exact-1`, applies open review blocks, then pages the eligible pairs. Page defaults to 1 and page size defaults to 50, with a maximum of 100. Currency totals cover the full eligible set. The preview does not persist a plan, create a `BatchExecution`, create an allocation, or change a review case. It does not authorize execution. An incomplete book returns the candidate-limit or inconsistent-money failure and is not described as a complete classification. `POST /api/matches/batch/execute` accepts 1 to 25 distinct pairs and a required `Idempotency-Key` UUID in `D` format. The JSON object allows only `pairs`, and each item allows only `invoiceId` and `bankTransactionId`. The body is limited to 8192 bytes and is rejected before it is parsed when it is larger. Organization, amount, currency, reference, score, allocation id, actor, and fingerprint are not accepted from the client. Submitting the pairs is the operator's approval to attempt confirmation. The server revalidates every pair. Only a unique exact-1 full current remainder can be confirmed. A completed attempt returns HTTP 200, including a batch whose items are all rejected. `allConfirmed` is true only when every item is confirmed. The same key and the same canonical pair set replay the stored result and do not allocate again. A different pair set for that key returns HTTP 409 `reconciliation.batch.idempotency_payload_conflict`. A receipt another request already owns returns HTTP 409 `reconciliation.batch.execution_in_progress`. A stored allocation whose identity does not match the item returns HTTP 409 `reconciliation.batch.allocation_integrity_conflict`. A new request whose open book exceeds the limit returns HTTP 409 `reconciliation.batch.candidate_limit_exceeded` and does not insert a receipt. Inconsistent stored money returns HTTP 500 `reconciliation.matches.inconsistent_money`. An unexpected infrastructure failure returns HTTP 500 `reconciliation.batch.infrastructure_failure` and does not turn committed items into a successful skip. `TenantMember` may start this financial batch. Finer financial authorization is Phase 7. Generated OpenAPI Bearer metadata for these routes remains incomplete and is the same Phase 7 follow-up. Each newly committed item also appends one confirmation audit entry in that item's transaction. The entry stores the persisted `BatchExecution` id and the actor from the request that committed the item. Replay of a completed receipt, and recovery of an item that is already committed, do not append another confirmation. A historical allocation that has no audit row is not backfilled. A Running receipt recovers a previously committed item by its original confirmation key even when that allocation is now Reversed. The recovered item remains historically confirmed, keeps the original allocation id and confirmation amount, and does not create a new allocation or confirmation audit. A Completed receipt keeps `ResultPayload`, `ResultVersion`, and `CompletedAt`. A later reversal does not change that stored result. See ADR 0010, ADR 0011, and ADR 0012.

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

`GET /api/matches` uses the same authorization and organization path. Its response is the advisory exact-match result for the open book, bounded by the fixed candidate limit. `GET /api/matches/smart` uses that same authorization and the same open-book query. Its response is the advisory `smart-1` ranking. It accepts no query parameters. `POST /api/matches/confirm` uses that same authorization. It reloads the named invoice and bank transaction and confirms only the current exact open amount. `POST /api/allocations` uses that same authorization and confirms one caller-supplied amount. It does not derive the amount from the open balances and it does not require the references to match. None of these reads is an authorization token for a later write. The same POST is the recovery mechanism for an explicit-amount attempt whose response was lost. A repeated request uses the original `Idempotency-Key`, pair, amount, and currency. The filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey` still decides whether that key already committed. A matching replay returns HTTP 200 and does not allocate again, including after the original allocation has been reversed. There is no separate operation-lookup route and no second idempotency table. A browser `sessionStorage` descriptor can preserve the key and the canonical JSON body across a reload in the same tab. It is not a lock, a reservation, or proof that the POST committed. Closing the tab, losing storage, using another tab, or explicitly abandoning the descriptor can leave a committed allocation that the browser no longer tracks. Abandonment does not roll back SQL Server. The Reconciliation screen can start that attempt from a Smart-1 suggestion after `GET /api/allocations/context`. Recovery of an already stored attempt does not require that read to succeed or the remaining balances to stay positive. A verified result invalidates the current epoch's invoice, match, and allocation-context queries even when the page is no longer mounted.

`POST /api/review-cases`, `GET /api/review-cases`, `GET /api/review-cases/{id}`, `POST /api/review-cases/{id}/resolve`, and `POST /api/review-cases/{id}/dismiss` use the same `TenantMember` authorization. `OrganizationId` comes only from `ICurrentOrganizationContext`. Opening a case records a manual exception for one invoice, one bank transaction, or both. It does not run `exact-1` or `smart-1`, and it does not allocate money. The list is paginated and ordered by opened time descending, then review case id descending. Detail returns the persisted case plus the current linked invoice outstanding amount and bank-transaction unapplied amount. Those figures are observations. Inconsistent stored money returns HTTP 500 `reconciliation.review_case.inconsistent_linked_money` and does not include the corrupted amounts. Resolve and dismiss submit the Base64 `rowversion` token read with the case. An identical terminal replay returns HTTP 200 and leaves `ClosedAt` and the token unchanged. See ADR 0009.

`GET /api/allocations/context` reads one invoice and one bank transaction in the authenticated organization. The query requires exactly one `invoiceId` and one `bankTransactionId`. Both must be non-empty GUIDs. Unknown or repeated query parameters, including a different casing of the same name, return HTTP 400 `reconciliation.allocation_context.invalid_request`. `OrganizationId` comes only from `ICurrentOrganizationContext`. A query or header value cannot select another tenant. The read does not require JWT `sub`. A missing invoice, including one owned by another organization, returns HTTP 404 `reconciliation.invoice.not_found` before the bank transaction is considered. A missing or foreign bank transaction then returns HTTP 404 `reconciliation.bank_transaction.not_found`. The response contains the invoice number, bank reference, booking date, source system, external id, and the stored principal, allocated, and remaining amounts. Source identity and the bank reference are null when the stored record has none. Remaining amounts are `TotalAmount - AllocatedAmount` and `Amount - AllocatedAmount`, calculated with `decimal`. A record whose stored principal is not strictly positive, whose allocated amount is negative or greater than the principal, whose currencies differ, or whose values do not fit `decimal(19,4)` returns HTTP 500 `reconciliation.allocation_context.inconsistent_money` and no partial body. An otherwise valid invoice and bank transaction in different currencies return HTTP 200 with each record's own currency. The response does not include `organizationId`, `rowversion`, or a decision that the pair can be allocated. The two lookups are separate statements. They do not lock either row and they are not one atomic snapshot. A database that has enabled read committed snapshot gives each statement its own versioned view; this repository does not enable that option in migrations or in the test database setup. `POST /api/allocations` reloads both aggregates and checks the current balances, currency, amount, concurrency, and idempotency at commit time.

`GET /api/allocations/{allocationId}/audit` lists the financial audit entries for one allocation in the authenticated organization. Page defaults to 1 and page size defaults to 50, with a maximum of 100. Ordering is `RecordedAt` ascending, then audit entry id ascending. An allocation that was committed before audit deployment returns HTTP 200 and an empty page. The read does not require JWT `sub`, does not create a missing confirmation row, and does not accept an organization id from the query string. An unknown allocation and an allocation owned by another organization both return HTTP 404. The page can include a confirmation entry, a reversal entry, or both. Confirmation `reason` is null. Reversal `reason` is the normalized reversal reason. See ADR 0011 and ADR 0012.

`GET /api/allocations` lists persisted allocations for the authenticated organization. It requires `TenantMember`. `OrganizationId` comes only from `ICurrentOrganizationContext`. Optional `invoiceId` and `bankTransactionId` are exact SQL predicates and combine with AND. Omitting a filter leaves that dimension unfiltered. An explicitly empty, empty-GUID, malformed, or repeated filter returns HTTP 400 `reconciliation.allocation_history.invalid_request` and does not run the history query. Invalid pagination, including an offset that does not fit in a 32-bit integer, returns HTTP 400 `reconciliation.allocation_history.invalid_pagination`. Page defaults to 1 and page size defaults to 50, with a maximum of 100. Ordering is `ConfirmedAt` descending, then allocation id descending. A valid filter with no rows, including an id owned by another organization, returns HTTP 200 and an empty page. The amount is the original allocation amount for both Confirmed and Reversed rows. Reversed rows stay in the default list. `status` is `confirmed` or `reversed`. `idempotencyOperation` is `exactMatch`, `explicitAmount`, `batchExactMatch`, or JSON null when the allocation row has no confirmation operation. Invoice number and bank reference are the current values on those records, not copies taken at confirmation. The list does not join `FinancialAuditEntry`, so an allocation without a confirmation audit remains visible. Actor, batch id, idempotency keys, `rowversion`, and current balances are not in this response. Actor and batch remain on `GET /api/allocations/{allocationId}/audit`. The count and the page share the same predicate and are two statements, not one snapshot. This GET does not mint an idempotency key, does not write, and is not proof that a particular POST committed. The operations workspace reads it at `/allocations` and opens `GET /api/allocations/{allocationId}/audit` only for the allocation the operator expands. See ADR 0013.

`GET /api/allocations/{allocationId}/reversal-context` reads the current persisted state of one allocation in the authenticated organization. It requires `TenantMember`. `OrganizationId` comes only from `ICurrentOrganizationContext` and is a SQL predicate together with the allocation id. The invoice and bank transaction are joined on `(OrganizationId, Id)`. A missing JWT `sub` is allowed. A malformed or empty allocation id, and any query parameter including `organizationId`, return HTTP 400 `reconciliation.allocation_reversal_context.invalid_request` before the query runs. A missing allocation and an allocation owned by another organization both return HTTP 404 `reconciliation.allocation.not_found`. The response contains the allocation id, invoice id, bank transaction id, current invoice number, current bank reference, original amount and currency, `status` (`confirmed` or `reversed`), `confirmedAt`, `reversedAt`, and `reversalReason`. Reversed rows stay addressable and keep the original positive amount. The read does not join `FinancialAuditEntry`, so an allocation without a confirmation audit is still returned. It does not return organization id, `rowversion`, idempotency keys, actor, batch id, audit payloads, or current balances. It is one `AsNoTracking` projection. It does not call `SaveChangesAsync`, does not lock or reserve the row, and does not set a response cache header. The other reads in this API also omit cache headers. `POST /api/allocations/{allocationId}/reverse` does not accept a client `rowversion`. It still loads the current rows, evaluates an existing reversal key before a new mutation, and lets EF Core compare the tracked `rowversion` values during that save. This GET is the current-state read used before a new reversal. The browser workflow posts `POST /api/allocations/{allocationId}/reverse` with only a trimmed `reason` and its own `Idempotency-Key`. It stores that exact JSON body in `finopsdesk.reversal-attempt` before the first send. A lost response is retried with the same key and body. The POST remains authoritative: another operator can reverse the allocation between the GET and the POST. A verified HTTP 200, including `replayed: true`, is the only automatic completion that clears that descriptor. Explicit confirmed removal of local tracking is a separate operator action and does not undo a server reversal. There is no partial reversal and no client `rowversion`.

## Data consistency

A transaction boundary follows one business operation.

Confirming an allocation updates the invoice financial state, the bank transaction financial state, the allocation record, and one confirmation audit record in the same transaction. It does not persist a `MatchSuggestion` and it does not record a suggestion state transition. `MatchSuggestion` remains an unmapped domain type.

Confirmed financial operations are reversed rather than deleted. `POST /api/allocations/{allocationId}/reverse` reverses one confirmed allocation for its full original amount. The allocation row remains, its status becomes Reversed, and the invoice and bank transaction receive the full original amount back. The confirmation identity stays unchanged. One reversal audit entry is appended in the same `SaveChangesAsync`, and an existing confirmation audit entry stays in place. A historical allocation without a confirmation audit can still be reversed. The audit trail records the financial events. `Allocation.Status` is the current state. This is not a general ledger and it is not event sourcing. The contract, idempotency rules, and batch-receipt policy are in ADR 0012.

`GET /api/allocations/context` does not open a transaction or request an isolation level. The invoice lookup and the bank-transaction lookup are separate statements, so they are not one transaction-wide snapshot. `GET /api/matches` and `GET /api/matches/smart` do not open a transaction or request an isolation level. Invoice candidates and bank-transaction candidates are separate statements, so they are not one transaction-wide snapshot. The smart response can be stale for the same reason as the exact response. Locking read committed releases shared locks as each statement reads. A database with read committed snapshot enabled still gives each statement its own versioned view. Phase 4B does not request snapshot isolation. Migrations and the test database setup do not enable `READ_COMMITTED_SNAPSHOT` or `ALLOW_SNAPSHOT_ISOLATION`, and production database options are not defined in this repository. The advisory result can be stale as soon as it is returned. Phase 4C reloads both aggregates and checks the current reference, currency, and open amounts before allocating. It does not take a serializable lock over other rows that might share the match key.

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

Command repositories load aggregates tracked by the scoped `ReconciliationDbContext`. `ReconciliationDbContext` implements Application `IUnitOfWork`. Confirming an allocation persists invoice, bank transaction, and allocation changes with one `SaveChangesAsync` call. EF Core/SQL Server supplies the transaction for that single SaveChanges. Explicit `BeginTransaction` is not used for this single-confirmation use case. Exact-match confirmation and explicit-amount allocation both store the client idempotency key on that same allocation insert, together with `IdempotencyOperation`. `IAllocationIdempotencyLookup` reads a committed key from a separate context so a failed `SaveChanges` is not queried through the tracked context that just rolled back.

Batch execution uses a different transaction shape. The receipt row is committed before the first allocation. One SQL connection then holds `UPDLOCK, ROWLOCK` on that receipt until the attempt completes. Each new item confirms through `AllocationConfirmation` in its own transaction and stores operation `BatchExactMatch` (3) on the derived item key. The same item transaction appends the confirmation audit entry, including the persisted batch id and the actor who committed that item. Completion updates the receipt on the lock connection. A completed receipt is not recalculated. There is no background worker. `BatchExecution` does not store an actor column. The financial actor is the audit entry. See ADR 0011.

The read side uses direct EF Core projections to endpoint-specific DTOs, normally with `AsNoTracking`. `GET /api/invoices` is the first such query: tenant-scoped, ordered by invoice id, and limited to a page size of 1 through 100. An optional `invoiceNumber` parameter trims the term and applies exact SQL equality on `InvoiceNumber`. `GET /api/bank-transactions` uses the same page bounds and orders by bank transaction id. An optional `reference` parameter does the same for `Reference`. An omitted parameter leaves the unfiltered list, including bank transactions whose reference is null. A supplied empty or whitespace-only term, or a term longer than 256 characters after trimming, returns HTTP 400 and does not repeat the term. No match returns HTTP 200 with an empty page and `totalCount` 0. Duplicate numbers and references stay in the result and are paged by id. Equality uses the column collation, not the `BIN2` collation of source identity, and the application does not change letter case. `InvoiceNumber` and `Reference` are `nvarchar(max)` without a search index, so an exact term can still scan one tenant's id range. The 5,000-row matching cap does not limit these tables. No index is added in this release. The reconciliation screen is the client that sends these optional filters; an empty search omits the parameter. Selecting both records does not add an allocation command or reserve either balance. The bank-transaction count uses the `(OrganizationId, Id)` alternate key together with the same optional reference predicate as the page. Its page is a scalar SQL projection, because `UnappliedAmount` is not a column and a bad currency must not be materialized through the value converter. `ListBankTransactionsHandler` then validates every selected row with `AllocationContextAmounts.TryRead`. One failing row on that filtered page returns HTTP 500 `reconciliation.bank_transactions.inconsistent_money` and no page items. The count and the page are separate statements, not one snapshot, and the read does not lock a balance. `GET /api/matches` and `GET /api/matches/smart` read open invoices and open bank transactions for the authenticated organization through `IExactMatchCandidateQueries`. The integrity checks are no-tracking EF queries. The candidate rows are scalar SQL projections of the stored amount and currency columns, capped at 5,000 rows on each side. Those candidate rows are not tracked. Smart matching does not add a table, a column, or an index.

A generic repository is not planned. A second unit-of-work wrapper over `DbContext` is not planned.

SQL Server-specific behaviour, including `rowversion`, provider constraints, and locking, must be tested against SQL Server rather than replaced with SQLite.

`ReviewCase` is persisted in the `reconciliation` schema. An open case is unique for one organization and subject key. A terminal case does not block a later open case for the same subject. `ReviewCase` uses a SQL Server `rowversion` shadow property. The HTTP concurrency token is the Base64 form of those 8 bytes. Review commands do not update invoice balances, bank-transaction balances, or allocation rows.

`Invoice` and `BankTransaction` use SQL Server `rowversion` as EF Core shadow properties. Confirmation updates those aggregates and inserts `Allocation` in one `SaveChangesAsync`. A stale version fails the whole commit. `Allocation` has its own SQL Server `rowversion` because reversal updates the existing row. It has a filtered unique index `UX_Allocations_OrganizationId_IdempotencyKey` on `(OrganizationId, IdempotencyKey)` where the confirmation key is not null, and a separate filtered unique index `UX_Allocations_OrganizationId_ReversalIdempotencyKey` where the reversal key is not null. Rows created without a key, including allocations from `ConfirmAllocationHandler`, are outside that index. A keyed row also stores `IdempotencyOperation`, and `CK_Allocations_IdempotencyOperation` requires the operation when the key is present. `ExactMatch` is 1, `ExplicitAmount` is 2, and `BatchExactMatch` is 3. Reversal audit uses operation 4, which is not a value of `Allocation.IdempotencyOperation`. Domain entities do not expose `RowVersion`. The tokens protect the invoice and bank transaction on confirmation, and those two rows plus the allocation on reversal. They do not replace domain validation. They do not stop another organization member from inserting a competing invoice or bank transaction, and they do not make a retry idempotent. The confirmation index does that for one confirmation key. The reversal index does it for one reversal key. The same UUID can be used in both namespaces. `IX_Allocations_OrganizationId_ConfirmedAt_Id` supports the tenant history order `(OrganizationId, ConfirmedAt DESC, Id DESC)`. It has no included columns. Reversal does not change `ConfirmedAt`, so that index is maintained on insert. Invoice and bank-transaction filters continue to use their existing foreign-key indexes. This index is not a measured performance claim. Two explicit amounts that share an invoice or bank transaction can still fail on `rowversion` even when both amounts would fit. Independent financial pairs can still commit concurrently. There is no distributed lock. See ADR 0012.

## Authentication direction

The host selects one authentication composition. `DevelopmentJwt` validates JWT Bearer access tokens for the `Development` and `E2E` environments. It does not issue tokens, refresh tokens, or manage users, and it does not register OpenID Connect or cookie CSRF. `ProductionCookie` is the composition for every other environment, including Production. Cookie authentication is the default authenticate and challenge scheme. OpenID Connect is registered as a named remote handler and is challenged only by `POST /login`. JWT bearer is not registered. A bearer token does not authenticate the request, and an unauthenticated API call returns 401 instead of a provider redirect.

`ProductionCookie` is a confidential authorization-code client with PKCE. The scope is `openid`. Tokens are not saved, `offline_access` is not requested, and inbound claim mapping is disabled. After a validated callback the cookie principal keeps `iss`, `sub`, and an optional `name` display claim as separate claims. It does not keep `org_id`, email, roles, or provider permissions. Email is not the stable identifier. `FinancialApiAccessRequirement` still fails closed, so an authenticated session cannot call financial routes. `GET /api/session` reports `financialAccess: false` and never returns an organization id or an OAuth token.

The session cookie remains `__Host-finopsdesk-session`: `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`, sliding expiration off, and an eight-hour absolute lifetime. OpenID Connect correlation and nonce cookies stay `SameSite=None` and `Secure` so the provider can return the browser to the callback. They do not use the application `Strict` policy. The callback path is `/signin-oidc` and is handled inside authentication middleware, before antiforgery. The redirect URI is built from `Authentication:Oidc:BrowserOrigin`, not from `Host` or forwarded headers. `POST /logout` deletes the local cookie. Provider end-session is deferred because `SaveTokens` is false and no `id_token_hint` is available. Local logout does not call the provider.

CSRF uses the ASP.NET Core antiforgery services. `GET /api/session` issues the request token. Clients send it in `X-CSRF-Token`. The antiforgery cookie is `__Host-finopsdesk-csrf` (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`). `ProductionCsrfMiddleware` runs after `UseAuthorization` and before the endpoint. It calls `IAntiforgery.ValidateRequestAsync` for unsafe methods on `POST /login`, `POST /logout`, and the `/api` group. A multipart request without the header is rejected before the form is read. Invalid CSRF returns 400 `auth.csrf.invalid` and does not run the handler. Authorization can still return 401 or 403 before that check. `DevelopmentJwt` does not register this middleware.

The cookie principal is built from the validated token issuer and the principal's `sub` and optional `name` claims. It does not downcast the token to `JwtSecurityToken` or `JsonWebToken`. The host validates that token with `JsonWebTokenHandler` (`UseSecurityTokenValidator` remains false). Signature, issuer, audience, lifetime, nonce, and correlation checks stay enabled. `ProtocolValidator.RequireStateValidation` stays false because the handler unprotects `state`, checks the correlation cookie, and clears `state` before the protocol validator runs.

The Data Protection key ring is the default local ring. More than one API instance needs a shared key ring before the cookie or antiforgery cookie can be read by another instance. This increment does not add a distributed session store. There is no production identity-provider deployment in the repository. A `ProductionCookie` host fails startup when `Authentication:Oidc` authority, client id, client secret, or browser origin is missing or invalid. An HTTP authority is accepted only when `RequireHttpsMetadata` is false and `Uri.IsLoopback` is true. A remote HTTP authority does not start. `DevelopmentJwt` does not require those settings.

The React application and the Vite proxy are unchanged. Local browser traffic still uses `DevelopmentJwt`. Same-origin routing for `/login`, `/logout`, and `/signin-oidc` is a later increment. Organization membership, roles, resource authorization, and OpenAPI security metadata remain later Phase 7 work. The host still implements `ICurrentOrganizationContext`, and modules still receive `OrganizationId` explicitly. The generated OpenAPI document does not yet include security metadata for `GET /api/matches`, `GET /api/matches/smart`, `GET /api/matches/batch/preview`, `POST /api/matches/batch/execute`, or the review-case routes. Those routes still require `TenantMember` at runtime.

Future machine-to-machine endpoints may use a separate JWT bearer scheme with narrow scopes.

## Infrastructure triggers

RabbitMQ or another broker is considered only when a reliable event must be consumed by an independently deployed process. An outbox is introduced before broker publication.

Redis is considered after measurement demonstrates a distributed caching or shared-state requirement.

SignalR is considered when users need live import progress or collaborative work-queue updates and polling is no longer appropriate.

Background jobs are considered when imports or matching exceed a reasonable HTTP request duration or require durable scheduling and retries.
