# Roadmap

## Phase 0: Repository foundation

Status: Complete.

Delivered:

- solution and project structure;
- API host;
- Reconciliation module entry point;
- health endpoint;
- OpenAPI in Development;
- initial architecture test;
- repository-wide build settings;
- project documentation;
- coding-agent instructions;
- continuous integration for backend build, test, and format checks on pushes and pull requests to `main`.

## Phase 1: Reconciliation domain

Status: Complete.

Delivered:

- currency representation;
- money representation;
- invoice aggregate;
- bank transaction aggregate;
- allocation model;
- match suggestion model;
- domain result and error conventions.

Unit tests cover:

- partial payment;
- multiple payments to one invoice;
- one payment allocated to multiple invoices;
- unapplied overpayment;
- invalid amount;
- cross-currency rejection;
- allocation reversal.

`MatchSuggestion` is a domain model. It is not persisted, and Phase 4 matching does not use it. Allocation reversal here is domain behavior on the allocation and the two balances. Phase 5C later persisted that workflow. See ADR 0012.

No EF Core or HTTP dependency is allowed in domain code.

## Phase 2A: ConfirmAllocation application slice

Status: Complete.

Delivered:

- `Result<T>` in SharedKernel;
- `ConfirmAllocation` command handler;
- specialized command repositories for invoice, bank transaction, and allocation;
- Application `IUnitOfWork` commit port;
- trusted `ICurrentOrganizationContext`;
- unit tests for application coordination.

Persistence and HTTP were deferred from this slice to Phase 2B. Phase 4C later made `ConfirmAllocationHandler` prevalidate both aggregates before either balance changes. The handler still accepts a caller-supplied amount, including a partial amount, and it is not an HTTP endpoint.

## Phase 2B.1: Reconciliation persistence foundation

Status: Complete.

Delivered:

- EF Core SQL Server;
- module-owned `ReconciliationDbContext`;
- SQL schema `reconciliation`;
- mappings and migrations;
- specialized command repositories;
- organization-scoped lookups;
- decimal precision;
- composite tenant foreign keys;
- integration tests against SQL Server.

ConfirmAllocation was the first persisted write use case. One `SaveChangesAsync` remains the transaction boundary for later financial writes as well.

## Phase 2B.2: Optimistic concurrency

Status: Complete.

Delivered:

- SQL Server `rowversion` shadow properties on `Invoice` and `BankTransaction`;
- `ConcurrencyConflictException` at the Application `IUnitOfWork` boundary;
- ConfirmAllocation mapping of that conflict to `Result.Failure`;
- SQL Server integration coverage for stale-version detection and atomic rollback.

Phase 4C maps `rowversion` and idempotency conflicts to HTTP 409 for `POST /api/matches/confirm`. Phase 4D does the same for `POST /api/allocations`. Phase 5C maps reversal conflicts to HTTP 409, including `Allocation` `rowversion` conflicts. Automatic retry remains out of scope.

## Phase 2B.3: First HTTP vertical slice

Status: Complete.

First complete HTTP use case:

```text
GET /api/invoices
```

Delivered:

- JWT Bearer access-token validation in the API host, without token issuance;
- separate general authentication and `TenantMember` authorization;
- production `ICurrentOrganizationContext` from the authenticated organization claim;
- paginated, organization-scoped invoice list projection;
- HTTP 200 and invalid-pagination HTTP 400;
- SQL Server query tests and JWT HTTP integration tests.

Exact-match confirmation over HTTP, including HTTP 409 for that endpoint, followed in Phase 4C. Explicit-amount allocation followed in Phase 4D. Confirmation audit is Phase 5B. Persisted reversal is Phase 5C. Phase 5 records the delivered manual allocation workflow. Roles and the browser BFF remain Phase 7. See ADR 0004.

## Phase 3: Imports and idempotency

Status: Complete.

Synchronous CSV import for invoices and bank transactions is delivered. Background processing remains Phase 10.

