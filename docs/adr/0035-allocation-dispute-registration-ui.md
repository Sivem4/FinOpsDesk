# ADR 0035: Allocation dispute registration screen

- Status: Accepted
- Date: 2026-11-07

## Context

ADR 0032 registers one allocation dispute with `POST /api/allocations/{allocationId}/disputes`. The server assigns `disputeId`. A matching key returns the original confirmation, including after reversal and including another valid actor of the same organization. A lost response does not prove that the row is absent. ADR 0034 reads those registrations and does not register one.

`DevelopmentJwt` already shows confirmed and reversed allocations on `/allocations`. The browser can observe `status === confirmed`. That observation does not reserve the allocation. The POST remains the authority.

## Decision

A confirmed row on `/allocations` opens a registration form for `DevelopmentJwt`. The form uses React Hook Form and Zod. The statement is normalized with `string.Trim()` under `Char.IsWhiteSpace`, from 1 to 500 UTF-16 code units. U+FEFF stays at either edge. The exact body is `{"statement":"<normalized>"}`.

The browser stores one `finopsdesk.allocation-dispute-attempt` record, schema version 1, before `fetch`. The record is bound to the organization id, `developmentJwt`, and the normalized `sub`. It stores the allocation id, the canonical idempotency key, and the exact body. The key and body are read back before the request. A storage failure sends nothing. Replay sends that same key and body. There is no automatic POST.

The client recognizes a refusal only by the status and full code pair mapped by `StatusCodeForDispute`. A later refusal does not clear `uncertaintyLocked` and does not hide replay. Turning replay off is the classification of a first recognized refusal, not proof that an earlier uncertain attempt is absent. HTTP 401 offers Update access. HTTP 403 `reconciliation.actor.required` keeps the token. Any other pair, a transport failure, an abort, or a defective HTTP 200 or 201 leaves the attempt uncertain.

HTTP 201 is the first commit. HTTP 200 is the original confirmation. `mapAllocationDispute` still requires the requested dispute id. Registration uses `mapRegisteredAllocationDispute`, which validates the closed record, a non-empty server `disputeId`, and the attempt's allocation id and normalized statement. Actor and time stay server data.

Publishing a result belongs to the form generation and epoch. Recovery confirmation, refusal, and the local-tracking warning belong to that same epoch and to the current history visit. Changing epoch, changing the selected allocation, or leaving and re-entering the page drops those messages. A late replay does not publish them and does not call `setSearchParams`. Settling the descriptor belongs to the attempt identity and does not require the original epoch. A late response does not delete or overwrite a later attempt and does not write the cache of a new session. Leaving the form does not abort the POST.

A verified HTTP 201 or 200 is shown before the dispute-list refresh settles. The module then invalidates `['allocation-disputes','list',epoch]` for the still current epoch, with `throwOnError` so a failed refetch is visible. Active list queries refetch once. Inactive list queries stay stale until they are used. The refresh warning and its read-only retry belong to the same generation and epoch. A refetch failure does not undo the registration or recreate the descriptor. There is no extra detail GET and no separate filtered-page GET. Allocation history, balances, and financial audit are not invalidated.

If deleting the descriptor fails, the current module instance keeps the confirmation for that attempt, including after an explicit replay, and offers only another local deletion. A later different descriptor is left untouched. After the module is recreated, a leftover `sending` or `uncertain` record is unresolved again. The owner may explicitly replay the same key and body. That replay is safe because the API is idempotent. No durable confirmation record is added. A storage failure before replay sends nothing and does not claim that the unresolved command is absent. HTTP 401 keeps Update access after uncertainty without clearing `uncertaintyLocked`.

`ProductionCookie` does not mount the form or the recovery and does not send this POST. `capabilityForPath`, contract v2, the financial flags, and the cookie write allowlist stay unchanged.

## Consequences

An operator with a development token can record an external challenge against one confirmed allocation and can repeat an unresolved attempt without minting a new key. A verified registration can appear in the dispute lists of the same session even after the form has closed. A reload cannot tell a failed local deletion from an attempt whose result was never seen. Resolution, delegation, and the operational dashboard remain later Phase 8 work. Phase 8 stays Planned.

## Alternatives considered

Requiring the original epoch before deleting the descriptor would leave a successful registration stored after the session changes. A second durable confirmation slot would fail in the same storage outage that could not delete the attempt. Fetching the dispute detail, or always fetching filtered page 1 beside `invalidateQueries`, would duplicate the active list read and would not be required: the POST body is already the registration.
