# Roadmap

## Phase 0: Repository foundation

Status: in progress.

Deliverables:

- solution and project structure;
- API host;
- Reconciliation module entry point;
- health endpoint;
- OpenAPI in Development;
- initial architecture test;
- repository-wide build settings;
- project documentation;
- coding-agent instructions;
- continuous integration.

## Phase 1: Reconciliation domain

Implement:

- currency representation;
- money representation;
- invoice aggregate;
- bank transaction aggregate;
- allocation model;
- match suggestion model;
- domain result and error conventions.

Cover with unit tests:

- partial payment;
- multiple payments to one invoice;
- one payment allocated to multiple invoices;
- unapplied overpayment;
- invalid amount;
- cross-currency rejection;
- allocation reversal.

No EF Core or HTTP dependency is allowed in domain code.

## Phase 2: Persistence and first vertical slice

Add:

- EF Core SQL Server;
- Reconciliation DbContext;
- mappings and migrations;
- organization scope;
- decimal precision;
- unique constraints;
- indexes;
- SQL Server `rowversion`;
- integration test infrastructure.

First complete use case:

```text
GET /api/invoices
```

It should include a paginated DTO projection, organization scope, HTTP mapping, and integration test.

## Phase 3: Imports and idempotency

Implement:

- invoice CSV import;
- bank transaction CSV import;
- import batches;
- row-level validation errors;
- source identity or file hash;
- database-enforced duplicate protection.

Keep imports synchronous while deliberately small. Introduce background processing only after measurement or durability requirements justify it.

## Phase 4: Deterministic matching

Implement:

- normalized references;
- deterministic rules;
- weighted score;
- reason codes;
- algorithm version;
- suggestion generation;
- accept and reject decisions.

Do not add AI before the deterministic decision path is observable and testable.

## Phase 5: Allocation workflow and audit

Implement:

- manual allocation;
- suggestion confirmation;
- atomic financial state updates;
- reversal with reason;
- audit entry in the same transaction;
- optimistic concurrency conflict handling;
- idempotency for selected commands.

## Phase 6: React operations workspace

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

Add:

- Disputes module;
- case lifecycle;
- assignment;
- comments;
- resolution;
- audit history;
- operational dashboard read models.

## Phase 9: Performance and operability

Add:

- structured logging;
- correlation identifiers;
- query-plan notes;
- targeted indexes;
- one keyset-paginated work queue;
- selected rate limits;
- import size limits;
- readiness checks;
- security review.

## Phase 10: Optional infrastructure

Only after a demonstrated trigger:

- outbox and message broker;
- durable background jobs;
- SignalR;
- Redis;
- full Docker Compose.

## Phase 11: Optional AI feature

Preferred first feature: AI Match Explanation.

It may convert deterministic reason codes into concise business language. It must not confirm allocations or mutate financial state.
