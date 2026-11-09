# ADR 0043: Allocation dispute queue keyset

- Status: Accepted
- Date: 2026-11-09

## Context

`GET /api/allocation-disputes/queue` already returns one offset page and a separate `COUNT_BIG` for `needs-decision` or `needs-correction`. ADR 0033 keeps that count and page as two statements. A deep offset still reads and discards every preceding qualifying row. The queue is the only read in this increment that changes shape. The registration list, writes, audit, idempotency, recovery, deployment flags, and the cookie allowlist stay as they are.

## Decision

The same route accepts two modes.

Without `pagination` and without `cursor`, the response stays `items`, `page`, `pageSize`, and `totalCount` as `int64`. Parsing and unknown or repeated parameters stay `reconciliation.allocation_dispute.invalid_request`. After numeric parsing, a `page` below 1, a `pageSize` outside 1–100, or a zero-based offset that does not fit in a 32-bit integer returns `reconciliation.allocation_dispute.invalid_pagination`.

`pagination=cursor` returns `items`, `pageSize`, `nextCursor`, and `previousCursor`. Both cursor fields are required and are a string or JSON null. This mode does not run `COUNT_BIG`. A missing cursor is the first page. `page` together with cursor mode, or `cursor` without `pagination=cursor`, is `invalid_request`. In cursor mode every invalid `pageSize` is `reconciliation.allocation_dispute.invalid_pagination`, including a non-numeric value, an empty value, a repeated value, and a value outside 1–100. Offset mode keeps the distinction above: a non-numeric `pageSize` stays `invalid_request`. Every other cursor failure is `invalid_request` before the queue read, and the problem body does not repeat the submitted cursor.

Cursor v1 is unpadded Base64Url, at most 64 characters, and exactly 29 bytes: version, direction, queue variant, `RecordedAt.Ticks`, the offset in whole minutes from −840 to 840, and 16 identifier bytes. `Guid.Empty` is a valid position. The cursor carries no organization. It is the boundary of the next read, not a lookup key. A cursor for the other queue variant is rejected before SQL. A cursor built in another organization is applied only inside the caller's organization and can return rows or an empty page.

Forward order is `RecordedAt ASC, Id ASC`, with `RecordedAt > @at OR (RecordedAt = @at AND Id > @id)`. Backward uses the strict reverse predicate and descending order, then reverses the kept rows. The comparison stays in SQL Server. Parameters are `datetimeoffset` and `uniqueidentifier`. The query reads `pageSize + 1` and drops the extra row before it mints cursors. The extra row proves another page in the direction just read. The opposite cursor is only a position and may return an empty HTTP 200.

The browser keeps an explicit `page` on the offset contract. A dashboard link has no `page` and opens cursor mode. `Back to start` is shown for a recognized `queue` whenever the address contains `cursor`, including while the page is loading, after a read error, and when the cursor value itself is invalid. It removes `cursor` and `page`, keeps `queue` and every `selected` value, and loads the first cursor page. The queue cache key keeps the prefix `['allocation-disputes', 'queue', epoch, variant]` and adds the mode and page or cursor after it. The work-summary observer uses `allocationDisputeWorkSummaryFreshMs = 30_000` on the dashboard and on the dispute page. Other queries keep the global client options. Cursor mode shows the matching summary count as a decimal string. A failed summary leaves the list in place and marks the count unavailable.

### Measurement

The gated test `AllocationDisputeQueueMeasurementTests` runs only when `FINOPSDESK_MEASURE_DISPUTE_QUEUE=1`. The default suite does not remeasure. It uses the existing SQL Server fixture, so the database name is `FinOpsDesk_IT_<guid>`, and it does not clear instance buffers or touch the shared `FinOpsDesk` database. Synthetic rows share one confirmed allocation. Scenario D stores 20,000 disputes, with an upheld resolution and no correction on every 50th row: 19,600 `needs-decision` and 400 `needs-correction`. Scenario C stores 8,000 disputes: the first 6,000 are `needs-correction`, the next 1,500 are `needs-decision`, and the last 500 are `not_upheld`. Each timed shape uses one warmup and five repeats. The median is the middle value. `STATISTICS IO` and `STATISTICS TIME` supply logical reads and elapsed time. A separate `STATISTICS XML` execution supplies the actual plan. Plans and `results.md` are written under `D:\Projects\FinOpsDesk-review\phase-9b-measurements`.

The compared page is 50 rows. The index gate uses D decision 1 and 5001, D correction 1 and 301, C decision 1 and 301, and C correction 1 and 5001. Position 51 is measured separately as the real second page and is not part of that gate. Offset and keyset returned the same 50 ids at every compared position, including position 51. The baseline for a deep page is the shape with the lower median logical reads. Equal reads use the lower median elapsed time. A remaining tie uses keyset without the candidate. Reads and elapsed time are both compared with that one baseline. `COUNT_BIG` is excluded from the ratio.

A deep page passes the ordered-range part of the gate only when the candidate index is ordered and its seek keys contain a start or end range on `RecordedAt` or `Id`. An `Index Seek` name, `Ordered="1"`, or an equality seek on `OrganizationId` does not pass that part. A predicate on `RecordedAt` or `Id` that remains under the index scan is residual. A missing `ActualRowsRead` or lookup `ActualExecutions` is reported as `absent`; this run contained both attributes on every saved plan, so no cell is `absent`. Lookup counts are clustered key lookups (`Lookup="1"`). Plans for offset, keyset, the narrow index, and the include index are saved beside `results.md`.

