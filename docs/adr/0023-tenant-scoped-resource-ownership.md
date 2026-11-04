# ADR 0023: Tenant-scoped resource ownership and authorization

- Status: Accepted
- Date: 2026-11-04

## Context

Phase 7E audited the Reconciliation and Access paths that already exist. It did not add an authorization mechanism. The audit separates six checks that the code already keeps apart.

Authentication establishes the caller. `ProductionCookie` does that with the application cookie, the server session, and the issuer and subject. `DevelopmentJwt` does that with a validated bearer token, and only in the `Development` and `E2E` host environments.

Tenant membership answers whether that caller may act in an organization. `AccessSessionGate` resolves the application user, rejects a disabled user and a mismatched `SessionStamp`, rejects a missing, revoked, or expired browser session, and lists only active memberships in active organizations. `POST /api/session/organization` can select an organization only from that list. ADR 0015 records that `finopsdesk_org` remembers the choice and is not itself authorization.

Capability-based authorization answers which operation the active membership may call. `TenantMember` carries `FinancialApiAccessRequirement`. `FinancialApiAccessHandler` reads the single `FinancialEndpointClassification` on the route. Cookie reads stay closed until `Authentication:FinancialReadAuthorization` is `Enabled`. Cookie writes also require `Authentication:FinancialWriteAuthorization` and the allowlist in `ProductionFinancialWriteActivation`. The role map in ADR 0017 supplies the permission. `DevelopmentJwt` accepts a consistent classification and does not read Access roles.

Resource ownership answers whether the requested row belongs to the effective organization. Handlers take `ICurrentOrganizationContext.OrganizationId` and pass it into the repository or query. A row owned by another organization is the same result as a row that does not exist.

Domain invariants answer whether the owned row may undergo the operation: a positive amount, the same currency, an open review case, or a matching row version. Those failures are not authorization decisions.

Transaction-time financial write authorization answers whether the cookie caller may still commit. `FinancialCommitGuard` runs inside the SQL transaction that performs a ProductionCookie financial write. ADR 0019 records that check. It is not a substitute for the row lookup, and the row lookup is not a substitute for it.

## Decision

Four boundaries stay in place. Phase 7E adds no handler, policy, repository method, or schema object.

### Trusted tenant context

`HttpContextCurrentOrganizationContext` is the tenant source for Reconciliation handlers.

For `ProductionCookie` it reads the active organization already resolved by `AccessSessionGate`. It does not read the raw `finopsdesk_org` claim. A body, query, or header organization id cannot replace that value. `POST /api/session/organization` returns 403 when the requested id is absent from the caller's active memberships, and the response does not include that organization's name.

For `DevelopmentJwt` the same context reads the validated organization claim through `OrganizationClaimParser`. ADR 0004 is that contract. The composition does not register `DevelopmentJwt` outside `Development` and `E2E`. A token whose claim is organization B is a caller of organization B. It is not a way for a caller of organization A to read organization B.

### Tenant-scoped data access

Repositories and read queries constrain the row by the effective `OrganizationId`. `InvoiceRepository.GetByIdAsync`, `BankTransactionRepository.GetByIdAsync`, `AllocationRepository.GetByIdAsync`, and `ReviewCaseRepository.GetByIdAsync` each require both values. List and history queries start from `OrganizationId` and only then apply a client filter. `AllocationHistoryQueries` joins the invoice and the bank transaction on `(OrganizationId, Id)`.

`ReconciliationDbContext` has no `HasQueryFilter`. A missing predicate is not corrected by a global filter. The predicate is visible on the query that uses it.

A point read whose id belongs to another organization returns no row. The HTTP mapper uses the same not-found result it uses for an unknown id. `GET /api/allocations` with a filter id from another organization returns an empty page, not that organization's rows. `POST /api/matches/batch/execute` classifies such a pair as `notFound` and does not allocate it. Those response shapes are existing endpoint behavior, not one new status rule.

### Persistence integrity

The relational model rejects a cross-organization reference that the application tried to save.

`Invoice`, `BankTransaction`, `Allocation`, `ImportBatch`, and `BatchExecution` each expose the alternate key `(OrganizationId, Id)`.

`Allocation` references `Invoice` and `BankTransaction` with the foreign key `(OrganizationId, InvoiceId)` and `(OrganizationId, BankTransactionId)`. Both use `ON DELETE RESTRICT`. `UX_Allocations_OrganizationId_IdempotencyKey` and `UX_Allocations_OrganizationId_ReversalIdempotencyKey` keep confirmation and reversal idempotency inside one organization.

`FinancialAuditEntry` references `Allocation`, `Invoice`, and `BankTransaction` on the same composite shape, and optionally `BatchExecution`. `UX_FinancialAuditEntries_OrganizationId_AllocationId_Action` is organization-scoped. `UX_BatchExecutions_OrganizationId_IdempotencyKey` and `UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash` are organization-scoped. `ImportRowError` references its batch through `FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId`.