### 3A: Import domain

Status: Complete.

`ImportBatch`, source identity, file hash, row outcomes, and row errors.

### 3B: Import persistence

Status: Complete.

Import batches and row errors are mapped in the `reconciliation` schema. File replay is enforced by `UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash`. Source identity is enforced by filtered unique indexes on invoices and bank transactions.

### 3C: Idempotent CSV import workflow

Status: Complete.

`POST /api/imports/invoices` and `POST /api/imports/bank-transactions` validate rows, reject duplicates, and replay an identical file. Upload size limits are enforced on these endpoints. SQL Server integration tests cover persistence, replay, and concurrent import.

## Phase 4: Deterministic matching

Status: Complete.

Approved sequence. Phase 4A, Phase 4B, Phase 4C, Phase 4D, Phase 4E, Phase 4F, and Phase 4G are complete. Phase 4C is on `main` (pull request 21, `fb96d4d`).

### 4A: Domain Rules & Matching Contract

Status: Complete.

Strict `exact-1` reference normalization, exact 1:1 classification, and unit tests. Results are not persisted. See ADR 0005.

### 4B: Exact Matching Engine

Status: Complete.

`GET /api/matches` is a read-only advisory `exact-1` engine for the authenticated organization's open invoices and open bank transactions. Each side is limited to 5,000 open rows. A larger open book returns HTTP 409 `reconciliation.matches.candidate_limit_exceeded` and does not match a partial set. Stored amounts that break the invoice or bank-transaction money invariants return HTTP 500 `reconciliation.matches.inconsistent_money`. Results are not persisted. A caller may confirm one selected pair through the Phase 4C workflow. The match response is not an authorization token.

### 4C: Safe Match Confirmation

Status: Complete.

`POST /api/matches/confirm` confirms one explicitly selected invoice and bank transaction for the authenticated organization. It is not automatic reconciliation. The amount is the full shared current open amount. The pair must still match on `exact-1` reference, currency, and positive open amount. The body accepts only `invoiceId` and `bankTransactionId`. Global uniqueness is not guaranteed, competing rows are not locked, and an explicitly named ambiguous pair can be confirmed. A required `Idempotency-Key` UUID is stored on the allocation and enforced by `UX_Allocations_OrganizationId_IdempotencyKey`. `rowversion` conflicts are returned as HTTP 409. The implementation is on `main` (pull request 21, `fb96d4d`). See ADR 0006.

### 4D: Partial & Multi-Allocation

Status: Complete.

`POST /api/allocations` confirms one explicit amount between one invoice and one bank transaction for the authenticated organization. References do not need to match. Each request is its own atomic commit. One invoice can be paid by several bank transactions, and one bank transaction can be allocated across several invoices, by repeating that request. Those calls are not an atomic batch. There is no batch endpoint and no automatic reconciliation. The amount must be a positive JSON number that fits `decimal(19,4)` without rounding. Idempotency is organization-scoped and operation-aware: exact match is `ExactMatch` and this route is `ExplicitAmount`. See ADR 0007.

Exact full-open-amount confirmation stays on `POST /api/matches/confirm`. `ConfirmAllocationHandler` remains an internal application command. Persisted reversal, immutable audit attribution, and finer authorization are not part of this phase.

### 4E: Smart Matching & Explainability

Status: Complete.

`GET /api/matches/smart` returns advisory `smart-1` suggestions for the authenticated organization's open book. It reuses the exact-match candidate query, including the 5,000-row limit and the stored-money checks. Reference signals do not stack. `ReferenceExact` is 70. A qualified reference signal is 60. An equal open amount adds 25. Currency is a hard filter. The suggested amount is the smaller open amount and does not authorize an allocation. `contested` and `exactMatchAmbiguous` are indicators. `coverageLimited` and `truncated` report incomplete candidate generation or output. Scores are points, not probabilities. `exact-1`, `POST /api/matches/confirm`, and `POST /api/allocations` are unchanged. A later change to scoring or normalization must use a new version such as `smart-2`. Review queues and automatic confirmation remain later phases. See ADR 0008.

