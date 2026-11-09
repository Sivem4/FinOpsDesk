# ADR 0040: Review case delegation

- Status: Accepted
- Date: 2026-11-09

## Context

ADR 0030 lets the current assignee release an open review case. The same person could not hand that open case to another known person without releasing it first. Reconciliation must not read Access tables. `DevelopmentJwt` `sub` and `ProductionCookie` `ApplicationUser.Id` are different people. A display name is not an identity.

## Decision

`POST /api/review-cases/{id}/assignment/delegate` requires `review-cases.assign`, `FinancialWriteOperation` value 17, and one canonical `Idempotency-Key`. The closed body is `concurrencyToken` and `recipientActorId`. Validation and key classification follow claim and release. A new command can be sent only by the current assignee of an open case. The server trims the recipient with the same rule as an actor id, requires 1–256 characters, requires the same actor source, and requires a different person. The normalized recipient must be on the host list for the server-chosen organization. That list is `DevelopmentDelegation:Organizations:<organization id>`. A missing section, a missing organization, or an empty list means there are no recipients. A new command for a missing list, a foreign recipient, a recipient who is not listed, or the sender returns `reconciliation.review_case_assignment.delegatee_unavailable` and does not say which. A matching replay returns the stored result and does not consult the list again.

The assignment change and one history row, `Delegate = 3`, commit in one `SaveChangesAsync`. The key space stays `(OrganizationId, IdempotencyKey)`. `rowversion` comparison and the fresh-context read after rollback stay the existing assignment lookup. A new row is HTTP 201. A replay is HTTP 200. The response records the sender, the recipient, and the delegation time. It has no `Location` and no resulting token. The next command reads the token from `GET /api/review-cases/{id}/assignment`.

`GET /api/review-cases/delegation-recipients` requires `review-cases.assignment.read`. The catalog uses the authentication composition fixed when the host starts. A later change to `Authentication:Composition` does not open it. The recipient values themselves are still read from the current configuration. `DevelopmentJwt` returns the normalized, de-duplicated, ordinally sorted identifiers for the current organization. No configuration returns an empty list. `ProductionCookie` returns HTTP 403 `reconciliation.review_case_assignment.delegation_unavailable` before that configuration is read. OpenAPI documents that code. The general authentication transformer does not add `reconciliation.actor.required` to this GET. Value 17 is not added to `ProductionFinancialWriteActivation`.

The SQL recipient check compares `RecipientActorId` and `ActorId` with `Latin1_General_100_BIN2`, the same ordinal distinction the application uses. A case-insensitive database collation still accepts two identifiers that differ only by case and rejects the exact same actor.

The migration adds nullable `RecipientActorId` and checks for claim, release, and delegate. `Down` first checks for a `Delegate` row. If one exists, it throws SQL error 50006 before any schema change and keeps the history and the assignment. It does not delete history. Without a `Delegate` row it restores the claim and release checks. This is not a production rollback.

The browser keeps the one slot `finopsdesk.review-case-assignment-attempt` at schema version 1. A claim or release record without recipient fields stays readable, replayable, and blocking. An unresolved attempt blocks claim, release, and delegate. After it is settled or abandoned, the detail read and the assignment read must both start after the shared freshness floor and return the same token.

## Consequences

The Access catalog is not a recipient source. Cookie delegation stays denied when both financial flags are enabled. A typo that is a configured string is a recipient. A command for a person who is no longer listed is refused, while the historical replay of that person still returns the stored result.
