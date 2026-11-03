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

Status: Completed.

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

The first frontend increment is an invoice register on `GET /api/invoices`. That response already contains the fields shown for the selected row. A separate invoice-detail endpoint does not exist, so an addressable invoice detail screen is not part of this increment. `GET /api/bank-transactions` is the tenant-scoped register of current bank-transaction balances, ordered by bank transaction id and paged like the invoice list. It includes unallocated, partially allocated, fully allocated, and reversed balances. A page that contains inconsistent stored money returns HTTP 500 `reconciliation.bank_transactions.inconsistent_money` and no amounts. Optional exact filters are `invoiceNumber` and `reference`. They are trimmed, limited to 256 characters, and compared with SQL equality under the column collation. There is no search index. A missing filter keeps the full paged list, so a null bank reference remains reachable. The invoice and bank transaction register screens do not expose search controls and do not send these filters. `GET /api/allocations/context` returns the current business context of one named invoice and one named bank transaction. It does not reserve funds. The Reconciliation screen can allocate an explicit amount for a Smart-1 pair through `POST /api/allocations`, and it can retry one stored attempt without requiring that context read to succeed. **Choose records** on that screen can also select an invoice and a bank transaction that are not a Smart-1 suggestion, using the optional exact list filters or an unfiltered page, and then opens the same allocation dialog and its existing recovery. The register screens do not start that allocation. `GET /api/allocations` is the tenant-scoped read of persisted allocations, including reversed rows. It orders by confirmation time and allocation id, accepts optional exact `invoiceId` and `bankTransactionId` filters, and does not join the audit trail. The operations workspace shows that read at `/allocations`. `GET /api/allocations/{allocationId}/reversal-context` reads the current persisted state of one allocation before a new reversal. It returns the original amount, current status, and current invoice and bank labels. It does not return `rowversion`, idempotency keys, audit, or balances, and it does not reserve the row. The Allocation history screen can reverse a confirmed allocation through `POST /api/allocations/{allocationId}/reverse`. The browser stores one reversal attempt separately from the explicit-amount attempt, retries that same request after an unknown outcome, and does not treat a conflict or a lost response as success. `POST /api/allocations/{allocationId}/reverse` still does not accept a client concurrency token. Invoice details and production browser authentication remain deferred.

The CSV import workspace posts the operator's original file to `POST /api/imports/invoices` or `POST /api/imports/bank-transactions` and shows that response, including replay and row errors. A list of earlier batches, a batch read by id, and an error-file download are not part of this increment because those endpoints do not exist. They remain out of scope until a separate read API is approved. The bank-transaction register is `GET /api/bank-transactions` and `/bank-transactions`. The API accepts an optional exact `reference` filter, and `GET /api/invoices` accepts an optional exact `invoiceNumber` filter. The invoice and bank transaction register screens do not expose that lookup. The Reconciliation screen does, through **Choose records**, including unfiltered browsing. That selection reuses the existing allocation dialog and recovery. Date filters and substring search remain deferred. The Allocation history screen reverses one confirmed allocation at a time. Phase 6H.1 supplies `GET /api/allocations/{allocationId}/reversal-context`. The browser stores the reversal request separately from an explicit-amount attempt and replays that same request after an unknown outcome.

Phase 6I.1 adds a local browser journey in `tests/FinOpsDesk.E2ETests`. That project is outside `FinOpsDesk.slnx` and is not a pull-request check. It runs Chromium against the Vite development server, a loopback API, and a disposable `FinOpsDesk_E2E_<guid>` database. The journey imports one valid invoice and one rejected invoice row, imports the matching bank transaction, replays the same invoice file, confirms the single exact-1 pair, and checks allocation history plus the confirmation audit in SQL Server. Phase 6I.2 adds browser coverage for a successful reversal, a reversal whose HTTP response is dropped after SQL Server commits, manual replay of that same request, one explicit-amount recovery, one cross-tenant recovery check, and coexistence of the two stored attempt keys. Those tests stay outside `FinOpsDesk.slnx` and outside CI.

Phase 6 is completed. The delivered workspace is a React, TypeScript, and Vite application with an MUI shell and routing, an API client, and TanStack Query. Sign-in is a development-only JWT access gate. The token stays in browser memory, and a production build does not accept that gate. The workspace includes the invoice register, the bank-transaction register, and a CSV import workspace for both files, including validation results and idempotent replay. Reconciliation shows Exact-1 candidates and advisory Smart-1 suggestions with their signals. The operator can choose a pair that is not a suggestion, allocate an explicit amount, read allocation history and its financial audit, and reverse one confirmed allocation after `GET /api/allocations/{allocationId}/reversal-context`. An unknown explicit-amount outcome and an unknown reversal outcome keep separate browser descriptors. A retry sends the original request body and the original `Idempotency-Key`, and only after the operator chooses that retry. A descriptor stored for another organization stays hidden. Local Playwright coverage of the core financial journey and of that recovery is described in `e2e-testing.md`.

This completion does not make the application production-ready. Production browser authentication, organization membership, roles, resource authorization, and OpenAPI Bearer metadata remain Phase 7. Phase 7 is the next phase.

The approved omissions stay outside Phase 6. There is no invoice-detail endpoint or page. Import batch history and an error-file download stay out of scope until a separate read API is approved. Register screens still do not send date filters or substring search. The ReviewCase API has no disputes workspace or dashboard; those remain Phase 8. Structured logging, performance work, and optional infrastructure remain Phases 9 and 10.