### 4F: Exception & Review Workflow

Status: Complete.

Manual review cases are persisted for the authenticated organization. `POST /api/review-cases` opens a case for `CompetingMatch` or `NeedsInvestigation`. Repeating that request for the same open subject returns the existing case. `GET /api/review-cases` lists the organization's cases with page size 1 through 100, default 50, ordered by opened time descending and then review case id descending. `GET /api/review-cases/{id}` returns the case and the current linked balances. `POST /api/review-cases/{id}/resolve` and `POST /api/review-cases/{id}/dismiss` close an open case when the caller sends the current Base64 `rowversion` token. An identical terminal replay succeeds without changing `ClosedAt` or the token. A different terminal decision conflicts. `AllocationRecorded` requires a confirmed allocation that covers the case. Review endpoints do not confirm allocations, do not run `exact-1` or `smart-1`, and do not persist `MatchSuggestion`. See ADR 0009.

### 4G: Batch Auto-Matching & Hardening

Status: Complete.

`GET /api/matches/batch/preview` is a read-only `exact-1` classification of the authenticated organization's open book. Eligible pairs are paged after that classification. The preview is not stored and does not authorize an allocation. `POST /api/matches/batch/execute` is operator-approved: the caller submits 1 to 25 invoice and bank-transaction pairs with one `Idempotency-Key`. The server revalidates each pair and confirms only a unique exact-1 full current remainder. A completed receipt freezes the result. Replay does not allocate again. Each item commits in its own transaction. `smart-1` stays advisory and cannot allocate. `TenantMember` can start a batch. Finer financial authorization and complete OpenAPI Bearer metadata remain Phase 7. There is no background worker. Confirmation audit for these commits was added in Phase 5B. See ADR 0010 and ADR 0011.

Do not add AI before the deterministic decision path is observable and testable. AI must not confirm allocations.

## Phase 5: Allocation workflow and audit

Status: Complete.

Phase 5B records confirmation audit for exact-match confirmation, explicit-amount allocation, and operator-approved batch exact matching. Phase 5C records full reversal of one allocation. The delivered workflow, the deferred suggestion lifecycle, and the idempotency boundary are stated after Phase 5C. Finer authorization remains Phase 7.

### 5B.1: Financial audit persistence

Status: Complete.

`FinancialAuditEntry` is stored in the `reconciliation` schema. A confirmation entry copies the allocation identity, amount, currency, confirmation time, idempotency operation, and idempotency key. `BatchExecutionId` is required only for `BatchExactMatch`. The table has tenant-consistent composite foreign keys, a unique confirmation constraint, and an append-only trigger. There is no historical backfill. See ADR 0011.

### 5B.2: Exact and explicit confirmation audits

Status: Complete.

`ConfirmExactMatchHandler` and `ConfirmExplicitAmountHandler` require a trusted JWT `sub`, append one confirmation audit entry, and commit it with the financial changes. An idempotent replay does not append another entry. A missing or invalid actor returns HTTP 403 `reconciliation.actor.required`.

### 5B.3: Batch exact-match confirmation audits

Status: Complete.

Each newly committed batch item appends one confirmation audit entry in that item's transaction. The actor is the operator who committed the item. `BatchExecutionId` is the persisted receipt id. Recovery of an already committed item, including a historical allocation with no audit row, does not append a confirmation. A completed receipt replay does not write.

### 5B.4: Financial audit read API and documentation

Status: Complete.

`GET /api/allocations/{allocationId}/audit` returns the authenticated organization's audit page for one allocation. Page defaults to 1 and page size defaults to 50, with a maximum of 100. Ordering is `RecordedAt` ascending, then audit entry id ascending. A historical allocation with no audit row returns an empty page. The read does not require `sub`. ADR 0011 records the foundation.

### 5B.5: Final regression and release review

Status: Complete.

