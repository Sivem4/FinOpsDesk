# ADR 0001: Use a modular monolith

- Status: Accepted
- Date: 2026-07-15

## Context

FinOpsDesk is finance operations and reconciliation product developed initially by one person.

The first workflows require strong consistency between invoices, incoming bank transactions, match suggestions, allocations, and audit records.

The project should demonstrate explicit business boundaries without introducing the operational and consistency cost of a distributed system before such cost is justified.

## Decision

FinOpsDesk will start as a modular monolith:

- one ASP.NET Core host;
- one deployment unit;
- one SQL Server database;
- explicit business modules;
- module-owned implementation details;
- architecture tests protecting dependency direction;
- in-process communication for initial workflows.

The first business module is Reconciliation.

Invoices, bank transactions, suggestions, and allocations remain in this module because they participate in one consistency-sensitive reconciliation workflow.

## Alternatives considered



### Traditional layered monolith

Rejected as the primary organization because code would tend to be grouped globally by technical layer rather than business capability.

### Microservices

Rejected for the initial product because they would introduce network communication, distributed transactions, event-delivery concerns, multiple deployments, and greater operational complexity without a demonstrated scaling or team-ownership requirement.

### Separate assembly for every internal module layer

Deferred. It provides stronger compile-time isolation but would initially create several small projects and more configuration. The first version uses one assembly per module and enforces internal layer rules through namespaces, tests, and review.

## Consequences

Positive:

- simple local development and deployment;
- strong local transactions;
- explicit business modules;
- lower operational complexity;
- future extraction remains possible.

Negative:

- internal layer boundaries are weaker inside a single module assembly;
- modules share one process and database;
- disciplined tests and review are required;
- extraction later would still require deliberate design work.



## Revisit when

Reconsider this decision when:

- independently deployed scaling is required;
- different teams own different modules;
- release cadence must differ by module;
- one module needs a different availability or technology profile;
- internal boundaries are repeatedly violated;
- deployment size or runtime coupling becomes a demonstrated problem.

