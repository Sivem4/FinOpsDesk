# ADR 0037: Allocation dispute resolution screen

- Status: Accepted
- Date: 2026-11-08

## Context

ADR 0036 records one terminal decision for a stored allocation dispute and leaves the screen for a later increment. ADR 0034 already shows the registration on `/allocation-disputes`. ADR 0035 stores one registration command in the browser before its POST. The decision needs the same kind of explicit replay, but it is a different command and must not reuse the registration descriptor or its list refresh.

## Decision

`DevelopmentJwt` reads and records the decision on the existing `/allocation-disputes` screen. The selected dispute calls `GET /api/allocation-disputes/{disputeId}/resolution`. `decision: null` is the only “no decision” state. A stored decision shows its outcome, reason, actor source, actor id, and time. The form sends `outcome` and `reason` through `POST` on that same path. The choice is `upheld` or `not_upheld`. The reason is normalized with the existing `string.Trim()` rules before it is stored. The decision does not depend on the current allocation status and does not move money.

The browser keeps one `finopsdesk.allocation-dispute-resolution-attempt` record, schema version 1, operation `AllocationDisputeResolution`. It is bound to the organization id, `developmentJwt`, and the normalized `sub`. The record and its read-back finish before `fetch`. Replay is an explicit click and sends the original key and the original raw body. Another operator does not see the dispute id, key, body, outcome, or reason, and cannot replay, delete, or replace the record.

A module-level boundary, keyed by session epoch and dispute id, survives unmounting the panel. Each entry has `serial`, an optional `confirmed` decision, and `nullObservedSerial`. A new command is allowed only when the identity is valid, the slot is empty, `confirmed` is absent, the latest qualifying read observed `decision: null` at the current serial, that query is still a successful idle `decision: null`, and no refresh is in flight. Those conditions are read again from the module and from TanStack Query at send time.

After a verified POST the screen cancels that dispute’s decision query, raises `serial`, stores `confirmed`, and only then removes the matching descriptor. It writes the confirmed decision into the cache for the same epoch and then starts one fresh GET. A response that cancellation did not stop is still checked against the epoch and `serial`. Confirmation is shown before that GET finishes. A failed GET leaves the confirmation and the write lock in place, does not restore the descriptor, and does not allow another command. That failed GET is shown as a read error. `confirmed` does not replace it with the decision fields. HTTP 401 offers Update access, HTTP 403 keeps the token, and HTTP 404 is not the “no decision” state. The retry is only the GET for the dispute named by the verified result, including when another dispute is selected or none is selected. A successful read of a different dispute does not clear that warning. The screen takes the next refresh generation before it waits for the read. An older success or failure cannot replace or remove a newer warning. A manual retry takes the same generation before its read. A changed epoch or an unmounted page drops that completion.

If removing the matching descriptor fails, the in-memory confirmation still marks that attempt verified. The screen offers only another local deletion. It does not send a POST, mint a key, or say that the result is unverified. Changing the selection or remounting the panel in the same module instance does not restore replay. The confirmation does not authorize deleting a newer descriptor or another operator’s descriptor. After a full reload the module memory is gone, so a leftover descriptor may be replayed explicitly with the same key and body.

Removing local tracking also raises `serial` and cancels the in-flight GET. The next command waits for a GET that started after that boundary and returned `decision: null`. That null is an observation, not proof that the abandoned POST never committed. A later session epoch does not receive the previous epoch’s cache write.

Recognized refusals compare the HTTP status and the full error code. The first recognized refusal marks the attempt rejected. The same pair after an earlier uncertain result stays uncertain and can still be replayed. HTTP 401, on either path, keeps the descriptor and shows Update access through the existing session action. It does not clear an earlier `uncertaintyLocked`. HTTP 500 is not a conflict even when its body contains a 409 code. A defective HTTP 200 or 201 leaves the attempt uncertain. A GET does not carry the idempotency key, so a similar decision does not confirm the stored command.

The parsers accept only the closed GET wrapper and the closed decision object. Identifiers, outcome, preserved text, and `DateTimeOffset` use the registration rules, including the UTC tick range. A correct server value is not normalized again. POST success must match the stored dispute id, outcome, and reason. The response actor may be the original actor.

Success and command-error messages belong to the epoch, the page instance, and the selection visit. Changing the selection, leaving the page, or changing the session invalidates that publisher. The refresh warning stays bound to the epoch and dispute id of the verified result, so another selection does not clear it. Its publication and removal also follow the refresh generation described above, so an older read cannot overtake a newer one. Leaving the page drops that component state. Settling the descriptor does not depend on the view still being mounted.

`ProductionCookie` does not mount the panel or its recovery and does not send these requests. The registration contract, address, filters, pagination, session contract, capability map, allowlist, flags, backend, and migrations stay unchanged.

## Consequences

- An operator can record the judgment without a settlement correction.
- The registration recovery record remains a separate slot.
- The decision still cannot be edited or reopened.
- Settlement correction, delegation, and the operational dashboard stay later.
- Phase 8 stays planned.

## Alternatives considered

### Reuse the registration attempt record

Rejected. The two commands have different bodies, routes, and refresh targets. A shared slot would hide one command behind the other, and a generic recovery framework would rewrite the registration path that ADR 0035 already fixed.