Cumulative regression of Phase 5B.1 through 5B.4, financial consistency, tenant isolation, idempotency, batch recovery, and documentation consistency have been reviewed.

Migration `20261028002321_AddFinancialAuditEntries` was verified against isolated pre-audit financial data, including historical allocations and batch execution receipts.

This increment introduces no new financial behavior.

Phase 5B is complete.

### 5C: Allocation reversal

Status: Complete.

`POST /api/allocations/{allocationId}/reverse` reverses one confirmed allocation for its full original amount. The allocation row remains. Invoice and bank-transaction allocated balances are restored by that amount. Confirmation identity is unchanged. One reversal audit entry is written in the same `SaveChangesAsync`. Confirmation and reversal idempotency keys are separate namespaces. `Allocation` has its own `rowversion`. See ADR 0012.

A batch receipt records the historical confirmation outcome. A later reversal does not rewrite a Completed receipt. Recovery of a Running receipt may return a currently reversed allocation as historically confirmed, with the original allocation id and amount, and without a new confirmation audit. There is no `ConfirmedThenReversed` outcome.

Migration `20261028221955_AddAllocationReversalPersistence` adds the allocation token, the nullable reversal key, and the reversal audit constraints. Its Down is rejected while a reversal audit row or a non-null reversal key exists.

The delivered allocation workflow is:

- exact-1 confirmation through `POST /api/matches/confirm`;
- explicit-amount allocation through `POST /api/allocations`, including a partial amount;
- several separate allocations against the same invoice;
- several separate allocations against the same bank transaction;
- batch exact-match execution through `POST /api/matches/batch/execute`;
- full reversal of one existing allocation through `POST /api/allocations/{allocationId}/reverse`.

Repeated explicit-amount calls are independent, idempotent financial transactions. They are not one atomic command that allocates several arbitrary amounts together.

Phase 5 does not include partial reversal of one allocation, one atomic request containing several arbitrary allocation amounts, cross-currency allocation, or direct confirmation by a persisted `MatchSuggestion` identifier.

`smart-1` suggestions are advisory and read-only. `MatchSuggestion` exists as a domain model and has no persisted EF Core lifecycle. Confirmation commands reload and validate the current financial records. Persisting a suggestion lifecycle is not required to complete Phase 5 and is not implemented. A later phase may revisit that persistence if a genuine business requirement appears.

Exact-match confirmation, explicit-amount confirmation, batch exact-match execution, and full allocation reversal already have tenant-scoped idempotency and HTTP conflict handling under their existing persistence rules. Import repetition uses the file hash and source identity. Review-case repetition uses the open subject key and `rowversion`. Those mechanisms are separate from allocation command keys. This phase does not add a generic idempotency framework, and it does not leave idempotency or conflict mapping unfinished for commands that do not exist.

Finer authorization, including resource-specific permissions and who may read audit `ActorId`, remains Phase 7. Authentication and authorization follow-ups already listed under Phase 7 stay open.

## Phase 6: React operations workspace

Status: Planned.

Create the React and TypeScript application with Vite.

Add:

- application shell;
- routing;
- API client;
- TanStack Query;
- MUI theme;
- invoice list and details;
- bank transaction list;
- reconciliation work queue;
- allocation history;
- allocation dialog;
- loading, error, empty, forbidden, and conflict states;
- component tests.