The narrow candidate is the unique nonclustered index `(OrganizationId, RecordedAt, Id)`. C decision page 1 uses that index as an equality seek on `OrganizationId`, reads 6,051 index rows, and performs 6,051 clustered lookups. D decision position 5001 also seeks only `OrganizationId` (`Prefix ScanType="EQ"`). `RecordedAt` and `Id` stay in a residual predicate. That plan reads 5,154 index rows and then looks up 52 rows. It is not an ordered range from the cursor boundary, so it does not pass the ordered-range gate. The repeat used `INCLUDE (AllocationId)`. `Statement` and `ActorId` stayed out. The include did not remove the lookups, because the page still fetches `Statement`, `ActorSource`, and `ActorId` from the clustered index. Correction plans stayed on the clustered scan or applied the boundary as a residual filter; none had a cursor-boundary range.

Neither candidate is migrated. A deep page must pass the ordered-range rule above, use at most 50% of the baseline logical reads, and use at most 80% of the baseline elapsed time. No deep page had a cursor-boundary range. C decision page 1 also exceeded 110% of the keyset-without reads. The measured medians from the recorded run are:

| Scenario | Queue | Position | Offset reads / ms | Keyset reads / ms | Narrow reads / ms | Include reads / ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| D | needs-decision | 1 | 691 / 13 | 693 / 15 | 269 / 0 | 269 / 0 |
| D | needs-decision | 51 | 796 / 12 | 693 / 14 | — | — |
| D | needs-decision | 5001 | 11214 / 18 | 693 / 10 | 302 / 2 | 312 / 2 |
| D | needs-correction | 1 | 686 / 2 | 688 / 3 | 688 / 4 | 688 / 3 |
| D | needs-correction | 51 | 786 / 2 | 688 / 4 | — | — |
| D | needs-correction | 301 | 1286 / 3 | 688 / 3 | 688 / 3 | 688 / 4 |
| C | needs-decision | 1 | 13064 / 15 | 13066 / 17 | 31051 / 26 | 31063 / 21 |
| C | needs-decision | 51 | 13167 / 17 | 691 / 4 | — | — |
| C | needs-decision | 301 | 13682 / 14 | 691 / 3 | 302 / 3 | 315 / 2 |
| C | needs-correction | 1 | 772 / 13 | 774 / 10 | 774 / 12 | 774 / 11 |
| C | needs-correction | 51 | 872 / 11 | 774 / 12 | — | — |
| C | needs-correction | 5001 | 10772 / 19 | 773 / 7 | 773 / 7 | 773 / 6 |

Position 51 is outside the index gate, so the candidate columns are not measured there. For D decision position 5001 the better baseline is keyset at 693 reads and 10 ms. Narrow is 302 reads and 2 ms, but its plan is the organization-equality seek plus the residual boundary described above, so the ordered-range rule fails before the ratio is accepted. D correction position 301 stays on a residual clustered scan at 688 reads, the same as keyset and above 50% of that baseline. C decision page 1 is 31,051 reads against a keyset baseline of 13,066, above the 110% first-page limit, with 6,051 lookups. Include follows the same split.

Separate `COUNT_BIG` medians were D decision 108 reads / 1 ms, D correction 154 / 2, C decision 136 / 2, and C correction 155 / 4. The narrow index used 171 pages. The include index used 227 pages. Five single-row inserts had median elapsed times of 0.519 ms with the narrow index, 0.616 ms with the include index, and 0.364 ms without an index. The narrow and include samples are above the no-index insert. That sample is not an acceptance gate. The page plans are the reason to keep the index out.

Keyset without a new index is the shipped read. On the deep gate pages, its logical reads stay near the first keyset page while offset grows: 693 against 11,214, 688 against 1,286, 691 against 13,682, and 773 against 10,772.

The measured second page is position 51. Combined logical reads for the three journeys, using the page medians above and the `COUNT_BIG` medians, are:

| Scenario | Queue | Page 1 keyset | Page 2 keyset | Fresh keyset | Keyset plus both summary counts | Offset with a count on each page |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| D | needs-decision | 693 | 693 | 1,386 | 1,648 | 1,703 |
| D | needs-correction | 688 | 688 | 1,376 | 1,638 | 1,780 |
| C | needs-decision | 13,066 | 691 | 13,757 | 14,048 | 26,503 |
| C | needs-correction | 774 | 774 | 1,548 | 1,839 | 1,954 |

Fresh keyset is the two page reads and no summary. Keyset plus both summary counts adds the scenario's decision count and correction count once. Offset adds that queue's own count to each page. A cursor entry that already has a fresh summary for the epoch reads one page and no summary. The next cursor reads another page and still no summary, including after 30 seconds on the open screen. A cold, stale, new-epoch, or invalidated entry reads one summary and one page, then the next cursor reads only the page. The summary failure path leaves the page usable and does not turn the count into a number.

## Consequences

OpenAPI documents both HTTP 200 schemas with `oneOf`. Offset `totalCount` stays `int64`. Cursor fields stay nullable strings. The permission sentence stays on the operation.

No migration is added. Applied migrations stay unchanged. The queue predicates and the summary predicates stay the two existing counts. An independent reversal still removes a dispute from `needs-correction` only.

Phase 9 remains Planned. Rate limits and the security review are not part of this increment.

## Limitations

`STATISTICS TIME` reports whole milliseconds, so a sub-millisecond median is 0. The plans are one actual execution after the timed repeats, not a median plan. The insert sample is five single-row inserts on the measurement database and is not a registration-throughput test. Tie ordering and a small tenant are covered by correctness tests, not by the index gate. A cursor does not see a row that leaves the queue between requests, and the opposite cursor can then return an empty page. The default test run does not repeat this measurement.
