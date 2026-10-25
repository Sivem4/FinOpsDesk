# ADR 0005: Exact match contract

- Status: Accepted
- Date: 2026-10-25

## Context

Phase 4 introduces deterministic matching between invoices and bank transactions. Confirming an allocation already exists as `ConfirmAllocationHandler`: it loads the authenticated organization's invoice and bank transaction, calls `Allocate` on each, inserts an `Allocation`, and commits once. SQL Server `rowversion` tokens on `Invoice` and `BankTransaction` reject a stale commit.

Matching needs a stable rule for which open records are an exact pair, which sets are ambiguous, and which records cannot participate. The existing `MatchSuggestion` type is a pending accept/reject model. It is not mapped in `ReconciliationDbContext`, and it cannot represent "no match" or an ambiguous set.

`Invoice` has `InvoiceNumber` and a derived outstanding amount. `BankTransaction` has an optional `Reference`, trimmed on write, and a derived unapplied amount. There is no separate payment-reference field, invoice status, or transaction type. Stored references must stay as they are, because import equality uses the existing trim.

## Decision

Exact matching is a pure domain operation, `ExactMatchRules.Evaluate`, algorithm version `exact-1`.

It receives the caller's organization id plus invoice and bank-transaction snapshots. It returns exact pairs, ambiguous groups, unmatched eligible records, and ineligible records. It does not change aggregates, write a suggestion, or authorize an allocation.

The match key is:

- normalized payment reference;
- currency;
- open amount.

For an invoice the open amount is the outstanding amount. For a bank transaction it is the unapplied amount. Original totals are not the match key. Decimal comparison has no tolerance. Booking date is not part of the key.

The payment reference is `Invoice.InvoiceNumber` compared with `BankTransaction.Reference`.

`exact-1` normalization, used only for comparison:

- null or whitespace-only input becomes null;
- leading and trailing whitespace is trimmed;
- letters are uppercased with `ToUpperInvariant`;
- internal whitespace is preserved;
- hyphens, slashes, and other punctuation are preserved;
- comparison of the normalized values is ordinal.

Examples: `INV-001` matches `inv-001` and ` INV-001 `. `INV 001` does not match `INV001`. `INV-001` does not match `INV001` or `INV/001`.

An invoice is eligible when it belongs to the supplied organization, its outstanding amount is greater than zero, and its normalized invoice number is present. A bank transaction is eligible on the same organization rule, a positive unapplied amount, and a present normalized reference. A missing reference or a non-positive open amount is ineligible. When both are true, the non-positive amount is the reason.

Classification:

- Exact: exactly one eligible invoice and one eligible bank transaction share the key. The suggested amount is that open amount.
- Ambiguous: at least one eligible invoice and one eligible bank transaction share the key, and either side has more than one record. That group produces no exact match.
- No match: an eligible record has no eligible counterpart for its full key. Two invoices that share a key, with no transaction on that key, are unmatched rather than ambiguous.
- Ineligible: missing reference or non-positive open amount.

A record from another organization is not ineligible. `Evaluate` throws `ArgumentException` and does not return that record's identifiers. This is defense in depth. Phase 4B queries must still restrict rows to the authenticated organization in SQL. The organization id continues to come from `ICurrentOrganizationContext`, not from the client.

Each invoice id may appear once in the invoice snapshots, and each bank transaction id may appear once in the transaction snapshots. A repeated id throws `ArgumentException` before grouping, whether or not the other fields match. The exception does not include that id. Distinct ids that share a match key remain ambiguous.

Results are ordered by normalized reference, currency, open amount, and then the ordinal `D` form of the relevant Guid. Order does not depend on input order. Each exact pair is emitted once. Result lists are copies wrapped so callers cannot cast them back to a mutable array.

Suggestions are not persisted in Phase 4A. `MatchSuggestion` is unchanged.

A later confirmation must not treat an `ExactMatch` as permission to allocate. It reloads the current invoice and bank transaction and uses `ConfirmAllocationHandler`, which rechecks currency and open balances and commits under the existing `rowversion` tokens.

Aggressive normalization, scoring, date windows, split payments, and automatic confirmation are deferred. Aggressive normalization belongs to Phase 4E. Candidate limits belong to Phase 4B.

## Phase 4C prerequisites

Safe confirmation is out of scope for this decision. Before a match can be confirmed:

- `ConfirmAllocationHandler` must stop leaving a tracked invoice modified when `BankTransaction.Allocate` fails. Today the handler applies the invoice allocation first, and `Handle_should_return_bank_transaction_failure_after_invoice_is_mutated_in_memory` records that the invoice stays changed even though `SaveChangesAsync` is not called. A later save on the same context could persist that invoice without the bank transaction or the allocation.
- Concurrency tests must cover two allocations of the same invoice against different bank transactions. The existing integration test covers one shared invoice and one shared bank transaction.
- Confirmation needs an idempotency decision. The handler generates a new `AllocationId` and has no idempotency key, so a sequential retry can create another allocation when the open balance still allows it.
- Confirmation must revalidate the current outstanding and unapplied amounts. A suggestion can be stale as soon as another allocation commits.

## Alternatives considered

### Removing internal spaces during normalization

Rejected for `exact-1`. `INV 001` and `INV001` would collapse, and so would other references that differ only by an internal space. Stronger folding is a Phase 4E decision.

### Classifying another organization's snapshot as ineligible

Rejected. An ineligible result would still carry that tenant's identifiers. The input is a contract violation.

### Persisting `MatchSuggestion` for every exact pair

Rejected for this phase. Nothing consumes a stored suggestion yet, and a stored row would go stale before confirmation. Confirmation already revalidates through `ConfirmAllocationHandler`.

### Weighted scoring in the first matcher

Rejected. Amount equality without the same reference is not a match, and an ambiguous key is not scored into a winner.

## Consequences

Positive:

- exact, ambiguous, unmatched, and ineligible outcomes are testable without a database;
- stored invoice numbers and bank references stay unchanged;
- an exact result cannot be produced from a key that has multiple eligible records on either side;
- tenant leakage through a classified foreign snapshot is rejected in the domain.

Negative:

- references that differ only by an internal space or by punctuation do not match until a later policy;
- Phase 4B must still apply an organization predicate in the query. The domain check does not replace it;
- `ConfirmAllocationHandler` can still hold a dirty invoice after a failed bank allocation. Phase 4C has to close that before confirmation is exposed.