The first frontend increment is an invoice register on `GET /api/invoices`. That response already contains the fields shown for the selected row. A separate invoice-detail endpoint does not exist, so an addressable invoice detail screen is not part of this increment. `GET /api/bank-transactions` is the tenant-scoped register of current bank-transaction balances, ordered by bank transaction id and paged like the invoice list. It includes unallocated, partially allocated, fully allocated, and reversed balances. A page that contains inconsistent stored money returns HTTP 500 `reconciliation.bank_transactions.inconsistent_money` and no amounts. Optional exact filters are `invoiceNumber` and `reference`. They are trimmed, limited to 256 characters, and compared with SQL equality under the column collation. There is no search index. A missing filter keeps the full paged list, so a null bank reference remains reachable. The invoice and bank transaction register screens do not expose search controls and do not send these filters. `GET /api/allocations/context` returns the current business context of one named invoice and one named bank transaction. It does not reserve funds. The Reconciliation screen can allocate an explicit amount for a Smart-1 pair through `POST /api/allocations`, and it can retry one stored attempt without requiring that context read to succeed. **Choose records** on that screen can also select an invoice and a bank transaction that are not a Smart-1 suggestion, using the optional exact list filters or an unfiltered page, and then opens the same allocation dialog and its existing recovery. The register screens do not start that allocation. `GET /api/allocations` is the tenant-scoped read of persisted allocations, including reversed rows. It orders by confirmation time and allocation id, accepts optional exact `invoiceId` and `bankTransactionId` filters, and does not join the audit trail. The operations workspace shows that read at `/allocations`. `GET /api/allocations/{allocationId}/reversal-context` reads the current persisted state of one allocation for a later reversal screen. It returns the original amount, current status, and current invoice and bank labels. It does not return `rowversion`, idempotency keys, audit, or balances, and it does not reserve the row. `POST /api/allocations/{allocationId}/reverse` still does not accept a client concurrency token. Invoice details, the reversal screen, and production browser authentication remain deferred.

The CSV import workspace posts the operator's original file to `POST /api/imports/invoices` or `POST /api/imports/bank-transactions` and shows that response, including replay and row errors. A list of earlier batches, a batch read by id, and an error-file download are not part of this increment because those endpoints do not exist. They remain out of scope until a separate read API is approved. The bank-transaction register is `GET /api/bank-transactions` and `/bank-transactions`. The API accepts an optional exact `reference` filter, and `GET /api/invoices` accepts an optional exact `invoiceNumber` filter. The invoice and bank transaction register screens do not expose that lookup. The Reconciliation screen does, through **Choose records**, including unfiltered browsing. That selection reuses the existing allocation dialog and recovery. Date filters, substring search, and the reversal screen remain deferred. Phase 6H.1 supplies only `GET /api/allocations/{allocationId}/reversal-context`. The browser reversal workflow remains Phase 6H.2.

## Phase 7: Authentication, authorization, and tenant isolation

Status: Planned.

Phase 2B.3 already validates JWT access tokens and requires an organization claim on Reconciliation routes. This phase adds the browser session and finer authorization.

Add:

- OpenID Connect BFF authentication;
- secure session cookie;
- CSRF protection;
- organization membership;
- roles;
- policies;
- resource-based authorization;
- cross-organization integration tests;
- BOLA/IDOR tests;
- OpenAPI Bearer security metadata for existing `TenantMember` routes, including `GET /api/matches`, `GET /api/matches/smart`, and the Phase 4F review-case routes. Those routes already enforce `TenantMember` at runtime. The generated document does not yet describe that requirement.

## Phase 8: Disputes and dashboards

Status: Planned.

Add:

- Disputes module;
- case lifecycle;
- assignment;
- comments;
- resolution;
- audit history;
- operational dashboard read models.

## Phase 9: Performance and operability

Status: Planned.

Phase 3C already limits import upload size. This phase adds further operability and hardening.

Add:

- structured logging;
- correlation identifiers;
- query-plan notes;
- targeted indexes;
- one keyset-paginated work queue;
- selected rate limits beyond the import upload limits;
- readiness checks beyond the existing `/health` endpoint;
- security review.

## Phase 10: Optional infrastructure

Status: Optional / Not started.

Only after a demonstrated trigger:

- outbox and message broker;
- durable background jobs;
- SignalR;
- Redis;
- full Docker Compose.

## Phase 11: Optional AI feature

Status: Optional / Not started.

Preferred first feature: AI Match Explanation.

It may convert deterministic reason codes into concise business language. It must not confirm allocations or mutate financial state.
