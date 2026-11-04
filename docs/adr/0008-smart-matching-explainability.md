# ADR 0008: Smart matching and explainability

- Status: Accepted
- Date: 2026-10-26

## Context

`exact-1` compares one normalized reference, one currency, and one open amount. It does not rank a reordered invoice number, a shared payment identifier, or a partial open balance. Operators still need those candidates, with a reason they can check, before they choose an allocation.

The open book is already loaded by `IExactMatchCandidateQueries` for `GET /api/matches`. Invoice snapshots carry the invoice number and outstanding amount. Bank snapshots carry the reference and unapplied amount. There is no counterparty, description, or invoice date on those snapshots. `BankTransaction.BookingDate` is not part of the snapshot and is not a match input. `SourceIdentity` is an import key, not a payment reference.

`MatchSuggestion` remains an unmapped domain type. Persisting suggestions, review queues, and automatic confirmation are later phases.

## Decision

Smart matching is a separate read-only operation, `SmartMatchRules.Evaluate`, algorithm version `smart-1`. It does not replace `exact-1`. `GET /api/matches`, `POST /api/matches/confirm`, and `POST /api/allocations` keep their contracts.

`GET /api/matches/smart` uses the `TenantMember` policy. `GetSmartMatchesHandler` reads `OrganizationId` only from `ICurrentOrganizationContext`. It calls `IExactMatchCandidateQueries.GetOpenCandidatesAsync` with `ExactMatchCandidateLimits.MaxPerSide` (5,000). The same organization filter, extra-row probe, ordering, cancellation token, and stored-money checks apply. A larger open book returns `reconciliation.matches.candidate_limit_exceeded` and HTTP 409. Inconsistent stored money returns `reconciliation.matches.inconsistent_money` and HTTP 500. Those codes are shared with exact matching. The ProblemDetails detail on the smart route says smart matching was not run. The exact-match route keeps its original detail. Neither route matches a partial book.

The endpoint accepts no query parameters. Any query string returns HTTP 400 `reconciliation.matches.smart.unsupported_query`. A client organization id in a query string or header does not select the tenant. There is no page size, weight, threshold, or strategy parameter.

`smart-1` emits a suggestion only when the references support one reference signal. Signals do not stack. The score is the winning reference signal plus the amount signal:

- `ReferenceExact` is 70 when the `exact-1` normalized references are equal;
- otherwise one qualified signal is 60: `ReferenceTokenSetEqual`, then `CompactFormEqual`, then `NumericIdentifierEqual`;
- `AmountExact` adds 25 when the open amounts are equal;
- `AmountDiffers` adds 0 when they are not.

The possible scores are 95, 85, 70, and 60. `scoreScale` is `points`. The score is a deterministic heuristic ranking measure, not a probability. The rank is the suggestion's one-based position in the returned ordered list. The response has no percentage.

Currency is not a scored signal. Different currencies produce no suggestion. The suggested amount is the smaller positive open amount. It is advisory. It is not validated as an allocation and it is not an authorization token.

Token-set equality requires the same token multiset, at least two tokens, one all-letter token of at least two characters, and one whole-token number of at least four digits that is not a year. A year is a whole token of four digits from 1900 through 2099. `RENT MAY` does not match `MAY RENT`. Compact form and numeric equality require a maximal digit run of at least six digits inside one token. Separators do not join short groups, so `12-34-56-78` does not match `12345678`. For every signal except `ReferenceExact`, the year-token multisets must be identical, including a year that appears on only one side. `FV/2025/001234` does not match `FV/2026/001234`. `INV-001234` does not match `FV/2026/001234`.

References are trimmed before the length check. A trimmed value longer than 256 characters, or more than 16 tokens, produces no smart candidate and sets `coverageLimited`. `PaymentReferenceNormalizer` is not changed. `exact-1` still keeps internal spaces and punctuation.

Candidate generation uses a blocking key of signal kind, currency, and value. A key with more than 32 rows on either side is discarded and `coverageLimited` is set. Candidates for one invoice are scored fully, then limited to 16. The discarded scores are retained as a small set so a closer discarded score can still mark the retained suggestion contested. The output keeps the top 200. `truncated` is true only for that final cut. Order is score descending, then the ordinal `D` form of the invoice id, then the bank transaction id.

`contested` is true when another retained suggestion shares the invoice or the bank transaction and the scores differ by at most 10, or when any discarded score for that invoice or bank transaction is within 10. It is decided before the 200-row cut. `contested` false does not prove the suggestion is unique when `coverageLimited` is true. An empty suggestion list with `coverageLimited` true does not prove that no match exists.

`exactMatchAmbiguous` repeats the `exact-1` ambiguity of the normalized reference, currency, and open amount. Smart matching still returns those rows. It does not hide them, and it does not turn them into a confirmation.

`ContestedCandidates` and `ExactMatchAmbiguous` are explanation signals with 0 points. A lower reference signal that also matched is recorded at 0 points. The points still sum to the score.

The response is advisory. The two candidate reads are separate statements under the existing read-committed behavior. The result can be stale before a person confirms anything. Confirmation remains `POST /api/matches/confirm` or `POST /api/allocations`, which reload the current rows. There is no smart-confirm route, no persisted suggestion, and no new table, column, index, or migration.

Changing the scoring or the normalization must introduce a new version such as `smart-2`. `smart-1` is not redefined in place.

## Alternatives considered

### Extending `GET /api/matches` with a strategy query parameter

Rejected. Exact clients would have to ignore new fields, and a parameter could silently change which algorithm ran. A separate route keeps `exact-1` stable.

### One shared error message for exact and smart failures

Rejected for the detail text. The shared codes stay stable, and the exact-match detail still says exact matching. The smart route states that smart matching was not run.

### Persisting suggestions or confirming the top score

Rejected. The score is a heuristic over incomplete reference data. A suggestion is not financial authority. Review and automatic matching belong to later phases.

## Consequences

- `GET /api/matches/smart` is read-only and tenant-scoped through the existing candidate query;
- the open-book limit remains 5,000 rows per side;
- blocking stops at 32 rows per currency-qualified key, 16 candidates per invoice, and 200 returned suggestions;
- `coverageLimited` and `truncated` must be shown with the score;
- `exact-1` confirmation and explicit-amount allocation are unchanged;
- a future scoring change requires `smart-2`;
- the generated OpenAPI document does not yet include Bearer security metadata for `GET /api/matches/smart` or the existing `GET /api/matches`. Runtime authorization still uses `TenantMember`. Describing that requirement in OpenAPI is a later authentication and API documentation change. This decision does not change the global OpenAPI setup. ADR 0024 records that later description for the active host composition.
