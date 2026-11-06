# ADR 0009: Review case workflow

- Status: Accepted
- Date: 2026-10-27
- Follow-up: [ADR 0025](0025-review-case-audit.md) records a server actor and one append-only review-case audit entry for a newly executed open, resolve, or dismiss. [ADR 0027](0027-review-case-open-idempotency.md) adds an optional `Idempotency-Key` for `POST /api/review-cases` and the `DevelopmentJwt` opening screen that retains that key. A request without that header still follows the open-subject rule below. The `DevelopmentJwt` screen can also resolve or dismiss an open case and keeps that command in a separate `finopsdesk.review-case-decision-attempt` record. Resolve and dismiss do not send `Idempotency-Key`; an identical terminal decision is the replay. The lifecycle, subject uniqueness, terminal replay, and `rowversion` decisions stay in place. Historical cases are not backfilled. `GET /api/review-cases/summary` counts persisted cases for the authenticated organization. The `/review-cases` screen shows those six counters. It is an input for a later dashboard and is not that dashboard. [ADR 0028](0028-review-case-comments.md) appends an immutable comment to an open, resolved, or dismissed case. A comment is not a reopen and is not a dispute.

## Context

`exact-1` and `smart-1` are advisory reads. They are not persisted, and a suggestion is not authority to allocate. Operators still need a durable place to record an exception that those reads cannot close: two rows that compete, or one row that needs investigation.

`MatchSuggestion` remains an unmapped domain type. Persisting every suggestion would store stale advice and would imply that a score or an exact pair is a financial decision. A review case is a different record. It is opened by a person, it names the financial subject, and it ends with an explicit decision.

Phase 4F does not assign cases, reopen them, write an immutable audit trail, or add roles beyond `TenantMember`. The React operations workspace remains Phase 6.

## Decision

### Persisted review case, unpersisted suggestion

`ReviewCase` is an aggregate in the Reconciliation module and is stored in the `reconciliation` schema. `MatchSuggestion` stays unpersisted. Matching and review are separate. `GET /api/matches` and `GET /api/matches/smart` do not create cases. Review endpoints do not run `exact-1` or `smart-1` and do not load the open reconciliation book.

### Manual creation and reasons

A case is opened only by `POST /api/review-cases`. The reason is `CompetingMatch` or `NeedsInvestigation`. `CompetingMatch` requires both an invoice id and a bank transaction id. `NeedsInvestigation` requires exactly one of them. The optional source label is `manual`, `exact-1`, or `smart-1`. That label records where the operator looked. It is not a match result and it is not checked against the matcher. The optional note is trimmed, a blank note is stored as null, and the maximum length is 500 characters.

The organization id and the subject key are not accepted from the client. `OrganizationId` comes from `ICurrentOrganizationContext`.

### Lifecycle

A case is `Open`, then `Resolved` or `Dismissed`. There is no reopen transition. Resolution outcomes are `AllocationRecorded`, `NoActionRequired`, and `HandledOutside`. `AllocationRecorded` requires an allocation id. The other outcomes reject one. Dismissal has no outcome and no allocation id.

### Subject identity and create idempotency

The subject key is deterministic. `CompetingMatch` uses `competing:{invoiceId}:{bankTransactionId}`. `NeedsInvestigation` uses `investigation:invoice:{invoiceId}` or `investigation:bank:{bankTransactionId}`. Each identifier is the lowercase `D` form of the GUID. At most one `Open` case may exist for an organization and subject key. The filtered unique index `UX_ReviewCases_OrganizationId_SubjectKey_Open` enforces that. Repeating an open request for that subject returns the existing open case and does not change its note, source label, or `rowversion`. That idempotency lasts only while the case is open. After the case is resolved or dismissed, a new open case for the same subject is allowed. This is not an idempotency-key contract.

### Terminal replay

Resolve and dismiss compare the requested decision with the stored decision. The same status, outcome, allocation id, and normalized resolution note returns success and does not change `ClosedAt` or the `rowversion`. A different terminal decision returns `reconciliation.review_case.terminal_conflict` and does not overwrite the stored decision. Dismissal compared with resolution is a different decision.

### Concurrency

`ReviewCase` uses a SQL Server `rowversion` shadow property. HTTP exposes it as an opaque Base64 `concurrencyToken` of the 8 bytes. The client reads the case, sends that token, and reloads after a conflict. The server does not substitute a newly loaded token for the client's token.

- A matching token on an open case applies the transition.
- A stale token on an open case returns `reconciliation.review_case.concurrency_conflict`.
- A stale token with the identical terminal decision succeeds.
- A stale token with a different terminal decision returns `reconciliation.review_case.terminal_conflict`.

There is no retry that applies the losing request with a newer token.

### Tenant isolation

Every read and write is scoped by the authenticated organization. A case, invoice, bank transaction, or allocation owned by another organization is not readable or usable through these routes. A missing case and a foreign case both return `reconciliation.review_case.not_found`. Error bodies do not include the other organization's financial values.

### AllocationRecorded

