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

The MVP uses shared-database, shared-schema multi-tenancy.

Every tenant-owned row contains `OrganizationId`.

The effective organization is derived from authenticated server-side membership and is applied to:

- queries;
- commands;
- entity creation;
- authorization;
- unique constraints;
- audit records;
- idempotency keys;
- tests.

A client-supplied organization identifier is never authoritative.

## Persistence

The write side uses tracked aggregates and explicit business transaction boundaries.

The read side uses direct EF Core projections to endpoint-specific DTOs, normally with `AsNoTracking`.

`DbContext` supplies change tracking and unit-of-work behaviour. A generic repository is not planned.

SQL Server-specific behaviour, including `rowversion`, provider constraints, and locking, must be tested against SQL Server rather than replaced with SQLite.

## Authentication direction

The planned browser architecture is a backend-for-frontend pattern with OpenID Connect.

The backend acts as the confidential OIDC client and maintains the authenticated browser session in a secure HTTP-only cookie. The React application calls same-origin API endpoints. Mutating cookie-authenticated requests require CSRF protection.

Future machine-to-machine endpoints may use a separate JWT bearer scheme with narrow scopes.

## Infrastructure triggers

RabbitMQ or another broker is considered only when a reliable event must be consumed by an independently deployed process. An outbox is introduced before broker publication.

Redis is considered after measurement demonstrates a distributed caching or shared-state requirement.

SignalR is considered when users need live import progress or collaborative work-queue updates and polling is no longer appropriate.

Background jobs are considered when imports or matching exceed a reasonable HTTP request duration or require durable scheduling and retries.