The Phase 6 closure review accepted the following non-blocking debt and did not assign it a new phase. The first three items are P2. The last two are P3.

- The E2E console filter for `ERR_FAILED` and `ERR_ABORTED` is wider than the intercepted financial POST.
- A verified reversal replay can also show the warning that the allocation is already reversed.
- The Playwright startup timeout in `E2EHost.WithinAsync` does not cancel an outstanding startup task.
- Playwright browser E2E is not a mandatory CI check.
- The Vite production build can warn that a chunk exceeds 500 kB. Bundle optimization is not part of Phase 6.

## Phase 7: Authentication, authorization, and tenant isolation

Status: Planned.

Phase 2B.3 already validates JWT access tokens and requires an organization claim on Reconciliation routes. This phase adds the browser session and finer authorization.

Phase 7A selects the host composition. `DevelopmentJwt` remains available only for the `Development` and `E2E` environments and keeps the existing JWT bearer handler. Every other environment uses `ProductionCookie`: cookie authentication as the default scheme, no JWT bearer handler, and no acceptance of an `Authorization` bearer token.

Phase 7B.1 adds the confidential OpenID Connect authorization-code handler, `POST /login`, the `/signin-oidc` callback, the `__Host-finopsdesk-session` cookie, `GET /api/session`, local `POST /logout`, and CSRF enforcement for unsafe `ProductionCookie` requests. Financial routes stay fail-closed. The session does not carry an organization id or a role. Provider end-session is not part of this phase. A production identity provider is not deployed from this repository. Normal CI uses an in-process protocol stub and does not call Keycloak or Entra ID.

Phase 7B.2 connects the React application to that cookie session. Development stays on `DevelopmentJwt` unless Vite is started in `cookie` mode. A production build always uses the cookie session and does not include the development token screen. Sign-in is a browser form POST. The restricted screen is shown while `financialAccess` is false, and the financial workspace is not mounted. Local sign-out does not end the provider session. A recovery descriptor left in `sessionStorage` is not shown or submitted. Identity-aware recovery, beyond the organization id already stored in that descriptor, is deferred until membership exists and two identities can share one browser. Financial access stays closed.

Phase 7C.1 persists organization membership in the Access module. A completed OIDC callback creates an application user for the exact issuer and subject and does not create a membership. `GET /api/session` can list active memberships. It does not select an active organization. Financial routes stay fail-closed. See ADR 0014.

Phase 7C.2 validates that membership on each cookie request. `finopsdesk_org` remembers an explicit `POST /api/session/organization` choice and is not itself authorization. A disabled or missing application user is signed out, and the old ticket does not survive a later restore. A SQL failure returns HTTP 503 and keeps the unexpired cookie. Switching organizations does not extend the eight-hour lifetime. CSRF additional data is bound to the exact issuer and subject. Financial routes stay fail-closed. See ADR 0015.

Phase 7C.3 shows that choice in the cookie screen. The browser does not treat one membership, or HTTP 204, as the active organization. The following session read is authoritative. A verified organization change clears cached queries. Other tabs, focus, and visibility only trigger a new read. Cookie-mode recovery descriptors stay blocked. `DevelopmentJwt` is unchanged.

Phase 7D.1 persists one `BrowserSession` per completed OIDC login and copies its id to `finopsdesk_sid`. Ordinary logout revokes that row before HTTP 204 and does not rotate `SessionStamp` or sign out another browser. A cookie without that id is rejected. Organization selection keeps the same id and the original eight-hour deadline, so a late `Set-Cookie` cannot revive a revoked row. SQL unavailability stays HTTP 503 and does not clear an unexpired cookie. Financial routes stay fail-closed. See ADR 0016.

Phase 7D.2 stores organization-scoped role assignments and a fixed permission map. A membership may hold several roles. Their permissions are the union, and none means an empty set. `OrganizationAdmin` can manage membership and roles and does not receive financial permissions. Login does not grant a role. The first administrator is assigned by the `FinOpsDesk.AccessBootstrap` console, which requires an explicit `--operator` id and is not an HTTP endpoint. Cookie validation does not read the role table. Permission resolution is a separate request-scoped step. Financial routes stay fail-closed. See ADR 0017.

Phase 7D.3a classifies every current Reconciliation route and evaluates that classification on `TenantMember`. An enabled test host can authorize a cookie GET only for the active organization's current read permission. Cookie POSTs stay forbidden, including for Operator and ReversalApprover. The deployed cookie host does not set `Authentication:FinancialReadAuthorization`, so production financial routes stay fail-closed. `DevelopmentJwt` is unchanged. See ADR 0018.

Phase 7D.3b publishes session contract v1/v2 negotiation, mounts the permission-aware read-only cookie UI, and documents operational activation of `Authentication:FinancialReadAuthorization`. Contract v1 stays compatible with older browser bundles. Contract v2 returns only enabled financial read capabilities. Cookie financial writes stay forbidden. Deployed configuration still leaves the activation key unset until operators enable it after the compatible release. See ADR 0018.

Phase 7D.4a authorizes financial POSTs in the SQL transaction that commits them, including a new `review-cases.open` permission and a Shared/Exclusive SQL application lock. Both `Authentication:FinancialReadAuthorization` and `Authentication:FinancialWriteAuthorization` must be exactly `Enabled`. Deployed configuration sets neither write activation nor a cookie write UI. `DevelopmentJwt` is unchanged. Session contract v2 still lists read capabilities only. See ADR 0019.

Still in this phase:

- identity-bound financial recovery, or recovery remaining disabled;
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
