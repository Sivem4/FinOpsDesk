# ADR 0002: Module-owned Reconciliation DbContext

- Status: Accepted
- Date: 2026-09-04

## Context

FinOpsDesk is a modular monolith with one ASP.NET Core host and one SQL Server database. Phase 2B.1 introduces the first real persistence implementation. The first write use case is ConfirmAllocation in the Reconciliation module.

The persistence design must keep module ownership explicit while remaining practical for a single shared database.

## Decision

Reconciliation persistence is owned by the Reconciliation module:

- `ReconciliationDbContext` lives in the module Infrastructure layer;
- SQL tables use schema `reconciliation`;
- EF Core migrations live in the Reconciliation module;
- the EF migrations history table is stored in schema `reconciliation`;
- the API host remains the composition root and supplies `ConnectionStrings:FinOpsDesk`.

Tenant isolation remains shared-database with `OrganizationId` on tenant-owned rows. The `reconciliation` schema is module ownership, not a schema-per-tenant model.

`ReconciliationDbContext` implements the existing Application `IUnitOfWork` port directly. Command repositories inject that scoped context. ConfirmAllocation continues to call `SaveChangesAsync` once.

## Alternatives considered

### One global FinOpsDeskDbContext

Rejected for the first persistence implementation because later modules would share mappings, migrations, and table ownership in one model.

### Separate Infrastructure assembly

Deferred. The current decision remains one assembly per business module, with Infrastructure as a namespace/folder boundary.

### Extra EfUnitOfWork wrapper

Rejected. It would only forward `DbContext.SaveChangesAsync()`.

## Consequences

Positive:

- Reconciliation mappings and migrations stay with the module that owns the workflow;
- later modules can add their own DbContext and schema without editing Reconciliation tables;
- ConfirmAllocation keeps a single SaveChanges transaction boundary.

Negative:

- multiple module DbContexts will eventually migrate the same physical database;
- internal Domain/Application/Infrastructure boundaries inside the module assembly still rely on convention and review.

Optimistic concurrency with SQL Server `rowversion` is recorded in ADR 0003.