`ReviewCase` references `Invoice` and `BankTransaction` with the composite foreign key. It stores `AllocationId` without a composite foreign key to `Allocation`. `ResolveReviewCaseHandler` loads that allocation through `AllocationLinkLookup` with the case's organization and rejects a link that does not cover the case. `CK_ReviewCases_StatusShape` still requires an allocation id only for the resolved outcome that records one. The open-subject uniqueness index is `UX_ReviewCases_OrganizationId_SubjectKey_Open`.

### Commit-time authorization

`FinancialCommitGuard.ProtectAsync` runs only when the host is `ProductionCookie` and the endpoint classification is a write. It reads the issuer, subject, session stamp, browser session id, and organization from the authenticated principal. It does not accept those values from the body. `OrganizationFinancialAuthorization.AuthorizeSharedAsync` takes the Shared application lock `finopsdesk-auth:{organizationId}` and, in that transaction, checks the user, stamp, session, active membership, active organization, and the classified permission.

`DevelopmentJwt` never enters `ProtectAsync`. Ordinary reads are authorized when the request is authorized. They are not held under that lock until the response is written.

The guard does not load the invoice, bank transaction, allocation, or review case. Those lookups stay in the handler, with the effective organization, in the same financial transaction as the mutation.

### Resource handlers

`IAuthorizationService` resource handlers are not added for these ownership rules. The tenant-scoped query already decides whether the row is visible, and it does not need to load another tenant's row in order to deny it.

A later operation can still need a resource-specific permission that tenant membership and the current capability do not express. An example is a rule that only the actor who confirmed an allocation may reverse it, or a permission that depends on the state of one row rather than on the caller's organization. That kind of rule is not implemented today. If it is approved, a resource handler is one possible place for it. This ADR does not choose that design in advance, and it does not forbid `IAuthorizationService`.

### HTTP semantics

The current endpoints do not share one status for every failure.

An unauthenticated caller receives 401. A cookie caller without the required capability, or a financial route while its activation flag is unset, receives 403. Selecting an organization outside the caller's active memberships receives 403.

An unknown resource id and a resource id owned by another organization both produce no row. Point reads and the mutations that look up one row return 404 for that result. Allocation history returns 200 with an empty page when the filter matches nothing in the caller's organization. A batch item that does not resolve in the caller's organization is reported as `notFound` on that item. Cookie batch execution does not reach that classifier unless the write allowlist includes it. Today the allowlist does not.

A malformed body or query is 400. A domain rejection, including a stale row version or a state that does not permit the operation, is 409 where that endpoint already maps the error to 409. Membership storage that cannot be read is 503 and does not clear an unexpired cookie. This ADR does not collapse those outcomes into one code.

### Security limitations and regression risk

There is no EF Core global query filter behind a forgotten predicate. A new handler must pass `OrganizationId` into every read and write of a tenant-owned row.

A command that carries two resource ids must resolve each id in the effective organization. Checking one of them does not authorize the other.

Hiding a button in the React application is not a security boundary. The API classification and the query are the boundary.

`DevelopmentJwt` does not implement ProductionCookie membership, roles, session revocation, or `FinancialCommitGuard`.

The existing integration and browser tests cover representative cross-tenant reads and writes. They are not an exhaustive BOLA/IDOR proof. This ADR does not treat the absence of a finding in the Phase 7E audit as a proof that no future gap can exist.

Phase 7F is the systematic cross-organization and BOLA/IDOR matrix. Phase 7G is the OpenAPI description of authentication and authorization. Neither phase is delivered here.

### Alternatives considered

An `IAuthorizationService` handler per operation would run after a resource is loaded, or would repeat the tenant-scoped query. For the current rule, "the row must belong to the effective organization," the query is the decision. A handler is reserved for a future rule that the query does not express.

A global query filter would hide rows that omit `OrganizationId` from the LINQ predicate. It would also make the tenant boundary depend on the ambient context and on every caller remembering not to ignore the filter. The explicit predicate stays easier to review. The filter was not added as a silent fallback.

Tenant-scoped repository and query methods are the ownership boundary that is already in the handlers. They stay.

Composite foreign keys and organization-scoped unique indexes remain the persistence backstop. They reject a saved relationship the application should already have rejected. They do not replace the lookup that keeps another tenant's data out of the response.

The four boundaries together are retained. No one of them is the whole control.

## Consequences

- Ownership predicates stay in the query that uses them, where a review can see the organization comparison.
- The database rejects a cross-organization allocation, audit link, import-row link, and review-case link to an invoice or bank transaction.
- The request path does not load a foreign row in order to deny it, and it does not add a resource-authorization layer.
- A new query can omit `OrganizationId` without a global filter to catch it. Phase 7F is the regression net for that class of mistake.
- Cookie financial writes still depend on `FinancialCommitGuard` inside the committing transaction. Reads stay request-time checks.
- Deployed configuration still leaves `Authentication:FinancialReadAuthorization` and `Authentication:FinancialWriteAuthorization` unset. This ADR does not enable either flag and does not change the write allowlist.
