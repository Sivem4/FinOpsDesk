# ADR 0026: Review case audit read

- Status: Accepted
- Date: 2026-11-05

## Context

ADR 0025 stores one `ReviewCaseAuditEntry` for a newly executed review open, resolve, or dismiss. The list and detail responses still omit the actor. There was no read route.

`review-cases.read` is held by Viewer. ADR 0018 already limits the allocation actor to `audit.read`. Reading the review actor through `review-cases.read` would widen that rule. Operator and ReversalApprover already have `audit.read`. Viewer does not. OrganizationAdmin alone has no financial permission.

## Decision

`GET /api/review-cases/{id}/audit` lists the persisted entries for one review case. The route uses `TenantMember` and exactly `FinancialEndpointClassification.Read("audit.read")`. It adds no permission, role, session-contract value, flag, or cookie write.

`OrganizationId` comes from `ICurrentOrganizationContext`. The existence check and the entry query each compare that organization. An unknown id and another organization's id both return HTTP 404 `reconciliation.review_case.not_found`. An existing case with no rows returns HTTP 200, an empty `items` array, and `totalCount` 0. A historical case may return only `Resolved` or `Dismissed`. The read does not invent `Opened` and does not copy current case, document, or Access fields into a missing payload.

Each item is the stored entry: `id`, `action`, `actorId`, `recordedAt`, and the action payload. Fields that the action does not store are JSON null. The case id stays in the route. The response omits `organizationId`, `reviewCaseId`, `concurrencyToken`, amounts, balances, and an actor display name. `action` is `Opened`, `Resolved`, or `Dismissed`. `reason` is `CompetingMatch` or `NeedsInvestigation`. `outcome` is `AllocationRecorded`, `NoActionRequired`, or `HandledOutside`. `sourceLabel` is `manual`, `exact-1`, or `smart-1`.

Only `page` and `pageSize` are accepted. They default to 1 and 50. Page size is 1 through 100. The offset is a 64-bit product and must fit in a 32-bit integer. A malformed id or an unsupported parameter returns HTTP 400 `reconciliation.review_case_audit.invalid_request`. Invalid, repeated, or overflowing pagination returns HTTP 400 `reconciliation.review_case_audit.invalid_pagination`.

Ordering is `RecordedAt` ascending, then `Action` ascending, then entry id ascending. An equal timestamp keeps `Opened` before the terminal action.

The existence check, the count, and the page are three separate `AsNoTracking` statements. They are not one snapshot. A terminal entry committed between the count and the page can make `totalCount` differ from the length of `items`. The read does not open a transaction. It does not require `sub` or a CSRF token. It does not change the case, the audit rows, or `rowversion`.

No index and no migration are added. One case still has at most one `Opened` row and one terminal row.

## Consequences

- Viewer and OrganizationAdmin alone cannot read this history when cookie reads are enabled. Operator and ReversalApprover can.
- `DevelopmentJwt` still ignores Access roles. A token without `sub` can read the stored actor.
- Deployed `Authentication:FinancialReadAuthorization` and `Authentication:FinancialWriteAuthorization` stay unset. The cookie write allowlist stays unchanged.
- There is still no audit UI. Phase 8 stays planned.

## Alternatives considered

### Authorize the route with `review-cases.read`

Rejected. That permission is what Viewer uses to open the case. It would expose the actor to every case reader.

### One transaction or snapshot for the count and the page

Rejected for this read. The allocation audit uses the same three statements, and a case has at most two legal rows. A snapshot would describe a guarantee this query does not take.

### A new index on `RecordedAt`

Rejected. The existing `(OrganizationId, ReviewCaseId, Action)` index finds the rows. Sorting two rows does not need another index.
