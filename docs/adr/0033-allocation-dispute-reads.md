# ADR 0033: Allocation dispute reads

- Status: Accepted
- Date: 2026-11-07

## Context

ADR 0032 stores an allocation dispute as a registration. The row is written only while the allocation is confirmed, and a later reversal does not remove it or rewrite its statement, actor, or time. Operators need to list and open those registrations without turning the read into a second view of the allocation's current financial state.

The existing non-unique index `IX_AllocationDisputes_OrganizationId_AllocationId` already supports the organization predicate and the optional allocation filter. A sort index on `RecordedAt` is not part of this increment.

## Decision

Reconciliation exposes two reads under `TenantMember`:

- `GET /api/allocation-disputes`
- `GET /api/allocation-disputes/{disputeId}`

Both are classified as `allocation-disputes.read`. The permission is granted only to `Operator` and is registered in `FinancialReadPermissionCatalog`. It is not a write, so it is not added to `FinancialWritePermissionCatalog` or `ProductionFinancialWriteActivation`. Session contract v2 does not advertise it. `OrderedReads` stays unchanged. Deployed financial flags and the three-write allowlist stay unchanged.

`ProductionCookie` allows the GET only when `FinancialReadAuthorization` is enabled, the membership is active, and the active roles include the permission. Viewer, ReversalApprover, OrganizationAdmin, and a member with no role receive HTTP 403. Roles still union. `DevelopmentJwt` keeps the current read rule: one consistent classification is enough, and a token with an organization claim can read without `sub`. Actor verification remains a write concern.

Every query predicate uses `OrganizationId` from `ICurrentOrganizationContext`. The list accepts only `page`, `pageSize`, and an optional `allocationId`. Each name may appear once. An unknown name, a duplicate, and an empty, empty-GUID, or unparseable `allocationId` return HTTP 400 `reconciliation.allocation_dispute.invalid_request`. A pagination value that is not an integer, is outside 1 to 100 for the page size, or produces an offset that does not fit in a 32-bit integer returns HTTP 400 `reconciliation.allocation_dispute.invalid_pagination`. The offset is calculated in a 64-bit integer before that check and before `Skip`. Defaults are page 1 and page size 50. A page past the rows returns an empty `items` array and the current `totalCount`.

`allocationId` is a filter, not an existence check. A foreign allocation, an unknown allocation, and an allocation in the caller's organization with no disputes all return HTTP 200 with `totalCount` 0. The detail route rejects every query parameter with `invalid_request`. A malformed path id returns HTTP 400. An unknown dispute and a dispute owned by another organization return the same HTTP 404 `reconciliation.allocation_dispute.not_found`.

The list and the detail reuse `AllocationDisputeResponse`. The list wrapper adds `items`, `page`, `pageSize`, and a 64-bit `totalCount`. The body is the stored registration: `disputeId`, `allocationId`, `statement`, `actorSource`, `actorId`, and `recordedAt`. It omits `OrganizationId`, `IdempotencyKey`, the allocation status and balances, and any future resolution. After reversal, both GETs return the original registration. Current allocation state remains available from the existing allocation reads. A SQL join could attach that state in one query. This read does not join, because the product question is what was registered, not what the allocation looks like now.

The queries are `AsNoTracking` projections of those columns. Ordering is `RecordedAt` ascending, then dispute id ascending. That is the SQL Server `uniqueidentifier` order, not `Guid` comparison in .NET. `ActorSource` is mapped by naming `DevelopmentJwt` and `ProductionCookie` explicitly. Any other stored value is a data inconsistency and fails the read. `LongCountAsync` and the page are separate statements. A dispute committed between them can make `totalCount` differ from `items`. `CancellationToken` flows from the endpoint to both statements. Neither GET opens a transaction or calls `SaveChangesAsync`.

No migration is added. The filter uses the existing `(OrganizationId, AllocationId)` index. The unfiltered organization list can use that index's leading column. The sort is not indexed.

## Consequences

- Dispute registration and dispute resolution remain separate. This increment does not decide a challenge or move money.
- The read is not an oracle for whether an allocation exists. A missing allocation and an allocation without disputes look the same on the list.
- A foreign dispute and a missing dispute look the same on the detail route.
- `totalCount` and `items` can diverge under concurrent registration. Callers must not treat that difference as a corrupt payload.
- UI, resolution, settlement correction, delegation, and the operational dashboard stay outside this increment.
- Phase 8 stays planned.
