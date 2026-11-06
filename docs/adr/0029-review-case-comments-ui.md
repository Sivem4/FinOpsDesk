# ADR 0029: Review case comments screen

- Status: Accepted
- Date: 2026-11-06

## Context

ADR 0028 stores an immutable comment and exposes `GET` and `POST /api/review-cases/{id}/comments`. The `/review-cases` screen already shows the case, its balances, and the decision audit. It has no way to read or append that comment, and a lost POST response would mint a new idempotency key if the browser kept the key only in memory.

## Decision

The existing `/review-cases` screen gains a comment section. It is separate from the decision history and from the balances. Entering the page, filtering, paging the case list, or opening a detail does not request comments. `View comments` opens the panel and `Hide comments` closes it. The GET sends only `page` and `pageSize`. The page size is 50. The page number stays in component state and is not part of the address.

`capabilityForPath` maps `/api/review-cases/{id}/comments` to `audit.read`. No other route changes capability. `ProductionCookie` fetches only when authorization is `verified` and `audit.read` is present. `review-cases.read` alone does not fetch. `DevelopmentJwt` fetches when the current token is present. The query key includes the case, the page, the page size, and the session epoch. The cookie key also includes readiness. The request carries the session `AbortSignal`. Entries are shown only after the current query succeeds. A cookie HTTP 403 uses the existing financial revalidation and does not sign out. A JWT HTTP 403 does not clear the token and does not close the detail. Text is rendered as a React child with `white-space: pre-wrap`. HTML and Markdown are not interpreted. The screen shows the stored `actorId` and `recordedAt` and does not invent an operator name.

`totalCount` is parsed as a non-negative `int64` through `lossless-json` and `BigInt`, up to `9223372036854775807`, and is displayed as that decimal string. It is not converted through `Number`. The whole page is validated before it is shown. `page` and `pageSize` must equal the request. Each comment needs a non-empty GUID `id`, preserved .NET text of 1 to 500 UTF-16 code units, an `actorId` of 1 to 256 UTF-16 code units with no surrounding .NET whitespace, and a real `DateTimeOffset`. The year is 1 to 9999. The offset is at most ±14:00, and minute 00 is required when the offset hour is 14. A missing or invalid timestamp is a rejected page, not `null`. The screen does not trim a response into a valid shape. U+FEFF and line breaks stay as returned. A disagreement between `totalCount` and `items.length` is not a rejected page, and items stay visible when `totalCount` is 0. The API order is kept.

`Next comments` is enabled only when `BigInt(page) * 50n` is at most `2147483647` and either `totalCount` is greater than that offset or the current page has 50 items. Page `42949673` is the last page the client will request. `Previous comments` is enabled when the page is greater than 1. A full last page may be followed by one empty page. An empty page that does not indicate further items disables `Next comments`.

`DevelopmentJwt` mounts the add form for an `Open`, `Resolved`, or `Dismissed` case when the token, `org_id`, and actor are valid. The form uses React Hook Form and Zod. Normalization is `trimDotNetWhiteSpace`. The length is checked after that normalization. The raw field has no `maxLength` of 500. Whitespace-only text is rejected. U+FEFF remains. The unsent draft is bound to the case and the session epoch. A comment read does not replace typed text. Changing the case or the session does not carry the draft. The comment does not send `concurrencyToken` and does not change decision drafts. `ProductionCookie` does not mount the form or the recovery panel. The module rejects a cookie POST before `fetch`.

One tab-local record, `finopsdesk.review-case-comment-attempt`, is schema version 1 and operation `ReviewCaseComment`. It stores the organization id, the case id, one generated idempotency key, the exact body, the matching normalized text, the lifecycle, and `uncertaintyLocked`. It does not store a bearer token or a CSRF token. Binding is the organization id only, so two people of that organization in the same tab see one stored comment. The open and decision slots stay independent. Only one comment send is in flight.

A new attempt starts as `ready`. The client persists `sending` and reads the record back before the first `fetch`. Retrying a stored `sending` attempt, including after reload, persists `uncertain` and `uncertaintyLocked` true before that `fetch`. Retrying `uncertain` keeps that lock. The write and the control read finish before transport. Corrupt or unavailable storage blocks the send and blocks overwriting the record. The stored body must equal the serialization of the stored text.

The first confirmed refusal, before uncertainty is locked, may become `rejected` and keeps the key. That set is HTTP 401, 403, 413, and 415, HTTP 404 `reconciliation.review_case.not_found`, and HTTP 400 only when the code is exactly `reconciliation.review_case_comment.invalid_request`, `reconciliation.review_case_comment.idempotency_key_required`, or `reconciliation.review_case_comment.idempotency_key_invalid`. Classification does not use a prefix. An unknown or unreadable HTTP 400 stays unverified. `rejected` blocks another send until the operator removes the local tracking. HTTP 409 `reconciliation.review_case_comment.idempotency_payload_conflict` keeps the key and body. HTTP 409 `reconciliation.review_case_comment.concurrency_conflict`, a network failure, an abort, HTTP 5xx, and a defective success become `uncertain` with the lock set. A later refusal does not clear an earlier lock. There is no automatic retry and no automatic new key. Removing local tracking is two steps and does not undo a server write.

Recovery is page-level. It does not depend on the selected case or on a successful GET. Every send checks the current organization and actor. An attempt for another organization stays hidden and unchanged. Retry repeats the original POST, target, key, and body. Success is only a verified HTTP 201 or HTTP 200. The response does not need `Location`, `caseId`, or the key. The response text must equal the stored text. The response actor does not have to be the current operator. After verification the client removes only the matching descriptor: the same epoch, organization, operation, case, key, and body. A cleanup failure leaves the confirmation. The confirmation shows the stored comment even when that row is not on the current page. The screen does not append the comment into the page and does not change the selected case or the address. It refreshes only comment queries for that case and epoch. It does not refresh the summary, balances, or decision history. A later GET error leaves the confirmation and retries only the read. A late completion after leaving and returning does not take over the new view.

## Consequences

- Operators can read and append comments without changing the ADR 0028 contract.
- A lost response can be replayed with the original key and body.
- The slot is not a general recovery framework.
- Phase 8 stays planned.

## Alternatives considered

### Reuse the decision-history panel

Rejected. A comment is not a `ReviewCaseAuditAction` and must not change how decision rows are read.

### One shared recovery module for open, decision, and comment

Rejected. The three operations have different keys, bodies, and refusal sets. A shared module would couple them without a new product behavior.
