# ADR 0027: Review case open idempotency

- Status: Accepted
- Date: 2026-11-05
- Follow-up: `DevelopmentJwt` opens a case from `/review-cases` and keeps one tab-local attempt in `finopsdesk.review-case-open-attempt`. The descriptor stores the organization id from `org_id`, the idempotency key, the exact body, and the normalized fields. It does not store a bearer token. The record is schema version 1 and is not bound to an issuer or subject, so two operators of the same organization can see it in the same tab. `ProductionCookie` does not mount the form or that recovery panel, and the client refuses the POST before fetch. Phase 8 stays planned.

## Context

ADR 0009 deduplicates `POST /api/review-cases` only while a case for that subject is open. After resolve or dismiss, the same request can open another case. That behavior is subject repetition. It is not a durable idempotency key.

A caller that retries one open attempt after a lost response, or after the case has been closed, needs the retry to name the original case. A later, deliberate attempt for the same subject still needs to be able to open a new case.

## Decision

`Idempotency-Key` on `POST /api/review-cases` is optional. Only a missing header uses the open-subject path. A present header must be exactly one non-empty UUID in `D` format. Leading or trailing `Char.IsWhiteSpace` is rejected before `Guid.TryParseExact`, because that parser trims those characters. An empty value, whitespace, several values, `Guid.Empty`, or another format returns HTTP 400 `reconciliation.review_case.idempotency_key_invalid`. The value is not trimmed into a valid key.

A valid key is stored in `reconciliation.ReviewCaseOpenAttempts` with the organization, the resulting review case, and the normalized reason, invoice id, bank transaction id, source label, and note. Normalization uses `ReviewCaseSource.Parse` and `ReviewCase.NormalizeNote`. Payload comparison is ordinal .NET equality. U+FEFF is preserved. The only uniqueness constraint on `(OrganizationId, IdempotencyKey)` is the composite primary key `PK_ReviewCaseOpenAttempts`. SQL Server error 2627 that names that constraint is the key conflict. The open-subject index remains separate.

The new case, its `Opened` audit entry, and the attempt commit in one `SaveChangesAsync`. A different key whose normalized request matches an already open case inserts only the attempt. A different normalized request for that open case returns HTTP 409 `reconciliation.review_case.open_request_conflict` and does not insert an attempt when that rejection happens before commit. The same key with a different payload returns HTTP 409 `reconciliation.review_case.idempotency_payload_conflict`. The same key and payload return HTTP 200 and the current case, including after resolve or dismiss, without a new case, audit entry, actor, decision, or `rowversion`. HTTP 201 and `Location` are used only when that request creates the case. The response body does not gain a `created` field.

The server actor is required before the attempt is read, including on replay. The key is not a credential. `OrganizationId` comes from `ICurrentOrganizationContext`. An initial miss does not prove the key is free. Before a document 404, and again after reading the open subject, the handler reads the attempt. After an open-subject unique violation it reads the attempt, then the open winner, then the attempt again. A stored attempt decides replay or payload conflict even when its case is already closed. A failed save is not reused. A later read or a single attempt insert uses a new scope. A primary-key conflict on that insert is one more read and a classification, not another insert. An audit-index violation is not success. A controlled rejection before commit does not store that request's attempt. A commit whose HTTP response is lost can leave the key bound.

There is no backfill. A case that already existed does not gain an attempt or an invented `Opened` entry. Resolve and dismiss are unchanged. ProductionCookie still rejects these writes before the handler. The allowlist, flags, and capabilities are unchanged.

## Consequences

- Clients that omit the header keep today's open-subject repetition.
- A retained key survives the case being closed.
- A new key can still open a new case after the previous one is closed.
- The attempt table is not a generic idempotency framework.
- Phase 8 stays planned.

## Alternatives considered

### Require the header

Rejected. Existing clients and the ADR 0009 contract omit it.

### Store the key on `ReviewCase`

Rejected. One case can be the result of more than one matching key, and a keyless open has no key.

### A second unique index on the same columns

Rejected. The primary key is the uniqueness constraint. A second index would duplicate it and split the SQL error identity.
