# Development Guidelines

## General approach

FinOpsDesk favours explicit business behaviour and small, reviewable changes over maximum pattern density.

Use a pattern when it solves a visible problem. Do not add abstraction because its name appears in an architecture diagram.

## Naming and organization

- Use business language in class and method names.
- Prefer feature folders inside Application.
- Avoid default dumping folders such as `Helpers`, `Managers`, or `Common`.
- Keep one primary type per file.
- Name commands as intent, for example `ConfirmAllocationCommand`.
- Name queries by returned information, for example `GetInvoiceDetailsQuery`.

## Domain modelling

Use domain modelling for:

- invariants;
- state transitions;
- financial calculations;
- allocation and reversal rules;
- decisions that must remain valid regardless of transport or storage.

Simple filtering, pagination, and DTO projection do not require rich domain objects.

Avoid public setters on aggregates. Prefer constructors or factory methods for creation and behaviour methods for state changes.

Keep derived values derived unless persistence or performance measurements justify storing them.

## CQRS

CQRS means separate command and query responsibilities. It does not imply separate databases, event sourcing, or asynchronous messaging.

Commands:

- represent intent;
- may mutate state;
- validate business preconditions;
- operate within a deliberate transaction;
- produce an auditable result.

Queries:

- never mutate state;
- project directly to DTOs;
- fetch only required columns;
- use bounded pagination;
- use deterministic ordering.

## EF Core

Use EF Core directly.  
Do not add:

- a generic `IRepository<T>`;
- a generic service layer that repeats CRUD operations;
- a second unit-of-work abstraction over `DbContext`.

Specialized repositories may be introduced for loading aggregates in command workflows when they improve the use-case language.  
Read handlers may use a dedicated read context or `DbContext` directly.

## HTTP API

Endpoints should be thin:

1. bind a transport request;
2. create a command or query;
3. dispatch it;
4. map the result to HTTP.

Use consistent Problem Details responses for validation, not-found, conflict, forbidden, and unexpected failures.  
Map stable application/domain error codes deliberately rather than deriving HTTP behaviour from exception or error message text.  
Do not expose stack traces, connection details, or internal exception messages to clients.  
Transport models must not be reused as persistence entities.

## Transactions and concurrency

A transaction should cover one business operation, not an arbitrary technical layer.  
Use optimistic concurrency for mutable financial records. A stale update should return a clear conflict response rather than silently overwrite newer state.  
Use database constraints as the final protection for uniqueness and idempotency.

## Testing

Test by risk:

- domain invariants: unit tests;
- dependency direction: architecture tests;
- mappings, transactions, constraints, concurrency, authorization, and tenancy: integration tests;
- critical UI workflows: component tests.

A test should explain a business rule or protect a technical contract. Avoid tests that merely reproduce implementation details.

For operations returning `Result`, tests should verify both:

- the returned success or failure;
- the resulting aggregate state.

A `Result.Failure` path must not partially mutate the aggregate.

## Git

Use small commits with clear intent.  
Recommended prefixes:

```
chore:
docs:
feat:
fix:
test:
refactor:
perf:
security:
ci:
```

Examples:

```
docs: define modular monolith boundaries
feat: add invoice amount invariants
test: verify partial allocation scenarios
security: enforce organization scope for invoice details
perf: add index for unmatched transaction work queue
```

Do not combine unrelated cleanup with a feature change.

## Documentation

Create or update an architecture decision record when a decision:

- has multiple plausible alternatives; 
- affects several features; 
- is difficult or expensive to reverse; 
- introduces a major dependency; 
- changes security, data ownership, or deployment architecture.

Documentation should describe current reality and explicitly label future plans.

## Operational logs

The host request summary is event 9101 from `RequestLog.Write`. Its structured state has exactly the seven fields defined in ADR 0042, and both the state `ToString()` value and the log message are `HTTP request completed.`. Copy `problemTraceId` only when the entire value is the server request id or a lowercase W3C id, and do not rewrite Problem Details to make it fit. Do not put request or response bodies, raw paths, query values, tokens, financial amounts, actor or organization identifiers, or exception text into that event. Console logging uses the JSON formatter with scopes excluded. That setting does not sanitize framework, Entity Framework, or exception logs.