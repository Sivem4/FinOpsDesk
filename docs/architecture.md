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

Phase 2B.2 adds SQL Server `rowversion` concurrency tokens as EF Core shadow properties on `Invoice` and `BankTransaction`. `IUnitOfWork.SaveChangesAsync` translates `DbUpdateConcurrencyException` into Application `ConcurrencyConflictException`. `ConfirmAllocationHandler` maps that abstraction to `Result.Failure`. There is no automatic retry; a failed DbContext is discarded with its scope. HTTP 409 mapping remains future work.

Phase 2B.3 adds the first HTTP read slice, `GET /api/invoices`. The API host validates JWT Bearer access tokens and does not issue them. A fallback authorization policy requires an authenticated user. The Reconciliation route group uses a separate `TenantMember` policy: an authenticated user plus a configured organization claim that parses to a non-empty Guid. `/health` stays anonymous. The API host implements `ICurrentOrganizationContext` from that claim. `ListInvoicesHandler` reads the organization once and passes it to `IInvoiceQueries`. The query is an organization-scoped, no-tracking DTO projection with bounded pagination. The invoice endpoint maps invalid pagination to HTTP 400. See ADR 0004.

Phase 3C adds synchronous CSV import at `POST /api/imports/invoices` and `POST /api/imports/bank-transactions`. Both require `TenantMember`. The client sends `file` and `sourceSystem`. Organization id comes only from the authenticated claim. A structurally valid file commits one `ImportBatch` with its accepted financial rows and row errors. An exact file-hash replay returns the stored batch. Unique-key collisions retry on a fresh persistence context. The database uniqueness constraints remain the final authority.

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

## Data consistency

A transaction boundary follows one business operation.

Confirming an allocation must update the invoice financial state, bank transaction financial state, allocation record, relevant suggestion state, and audit record atomically.

Confirmed financial operations are reversed rather than deleted.

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

Command repositories load aggregates tracked by the scoped `ReconciliationDbContext`. `ReconciliationDbContext` implements Application `IUnitOfWork`. ConfirmAllocation persists invoice, bank transaction, and allocation changes with one `SaveChangesAsync` call. EF Core/SQL Server supplies the transaction for that single SaveChanges. Explicit `BeginTransaction` is not used for this use case.

The read side uses direct EF Core projections to endpoint-specific DTOs, normally with `AsNoTracking`. `GET /api/invoices` is the first such query: tenant-scoped, ordered by invoice id, and limited to a page size of 1 through 100.

A generic repository is not planned. A second unit-of-work wrapper over `DbContext` is not planned.

SQL Server-specific behaviour, including `rowversion`, provider constraints, and locking, must be tested against SQL Server rather than replaced with SQLite.

`Invoice` and `BankTransaction` use SQL Server `rowversion` as EF Core shadow properties. ConfirmAllocation updates those aggregates and inserts `Allocation` in one `SaveChangesAsync`. A stale version fails the whole commit. `Allocation` does not have a concurrency token yet; that can be revisited when a persisted reversal updates an existing allocation. Domain entities do not expose `RowVersion`.

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
