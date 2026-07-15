# AGENTS.md

These are repository-wide instructions for Cursor, Codex, and human contributors.

## Read before changing code

Read:

1. `README.md`
2. `docs/architecture.md`
3. `docs/development-guidelines.md`
4. relevant records under `docs/adr`

For any non-trivial task, first explain:

- intended product behaviour;
- affected module and layer;
- business assumptions;
- consistency and transaction impact;
- authorization and tenant-isolation impact;
- planned tests;
- files expected to change.

Do not edit files until the plan is accepted when the task is architectural or spans multiple layers.

## Repository boundaries

- `FinOpsDesk.Api` is the host and composition root. It must not contain business rules.
- `FinOpsDesk.SharedKernel` must remain small and stable.
- A module represents a business capability, not a database table.
- The Reconciliation module owns invoices, bank transactions, match suggestions, and allocations.
- Modules must not reference the API host.
- A module must not access another module's internal database tables or internal types directly.
- Cross-module interactions require explicit contracts or events.
- Do not add projects, abstractions, or packages only to satisfy a pattern name.

## Domain rules

Domain code must not reference:

- ASP.NET Core;
- EF Core;
- MediatR;
- logging frameworks;
- authentication-provider types;
- HTTP requests, responses, or transport DTOs;
- Infrastructure or Presentation namespaces.

Keep invariants inside domain types. Avoid public setters on aggregates.

Financial amounts use `decimal`, never `float` or `double`.

Do not invent undocumented business rules. State assumptions explicitly.

## Application rules

- Commands change state.
- Queries do not change state.
- MediatR may be used at application boundaries, not as a replacement for normal method calls.
- Expected business failures must not rely on unhandled exceptions.
- Cancellation tokens must flow from endpoint to handler and database call.
- Application contracts belong near the code that requires them.

## Persistence rules

- Use EF Core directly.
- Do not add a generic repository or redundant unit-of-work abstraction.
- Query handlers should normally use `AsNoTracking` and direct DTO projections.
- All list queries require bounded pagination and deterministic ordering.
- Tenant-owned data must be scoped by `OrganizationId`.
- Important mutable financial records use SQL Server `rowversion`.
- Idempotency must be enforced by a database uniqueness constraint, not only an application-side existence check.
- Do not edit an applied migration. Create a new migration.

## Security and tenancy

- Never trust an `OrganizationId` supplied by the client.
- Resolve user and organization from the authenticated server-side context.
- Every tenant-owned read and write must be organization-scoped.
- A global query filter may be defense in depth, but it is not the only security boundary.
- Do not bind HTTP request models directly to entities.
- Do not log secrets, tokens, full bank account numbers, or unrestricted request bodies.
- Every new state-changing endpoint requires an explicit authorization decision.
- Sensitive resource endpoints require BOLA/IDOR integration tests.

## Financial consistency

- Confirming or reversing an allocation must be atomic.
- Cross-currency allocation is outside the MVP.
- Allocation amount must be positive.
- A confirmed allocation must not exceed invoice outstanding amount or transaction unapplied amount.
- Confirmed financial operations are reversed, not hard-deleted.
- Audit records for financially relevant changes must be written in the same transaction.

## Frontend rules

When the frontend is added:

- organize code by feature;
- use TanStack Query for server state;
- use React Hook Form and Zod for forms and validation;
- do not copy query data into local component state without a deliberate draft model;
- do not introduce Redux without a demonstrated cross-feature client-state problem;
- handle loading, error, empty, forbidden, conflict, and success states deliberately;
- do not store access tokens in `localStorage` or `sessionStorage`.

## Verification

Before completing a backend change, run:

```bat
dotnet build FinOpsDesk.slnx
dotnet test FinOpsDesk.slnx
dotnet format --verify-no-changes
```

Report commands that were run and their results. Report incomplete verification honestly.

## AI-assisted development

AI output is a draft, not authority.

- Keep changes narrow and reviewable.
- Do not perform unrelated cleanup.
- Do not generate broad refactors without an accepted plan.
- Never weaken a test merely to make it pass.
- Do not add packages without explaining why the existing platform is insufficient.
- Do not change business behaviour silently.
- Summarize changed files, decisions, assumptions, and verification results.
- Ask before making a destructive Git or database operation.

## Definition of done

A change is complete only when:

- intended behaviour is implemented;
- relevant tests pass;
- security and tenant isolation were considered;
- consistency and failure behaviour were considered;
- documentation is updated when the decision or workflow changed;
- no secrets or generated build artifacts are committed;
- the diff contains no unrelated changes.
