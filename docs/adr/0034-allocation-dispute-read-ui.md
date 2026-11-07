# ADR 0034: Allocation dispute read screen

- Status: Accepted
- Date: 2026-11-07

## Context

ADR 0033 lists stored allocation-dispute registrations and reads one registration. The response is the statement, the actor source, the actor id, and the recorded time. It does not include the organization, the idempotency key, the current allocation state, or a resolution. A reversal leaves the registration unchanged. Contract v2 does not advertise the read, and `allocation-disputes.read` belongs only to `Operator`.

`DevelopmentJwt` does not check Access roles. The browser already reads JSON through `apiGet`, keeps the session epoch on the TanStack Query key, and aborts the previous session when that epoch changes.

## Decision

`/allocation-disputes` is a `DevelopmentJwt` screen. The default navigation label is `Allocation disputes`. The screen is available when a token is present, including a token with no `sub`. It does not consult `allocation-disputes.read` in the session.

The list is `GET /api/allocation-disputes`. The detail is `GET /api/allocation-disputes/{disputeId}`. Both use the existing `apiGet`. The page size stays 50 and is not part of the address. The address carries `page`, the approved `allocationId`, and `selected`. A missing page means page 1. `selected` is not sent on the list request. Previous page and Next page change only `page`, keep the approved filter and the selection, and push a history entry. Browser Back and Forward restore the page, the approved filter, and the selection. The address is the source of truth.

The filter uses React Hook Form and Zod. Empty input clears the filter. Any other value must be one exact GUID. Submitting the filter writes `allocationId`, sets the page to 1, and keeps `selected`. The form resets when the approved allocation id changes, including through history. Changing only `selected` or only the page does not reset an unsubmitted draft.

An invalid page or allocation id does not call the list. An invalid selection keeps the list and does not call the detail. An unknown address name is not forwarded and does not by itself block the list. The last valid page is 42949673, because the next offset has to fit in a 32-bit integer. The next page is enabled when that offset fits and either `totalCount` is greater than the offset or the current page is full. `totalCount` stays a decimal `int64`. A difference between the count and the items is not a corrupt payload. An empty page with count 0 says that no disputes are stored. It does not say that the allocation is missing.

The response shape is closed. A list is `items`, `page`, `pageSize`, and `totalCount`. A registration is `disputeId`, `allocationId`, `statement`, `actorSource`, `actorId`, and `recordedAt`. `page` and `pageSize` must echo the request. The detail `disputeId` must be the requested GUID, compared by value so hex case does not matter. With an approved filter, every item `allocationId` must match that filter by the same comparison. One mismatch rejects the page. Without a filter, items may name different allocations. `statement` and `actorId` are 1 to 500 and 1 to 256 UTF-16 code units and must already equal `string.Trim()` under `Char.IsWhiteSpace`. U+FEFF is kept at either edge. `actorSource` is `developmentJwt` or `productionCookie`. `recordedAt` uses the review-case comment `DateTimeOffset` range and is shown as the original text. The screen shows the stored actor source and actor id. The allocation id is text, not a link.

The query keys are `['allocation-disputes','list',epoch,page,allocationId]` and `['allocation-disputes','detail',epoch,disputeId]`. The detail does not depend on the current page. There is no `placeholderData`. A `StaleSessionError` renders nothing. HTTP 401 offers the existing Update access action and hides the records. HTTP 403 keeps the token and the screen. HTTP 404 is one message for an unknown or foreign dispute, and the list stays. A transport or response failure retries that query only. A failed refetch hides the registration shown by the previous successful detail read. The fields stay hidden for HTTP 401, 403, 404, 500, and a defective HTTP 200 while that error and its action remain. A later successful retry shows the verified registration. The list is unchanged by that detail failure.

`ProductionCookie` does not receive the route, the label, or a dispute capability. `capabilityForPath` stays unchanged, so a cookie call to an unmapped dispute path is rejected before `fetch`. A direct cookie URL uses the existing unavailable-page alert and does not mount this screen.

The screen does not register a dispute, resolve one, reverse an allocation, or recover a write.

## Consequences

An operator with a development token can browse stored registrations after the allocation has been reversed and can see the original statement, actor, and time. The cookie browser still has no dispute screen. Contract v2, the financial flags, and the cookie write allowlist stay as they were. Resolution, delegation, and the operational dashboard remain later Phase 8 work.

## Alternatives considered

Adding `allocation-disputes.read` to the cookie capability map would make the cookie browser call the API. This increment does not give `ProductionCookie` the screen. Sharing the review-case parser would couple two features for the same text and timestamp rules. The dispute feature keeps its own copy. Treating a divergent `totalCount` as a corrupt page would hide a valid page when the count and the items were read separately.
