# ADR 0031: Review case assignment screen

- Status: Accepted
- Date: 2026-11-06

## Context

ADR 0030 lets an operator claim an unassigned open review case and release only that operator's own assignment. The `/review-cases` screen can already open, resolve, dismiss, and comment on a case. It does not show who holds the case, and a lost assignment POST would mint a new idempotency key if the browser kept the key only in memory.

The assignment read is not part of contract v2. Cookie POST stays outside the financial write allowlist. `DevelopmentJwt` still does not distinguish roles.

## Decision

The existing `/review-cases` screen gains an assignment section for `DevelopmentJwt` only. Selecting a case starts `GET /api/review-cases/{id}` and `GET /api/review-cases/{id}/assignment` together. The query key includes the case id and the session epoch. The request carries the session `AbortSignal`. Each read receives its sequence number before the request is sent. A response whose `reviewCaseId` is not the requested case is rejected. The GET token field is `concurrencyToken`.

The current assignee is separate from the confirmation of a command. A verified HTTP 200 remains the historical result: it can confirm a claim after a later release has cleared the case. After verification, while that case is still selected, the screen cancels an in-flight detail or assignment read and starts both GETs again. A read that started before that confirmation cannot replace the newer result or unlock the next command. If either fresh read fails, the confirmation, the warning, and `Retry reading the case and assignment` stay visible for that same case and epoch. The retry reads the detail and the assignment again. Those messages belong to one visit. The visit changes on every selection transition, including a return to the same case, and a result from the previous visit is not shown. Leaving the page or changing the session epoch does not show it either. A stored attempt can still be retried in the new visit. A late result does not update another case or another session epoch.

A new claim or release requires an empty assignment slot, a current organization, and a normalized `sub`. It also requires both reads to be for that case and epoch, with equal concurrency tokens. Claim is an unassigned `Open` case. Release is an `Open` case assigned to `developmentJwt` and the current `sub`. Those conditions are checked again from the current detail and assignment queries when the operator clicks. Both queries must be successful. A query that failed and still holds older data does not qualify. The tokens must match, both sequences must be above the remembered boundary, and the chosen operation must still be available. After a verified HTTP 200 or 201, or after the operator removes local tracking, both sequence counters are remembered. The next command needs two successful reads whose numbers are strictly greater than those counters. The counters live in module memory, so they survive unmounting the panel. A cached result, or a read that started before the counter was remembered, does not unlock the command. One failed GET keeps the command unavailable. `Refresh the case and assignment` repeats the two GETs and does not POST. A resolved or dismissed case shows the stored assignee and offers neither command. Resolve, dismiss, and comments do not wait on the assignment.

`ProductionCookie` does not mount the section or the recovery panel and does not request `/assignment`, including when the cookie capabilities list `review-cases.read`, `audit.read`, `review-cases.assign`, or `review-cases.assignment.read`. `capabilityForPath` does not gain an assignment route. The module rejects a cookie POST before `fetch`.

One tab-local record, `finopsdesk.review-case-assignment-attempt`, is schema version 1. It stores the operation `ReviewCaseClaim` or `ReviewCaseRelease`, the organization id, `developmentJwt`, the normalized `sub`, the case id, one idempotency key, the exact body `{"concurrencyToken":"..."}`, the lifecycle, and `uncertaintyLocked`. It does not store a bearer token or a CSRF token. The open, decision, and comment slots stay independent. At most one assignment POST is in flight in the tab.

The record belongs to one operator. Another operator in the same organization may read the assignment. That operator cannot create, retry, remove, or replace the record, and the screen does not show that command's case id, key, or body. The owner sees the stored case id with Claim or Release, including while another case is selected, while no case is selected, and while confirming removal of local tracking. When the original operator returns, the original key and body are still there. A missing or invalid `sub` blocks a new command and blocks replay. The stored record stays.

The client persists the required lifecycle and reads it back before `fetch`. The first send persists `sending`. A replay persists `uncertain` with `uncertaintyLocked` true. If that write fails before the first send, no POST is made and the screen says the command was not sent. If that write fails before a replay, no POST is made and the screen says the retry was not sent. The previous descriptor, key, body, and existing lock stay. Replay sends the stored operation, key, and exact body, including an old token.

`reconciliation.review_case_assignment.idempotency_payload_conflict` and `reconciliation.review_case_assignment.concurrency_conflict` are conflicts only when the status is HTTP 409. The same codes on another status keep the original key and exact body, set `uncertain` and `uncertaintyLocked`, and leave an explicit replay available. The first HTTP 409 `reconciliation.review_case_assignment.idempotency_payload_conflict` sets `rejected`, keeps the key and body, turns replay off, and leaves removal available. The same conflict after an earlier uncertain result also sets `rejected` and keeps `uncertaintyLocked` true. The first HTTP 409 `reconciliation.review_case_assignment.concurrency_conflict` sets `rejected` and may say that the server did not find an entry when it checked and that this command was not stored. After a lost response, that same code leaves the record `uncertain` with the lock set. Its message may say only that the server did not find an entry for this key at the time of the check. A later refusal does not clear an existing lock. Removing local tracking does not undo a server write and raises the fresh-read boundary.

A success is HTTP 200 or 201 whose `reviewCaseId`, operation, `recordedAt`, and `assignee` match the command. Claim requires `developmentJwt`, the stored actor, and `assignedAt`. Release requires the `assignee` field to be present and null. Both timestamps use the comment `DateTimeOffset` rules: year 1 to 9999, offset at most ±14:00, and minute 00 when the offset hour is 14. The same assignee parser reads GET and POST. A defective success keeps the key and body, sets `uncertain` with the lock, and shows no confirmation. The descriptor is removed only after that full check for the same operator, organization, and epoch.

## Consequences

- A DevelopmentJwt operator can see, claim, and release an assignment without a new route or a change to ADR 0030.
- A lost response can be replayed with the original key and body.
- The slot is not a general recovery framework. Cookie operators still have no assignment screen.
- Contract v2, the cookie allowlist, and the financial flags stay unchanged.
- Phase 8 stays planned.

## Alternatives considered

### Put the assignee on the detail DTO

Rejected. ADR 0030 keeps list, detail, and summary free of assignee fields so `review-cases.read` does not reveal the assignment.

### Share one recovery module with comments

Rejected. The assignment record is bound to the operator as well as the organization, and its conflict transitions are not the comment transitions.