The handler checks that the allocation exists in the same organization, is `Confirmed`, and covers the case. `CompetingMatch` requires the allocation's invoice and bank transaction to be the case's pair. A single-sided case requires the corresponding id. A missing, foreign, reversed, or non-covering allocation returns `reconciliation.review_case.allocation_mismatch`. A partial confirmed allocation is acceptable. Recording the outcome does not insert an allocation and does not change invoice or bank-transaction balances.

Review actions never perform financial allocations. Balances change only through the existing allocation commands.

### Linked observations

`GET /api/review-cases/{id}` returns the persisted case and current linked balances. Those balances are read at request time. They are not a snapshot stored on the case, and they do not authorize an allocation. A missing linked row is reported as not found in the observation. It is not an integrity failure. Stored money that breaks the invoice or bank-transaction invariants returns `reconciliation.review_case.inconsistent_linked_money` and HTTP 500. The response does not include the corrupted amounts, currencies, SQL, or database exception text. That failure is distinct from a missing case.

### List

`GET /api/review-cases` returns `items`, `page`, `pageSize`, and `totalCount` for the authenticated organization. Page defaults to 1 and page size defaults to 50. Page size must be from 1 through 100, and the zero-based offset must fit in a 32-bit integer. Order is opened time descending, then review case id descending. Optional filters are status, invoice id, and bank transaction id. Any other query parameter is rejected. Status values are the strings `Open`, `Resolved`, and `Dismissed`. Numeric enum values are rejected.

### HTTP contract

The routes use the existing `/api` group and `TenantMember` policy. Endpoints bind and validate JSON, call the application handlers, and map `Result<T>` to ProblemDetails. Unknown JSON properties, wrong JSON types, empty GUIDs, and malformed JSON are rejected for these routes without changing the global serializer. Reason, status, outcome, and source label are JSON strings.

| HTTP | Codes |
| --- | --- |
| 400 | `reconciliation.review_case.invalid_request`, `reconciliation.review_case.invalid_concurrency_token`, `reconciliation.review_cases.invalid_pagination`, `reconciliation.review_case.allocation_required`, `reconciliation.review_case.allocation_forbidden` |
| 404 | `reconciliation.review_case.not_found`, `reconciliation.invoice.not_found`, `reconciliation.bank_transaction.not_found` |
| 409 | `reconciliation.review_case.concurrency_conflict`, `reconciliation.review_case.terminal_conflict`, `reconciliation.review_case.allocation_mismatch`, `reconciliation.review_case.subject_conflict` |
| 413 | `reconciliation.review_case.request_too_large` |
| 415 | `reconciliation.review_case.unsupported_media_type` |
| 500 | `reconciliation.review_case.inconsistent_linked_money` |

A new case returns 201 and `Location: /api/review-cases/{id}`. An existing open case returns 200. The `created` flag selects the status code and is not a response field.

### Known limits

The workflow is actorless. It does not record which user opened, resolved, or dismissed a case. It does not write an immutable audit record. It does not assign, comment, or reopen. Those gaps are accepted for this phase. Phase 5 owns financial audit. Phase 6 can present this API in the React operations workspace. Phase 7 owns finer roles and OpenAPI Bearer security metadata. Those roles and the later OpenAPI description were delivered after this decision. See ADR 0017 and ADR 0024. ADR 0025 later adds the server actor and the append-only review-case audit entry. It does not assign, comment, or reopen a case, and it does not add an audit read.

The generated OpenAPI document describes the five operations, their schemas, and the response codes. It does not yet include Bearer security metadata for these routes, which is the same gap already recorded for `GET /api/matches` and `GET /api/matches/smart`. Runtime authorization still requires `TenantMember`. That was the state of this decision. Phase 7G later described the active host composition. See ADR 0024.

## Alternatives considered

### Persisting `MatchSuggestion`

Rejected. A suggestion is recalculated advice. Storing it would freeze a ranking that can be stale before anyone acts, and it would blur advice with a decision.

### Automatic case creation from `exact-1` or `smart-1`

Rejected for this phase. The operator chooses the exception. Background matching and batch confirmation remain Phase 4G.

### A generic workflow engine

Rejected. The lifecycle is three states and three resolution outcomes. A general engine would add assignment, reopen, and audit that this phase does not have.

### Lifetime idempotency for create

Rejected. The unique open subject stops two active investigations of the same rows. After a terminal decision, the subject can be opened again. A client idempotency key is not part of this contract.

## Consequences

- Review state is durable and tenant-scoped. Match suggestions stay ephemeral.
- Create idempotency is limited to the active case for a subject.
- Terminal replay is identity of the stored decision, not a new write.
- Optimistic concurrency stays on the client-supplied `rowversion` token.
- `AllocationRecorded` validates an existing confirmed allocation and does not allocate.
- Linked balances can change after the case is opened. Readers must treat them as observations.
- Inconsistent stored money is a controlled HTTP 500, not a missing case.
- There is no actor and no immutable audit trail for review actions. ADR 0025 later requires a server actor and one audit entry for a newly executed action. This sentence records the Phase 4F decision.
- The React workspace can call these routes later without another review-case model.
