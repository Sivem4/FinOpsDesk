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

`MatchSuggestion` is a domain model. It is not persisted, and Phase 4 matching does not use it. Allocation reversal here is domain behavior on the allocation and the two balances. A persisted reversal workflow remains Phase 5.

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

Phase 4C maps `rowversion` and idempotency conflicts to HTTP 409 for `POST /api/matches/confirm`. Other allocation commands, including a persisted reversal, still need their own HTTP conflict mapping in Phase 5. Automatic retry remains out of scope.

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

Exact-match confirmation over HTTP, including HTTP 409 for that endpoint, followed in Phase 4C. Broader manual and partial-allocation HTTP workflows remain Phase 4D and Phase 5. Roles and the browser BFF remain Phase 7. See ADR 0004.

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

Status: In progress.

Approved sequence. Phases 4A and 4B are complete. Phase 4C is committed, pending merge. Phases 4D through 4G are planned.

### 4A: Domain Rules & Matching Contract

Status: Complete.

Strict `exact-1` reference normalization, exact 1:1 classification, and unit tests. Results are not persisted. See ADR 0005.

### 4B: Exact Matching Engine

Status: Complete.

`GET /api/matches` is a read-only advisory `exact-1` engine for the authenticated organization's open invoices and open bank transactions. Each side is limited to 5,000 open rows. A larger open book returns HTTP 409 `reconciliation.matches.candidate_limit_exceeded` and does not match a partial set. Stored amounts that break the invoice or bank-transaction money invariants return HTTP 500 `reconciliation.matches.inconsistent_money`. Results are not persisted. A caller may confirm one selected pair through the Phase 4C workflow. The match response is not an authorization token.

### 4C: Safe Match Confirmation

Status: Complete.

`POST /api/matches/confirm` confirms one explicitly selected invoice and bank transaction for the authenticated organization. It is not automatic reconciliation. The amount is the full shared current open amount. The pair must still match on `exact-1` reference, currency, and positive open amount. The body accepts only `invoiceId` and `bankTransactionId`. Global uniqueness is not guaranteed, competing rows are not locked, and an explicitly named ambiguous pair can be confirmed. A required `Idempotency-Key` UUID is stored on the allocation and enforced by `UX_Allocations_OrganizationId_IdempotencyKey`. `rowversion` conflicts are returned as HTTP 409. The implementation is committed on `feature/phase-4c-safe-match-confirmation` and is not yet on `main`. See ADR 0006.

### 4D: Partial & Multi-Allocation

Status: Planned.

HTTP workflows for partial allocation and for two distinct multi-allocation cases: one invoice paid by multiple bank transactions, and one bank transaction allocated across multiple invoices. Phase 4D tests cover that end-to-end multi-invoice scenario. Exact full-open-amount confirmation stays in Phase 4C. `ConfirmAllocationHandler` remains an internal application command.

### 4E: Smart Matching & Explainability

Status: Planned.

Additional matching strategies may add more aggressive reference normalization, weighted scoring, and richer explanations. `exact-1` remains the current strategy. A later strategy should be versioned or separate so `exact-1` is not replaced in place.

### 4F: Exception & Review Workflow

Status: Planned.

### 4G: Batch Auto-Matching & Hardening

Status: Planned.

Do not add AI before the deterministic decision path is observable and testable. AI must not confirm allocations.

## Phase 5: Allocation workflow and audit

Status: Planned.

Phase 4C already confirms one exact pair, commits that allocation and both balances atomically, stores an idempotency key for that command, and maps that endpoint's conflicts to HTTP 409. Phase 4D is the planned HTTP scope for partial and multi-allocation.

This phase covers the remaining allocation lifecycle:

- persisted reversal with reason;
- audit records written in the same transaction as the financial change;
- manual allocation workflows that Phase 4D does not cover;
- persisted confirmation of a `MatchSuggestion`, which remains a domain model only;
- optimistic concurrency for persisted reversal;
- idempotency for financial commands other than exact-match confirmation;
- HTTP conflict mapping for those remaining allocation commands.

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
- allocation dialog;
- loading, error, empty, forbidden, and conflict states;
- component tests.

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
- BOLA/IDOR tests.

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
