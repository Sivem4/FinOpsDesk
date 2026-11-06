# ADR 0030: Review case assignment

- Status: Accepted
- Date: 2026-11-06

## Context

An open review case can be claimed by the operator who is working on it. The product does not assign another person, take over someone else's assignment, or give an administrator a separate overwrite. Resolve and dismiss already close a case without an assignee. Comments and the decision audit already have their own records.

The same actor string can come from `DevelopmentJwt` `sub` or from `ProductionCookie` `ApplicationUser.Id`. Those sources are not one identity. `ICurrentActorContext` already defines the actor id for audit and comments. Changing that meaning would rewrite those records.

A claim changes the case row, so it shares the case `rowversion` with resolve and dismiss. A second save that wrote the resulting token back into an append-only history row would be rejected by the mutation trigger. The next command reads the token again.

## Decision

`ReviewCase` stores `AssignedActorSource`, `AssignedActorId`, and `AssignedAt`. All three are null or all three are set. There is no separate assignment aggregate and no second token. Existing rows stay unassigned. There is no backfill.

`ReviewCaseAssignmentEntries` is one append-only table. It is the history and the idempotency record. A row stores the entry id, organization, case, operation, actor source, actor id, canonical request token, idempotency key, `RecordedAt`, and `AssignedAt` for a claim. A release stores null `AssignedAt`. The case update and the insert commit in one `SaveChangesAsync`. The resulting `rowversion` is not stored. The row is not updated.

The key is unique on `(OrganizationId, IdempotencyKey)` for both operations. The payload is the operation, case, source, actor id, and canonical token. `ReviewCaseConcurrencyToken` still decodes eight bytes. The stored token is `Encode` of those bytes. Equivalent Base64 whitespace is the same token. Resolve and dismiss keep their decoder.

The order is fixed. The route, body, key, and token are validated, then the actor and source. A missing actor returns the existing `reconciliation.actor.required` before the key is read. A stored key is classified and returned without reading the case. A missing key loads the case. The token is compared before terminal status and before the assignment rules. A mismatch reads the key again. A matching entry returns HTTP 200. A different payload returns `idempotency_payload_conflict`. No entry returns `concurrency_conflict`. The current assignment does not prove that this request ran. The same recovery follows a named SQL Server 2601 or 2627 collision on the idempotency index and a `rowversion` conflict. Another index, primary key, or foreign key violation is not a replay. The failed context is not saved again.

An operator can claim an unassigned `Open` case and can release only that same `(ActorSource, ActorId)` pair, compared with `StringComparison.Ordinal`. A new key on an existing assignment, including the holder's own, returns `already_assigned`. Releasing nobody returns `not_assigned`. Releasing someone else returns `not_assignee` and does not name the holder. New claim and release reject a resolved or dismissed case. Resolve and dismiss keep their permissions, do not require an assignment, and do not clear it. The assignment stays frozen, with no extra history row. Assignment does not change balances, allocations, comments, the decision, `ClosedAt`, or existing `ReviewCaseAuditEntry` rows.

`POST /api/review-cases/{id}/assignment` and `POST /api/review-cases/{id}/assignment/release` accept only `concurrencyToken`. `Idempotency-Key` is required: one non-empty UUID in `D` format, with no surrounding .NET whitespace. `Guid.TryParseExact` is not the whitespace guard. A new row returns HTTP 201. A matching historical row returns HTTP 200. There is no `Location` header and no resulting concurrency token. `GET /api/review-cases/{id}/assignment` returns the case id, the current assignee or null, and the current case token. Unknown and foreign cases share HTTP 404 `reconciliation.review_case.not_found`. List, detail, and summary do not gain assignee fields.

The actor source is a narrow port implemented by the host. `DevelopmentJwt` remains `sub`. `ProductionCookie` remains `ApplicationUser.Id`. The module does not read Access tables. `FinancialWriteOperation.ClaimReviewCase` is 12 and `ReleaseReviewCase` is 13. Both writes use `review-cases.assign`. The read uses `review-cases.assignment.read`. Only `Operator` has those permissions. They are classified and catalogued. They are not added to `ProductionFinancialWriteActivation`. Cookie POST stays denied when both flags are enabled. `FinancialCommitGuard`, the flags, and the six `SessionContract.OrderedReads` stay unchanged. Contract v2 does not advertise the new read. `DevelopmentJwt` still does not distinguish roles.

The composite foreign key to `ReviewCase` is `Restrict`. Checks cover the assignment shape, the source, the actor, the token, and the key. A trigger rejects update and delete of the history. EF uses `UseSqlOutputClause(false)`. `Down` drops the assignment columns and the history. It deletes stored assignments and is not a production rollback.

## Consequences

- A lost claim or release is retried with the same key and the same canonical token. A later release, claim, or close does not change that historical response.
- The next command must read the case token again. The POST does not return it.
- Cookie operators cannot claim or release until a later activation decision. This record does not enable the flags or add a screen.
- Phase 8 stays planned.
