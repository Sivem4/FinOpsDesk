# ADR 0041: Operational dashboard

- Status: Accepted
- Date: 2026-11-09

## Context

Review cases already have a summary, and allocation disputes already have a registration list. The operator still has no one screen that shows which cases are open and which disputes need a decision or a full reversal. A counter that included every upheld dispute without a link would count allocations that can no longer be reversed.

## Decision

`/dashboard` is the operational screen. Review-case counters come from `GET /api/review-cases/summary`. The open-case link is `/review-cases?status=Open`. Reason counts do not get their own address because the review list has no reason filter. Delegation does not change those counts.

`GET /api/allocation-disputes/summary` and `GET /api/allocation-disputes/queue` require `allocation-disputes.resolution.read`. The summary and the queue use the same predicates. `needs-decision` is an organization dispute with no decision, whatever the allocation status. `needs-correction` is an `upheld` dispute with no correction whose allocation in that organization is `Confirmed`. An allocation reversed independently is excluded. The queue is ordered by `RecordedAt`, then dispute id. `totalCount` is a full `int64`. The UI keeps that precision and does not use a JavaScript number. The links are `/allocation-disputes?queue=needs-decision` and `/allocation-disputes?queue=needs-correction`. `GET /api/allocation-disputes` keeps `allocation-disputes.read` and rejects `queue` before it reads rows.

`CookieFinancialRouter` registers `/dashboard` behind the existing `review-cases.read` gate. The dispute section is `DevelopmentJwt` only. The cookie review section is shown only when `review-cases.read` is verified. Selecting a dispute, changing page, and moving back or forward keep `queue`. `Show the full register` leaves the queue. Applying the allocation filter also leaves the queue, because the queue read has no allocation filter. The queue request stays the queue read from the dashboard link through the next page and the detail. Its query key includes the session epoch and `session.readiness`. An HTTP 401 or 403 hides the counts and uses `clearSummaryCountsForAccessDenial`. Contract v2 does not advertise dispute or assignment reads.

A verified registration refreshes the decision queue. A verified decision refreshes both dispute queues. A verified correction and a verified ordinary reversal, including replay, refresh the correction queue. Opening and closing a review case refresh the review summary. Each refresh is bound to the epoch of that operation.

## Consequences

The counters and `totalCount` can differ across separate requests because they are not one snapshot. Agreement is asserted on stable data. The cookie allowlist, flags, roles, and capability map stay unchanged. `Operator` still does not receive `allocations.reverse`.
