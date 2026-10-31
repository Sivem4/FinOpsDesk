# Frontend development

The operations workspace is a Vite application in `src/frontend`. It shows the invoice register from `GET /api/invoices`, the bank transaction register from `GET /api/bank-transactions`, CSV import at `/imports`, the reconciliation queue at `/reconciliation`, and allocation history at `/allocations`. It does not issue tokens. The import screen sends the selected file to the existing import endpoints and does not change amounts in the browser. The reconciliation screen reads `GET /api/matches` and `GET /api/matches/smart`. An exact match can be confirmed with `POST /api/matches/confirm`. A Smart-1 suggestion can start one explicit-amount allocation with `POST /api/allocations`. **Choose records** on that same screen can browse or exactly search the invoice and bank-transaction lists, then open that same allocation dialog. The invoice register and the bank transaction register do not start an allocation.

Production browser authentication is Phase 7. The development token screen is compiled only into the Vite development build. A production build shows that browser sign-in is not configured.

## Prerequisites

- .NET 10 SDK
- Node.js `^22.22.2`, `^24.15.0`, or `>=26.0.0` (this repository was verified with Node.js 24.18.0)
- npm 11
- SQL Server LocalDB on Windows
- A trusted ASP.NET Core HTTPS development certificate:

```powershell
dotnet dev-certs https --trust
dotnet dev-certs https --check --trust
```

## Database

The API reads `ConnectionStrings:FinOpsDesk`. The Development default is LocalDB database `FinOpsDesk`. The host does not apply migrations at startup.

```powershell
dotnet tool restore
dotnet ef database update `
  --project src/backend/Modules/FinOpsDesk.Modules.Reconciliation/FinOpsDesk.Modules.Reconciliation.csproj `
  --startup-project src/backend/FinOpsDesk.Api/FinOpsDesk.Api.csproj `
  --context ReconciliationDbContext
```

An empty database is valid. The register then shows that the organization has no invoices.

## API signing key

Set these variables in the same PowerShell session that starts the API. Do not commit them and do not put them in a `VITE_` variable.

```powershell
$env:Authentication__JwtBearer__Authority = ''
$env:Authentication__JwtBearer__Issuer = 'https://finopsdesk.local'
$env:Authentication__JwtBearer__Audience = 'finopsdesk-api'
$env:Authentication__JwtBearer__SigningKey = '<at least 32 UTF-8 bytes from a password manager>'
```

`Issuer` is the token issuer. It is not the Kestrel URL. The local issuer must be exactly `https://finopsdesk.local`. Leave `Authority` empty when `SigningKey` is set. Do not reuse `finopsdesk-integration-test-signing-key`.

## HTTPS

Start the API with the `https` launch profile:

```powershell
dotnet run --project src/backend/FinOpsDesk.Api --launch-profile https
```

Kestrel listens on `https://localhost:7257` and `http://localhost:5185`. With this profile, `http://localhost:5185` responds with `307` to `https://localhost:7257`. The Vite proxy therefore targets only `https://localhost:7257` and keeps `secure: true`. The browser talks to the Vite origin, so the API does not need CORS.

The package engine range is the intersection of the resolved lockfile. `react-router` requires Node `>=22.22.0`. `jsdom` requires `^22.22.2 || ^24.15.0 || >=26.0.0`. Node 22.19, 23, 24.0 through 24.14, and 25 are outside that intersection. `node --use-system-ca` was checked on Node.js 24.18.0 against a trusted ASP.NET development certificate. Node still failed the handshake with `DEPTH_ZERO_SELF_SIGNED_CERT`. Do not set `NODE_TLS_REJECT_UNAUTHORIZED=0` and do not set the proxy `secure` option to `false`.

The verified trust path exports the public certificate only, from `Cert:\CurrentUser\Root`, to a file outside the repository:

```powershell
scripts\dev-frontend.ps1
```

That script writes `%USERPROFILE%\.finopsdesk\localhost-dev.crt`, sets `NODE_EXTRA_CA_CERTS` to that file, and runs Vite. The export uses `X509ContentType.Cert`, which does not include the private key. The script stops if the store object reports a private key.

`GET https://localhost:7257/health` through this trust path returns `200` and `Healthy`.

## Development token

In the same session that holds the signing variables:

```powershell
$organizationId = [guid]::NewGuid()
scripts\dev-token.ps1 -Subject operator-1 -OrganizationId $organizationId
```

Stdout is one JWT. The script does not write a file and does not call the API. Claims are `iss`, `aud`, `sub`, `org_id`, `nbf`, and `exp`. The default lifetime is 60 minutes and the maximum is 8 hours. `sub` is required and is limited to 256 characters. `org_id` is a non-empty GUID. There is no membership store: data created with this token belongs to that organization.

Paste the token into **Development access token**. It is held in memory only. Reload discards it. Log out aborts in-flight requests and clears the TanStack Query cache. Replace token starts a new session before the next response can be stored. A token is not written to `localStorage`, `sessionStorage`, or the URL. When `exp` passes, the screen asks for a new token. The API remains the authority: a rejected token is HTTP 401, and a token without a usable organization id is HTTP 403.

The JWT stays valid until `exp` even after log out. This development flow has no revocation list.

## CSV import

Open `http://localhost:5173/imports` after entering the development token. Choose invoices or bank transactions, enter a source system, and select the original CSV. The screen shows the required headers before submission. Invoice columns are `ExternalId,InvoiceNumber,Amount,Currency`. Bank transaction columns are `ExternalId,BookingDate,Amount,Currency,Reference`, and `BookingDate` uses `yyyy-MM-dd`. Files larger than 2 MiB are blocked in the form. The API remains the authority for the multipart request, the header, and every amount.

Submit with **Import file**. The browser sends `multipart/form-data` fields named `file` and `sourceSystem`. The file bytes are not rewritten. Surrounding spaces are removed from the source system; its letter case is not changed in the browser. The server trims it and stores uppercase.

A new import returns HTTP 201. The same bytes, import type, and normalized source system return HTTP 200 with `replayed` true and the stored batch. `Completed`, `CompletedWithErrors`, and `Rejected` are persisted batch results. HTTP 400 means the file was structurally rejected and no batch was saved. If the request ends without a response, the screen says the outcome is unknown. Submit the same file again manually. Reloading the browser discards the selected file, so it must be selected again. There is no import-history endpoint, so a finished result cannot be loaded by batch id.

A confirmed invoice import or replay refreshes the invoice register and the reconciliation queries for that session. Open **Invoices** to see rows from `GET /api/invoices`. A verified bank-transaction import refreshes the reconciliation queries. It also refreshes the bank transaction register when `importedRows` is greater than zero, including `CompletedWithErrors` and a replay of a batch that recorded rows. A `Rejected` bank import and an import whose outcome is unknown do not refresh that register. A bank import does not refresh the invoice register. Open **Bank transactions** to see `GET /api/bank-transactions`. That list is an observation. It does not reserve a balance, and it does not choose an invoice for allocation. The invoice and bank-transaction APIs accept optional exact `invoiceNumber` and `reference` filters. A missing filter returns the normal page, including a bank transaction with no reference. The register screens do not send those filters and do not start an allocation. Date filters and a reversal screen are not part of this register.

## Reconciliation

Open `http://localhost:5173/reconciliation`. **Exact** calls `GET /api/matches` and shows `exact-1` pairs, ambiguous groups, unmatched rows, and ineligible rows in the returned order. **Smart** calls `GET /api/matches/smart` with no query string and shows `smart-1` suggestions. Scores are points. The screen does not turn them into percentages. Only the selected view requests its read endpoint. A book larger than 5,000 open rows on either side returns HTTP 409 and is not matched. Inconsistent stored money returns the matching HTTP 500 problem and is not matched. Exact and Smart are separate reads.

**Confirm** is available only on an exact-match row. Ambiguous groups, unmatched rows, ineligible rows, and smart suggestions have no confirmation action. The dialog shows the reference, the amount from that read, and both identifiers. The displayed pair is not reserved. The books can change before the server accepts the request, so that amount can be stale. The request body is only `invoiceId` and `bankTransactionId`. It does not send an amount, a currency, an actor, or an organization id. `Idempotency-Key` is one new UUID in `D` format for a new confirmation. The server calculates the full current open amount and checks that pair. It does not check whether the pair is still the only exact match in the book.

HTTP 201 with `replayed` false means the allocation was recorded. HTTP 200 with `replayed` true means the same key and the same pair were already recorded. The screen shows the allocation id and the amount from that response. A verified 201 or 200 refreshes the invoice register, the bank transaction register, and both reconciliation reads for the current session. An unreadable success or a lost response keeps the same key and does not refresh the caches as a confirmed success. The operator can send that same request again. The screen does not create a new key after an error. The attempt stays in memory only while the Reconciliation page stays mounted. Reloading the browser or leaving `/reconciliation` discards it. The application does not keep an unresolved attempt across that navigation. Opening the page again can start a new confirmation with a new idempotency key, and that new operation cannot tell whether the earlier POST was saved. Refreshing the list is a GET and does not prove the result of a particular POST.

**Allocate** is available on a Smart-1 suggestion row. Exact-1 rows keep **Confirm** and do not gain a second allocation action. The dialog reads `GET /api/allocations/context` for that pair. The read does not reserve either balance. A new attempt starts only after that context loads, the currencies match, both remaining amounts are positive, and the operator confirms an amount that does not exceed the smaller remaining balance in that read. The suggestion amount only fills the text field. Exact-match confirmation remains an in-memory attempt and is unchanged.

**Choose records** stays available on Exact, on Smart, and when either matching read fails or is empty. It does not depend on a Smart-1 result. The dialog has separate invoice and bank sections. Each section browses its list when the search box is empty. Search is submitted explicitly. A nonempty term is trimmed and sent once as `invoiceNumber` or `reference`; an empty submission omits that parameter. The browser does not change letter case, and a term longer than 256 characters is rejected locally without a request. No match is an empty page. Duplicate invoice numbers and duplicate references stay selectable by record id. A null bank reference is shown as None and can be selected from the unfiltered list. A fully allocated row stays visible and selectable. Page size is 25, 50, or 100, and changing the term or the page size returns that section to page 1. Choosing one side does not clear the other side. Closing the dialog does not clear the selection and does not abandon a stored allocation attempt.

When both records are selected, **Review balances** opens the existing allocation dialog with no suggested amount and no Smart-1 score. List balances are only the latest list observation. The allocation dialog loads `GET /api/allocations/context` for those two ids and applies the same amount, currency, and remaining-balance checks. The selector does not create an idempotency key, write `sessionStorage`, or call `POST /api/allocations`. An unresolved stored attempt takes precedence: **Choose records** opens the existing recovery dialog instead of starting another attempt. Retry still sends the original key and body, including when the context read fails, the current balances are zero, or the pair is absent from both lists. A session epoch change clears the incomplete selection immediately. It does not delete a stored attempt. A verified allocation still invalidates the current session's invoice, bank-transaction, match, allocation-history, and allocation-context queries, including list searches and filtered history. An unknown outcome does not. A verified exact-match confirmation invalidates the same history prefix. CSV import does not, because it creates no allocations.

`/allocations` reads `GET /api/allocations`. The main navigation opens the unfiltered tenant history. A selected invoice and a bank-transaction row open the same screen with that record's id in the query string. Both ids may be present together. Empty, empty-GUID, repeated, and malformed ids are rejected in the browser and are not sent. Page size is 25, 50, or 100. The browser does not re-sort the page. Confirmed and reversed rows both show the original positive amount. Invoice numbers and bank references are labels; the record ids distinguish duplicates, and a null reference is shown as None. **View audit** loads `GET /api/allocations/{allocationId}/audit` for that row only. An empty audit page stays empty. The actor comes from the audit entry. A confirmed row can open **Reverse allocation**. A reversed row has no new reversal action. Opening that action reads `GET /api/allocations/{allocationId}/reversal-context`. It does not post. History browsing does not write `finopsdesk.explicit-amount-attempt` and does not call `POST /api/allocations`. It reads that key only to show a link to Reconciliation when the stored attempt belongs to the current organization. Query keys include the session epoch. A failed refetch does not present retained rows as the current page.

A new reversal starts only after the operator confirms a reason and a fresh reversal-context read still says `confirmed` for the same allocation. The browser trims the reason with the same whitespace classes as .NET `Char.IsWhiteSpace`, including U+0085, and it does not trim U+FEFF. An empty reason is rejected. A reason longer than 500 UTF-16 code units is rejected and is not truncated. The module `reversal-attempt.ts` then writes `finopsdesk.reversal-attempt` and reads it back before `POST /api/allocations/{allocationId}/reverse`. The stored `requestBody` is the exact JSON object `{"reason":"..."}` sent on every attempt. The `Idempotency-Key` is a UUID in format D and is not generated again for that attempt. The descriptor also keeps the allocation, invoice, and bank transaction ids, the organization id for a local check, the normalized reason, the original amount and currency, and `ready` or `uncertain`. It does not store a token, a signing key, or a `rowversion`. There is no partial reversal.

The descriptor is marked `uncertain` before the POST leaves the browser. A lost response, an abort, a timeout, an unreadable HTTP 200, or leaving the page keeps that descriptor. Closing the dialog, navigation, and a session change do not remove it. Retry is a separate operator action. It sends the stored key and the stored body, including when the reversal-context read fails or the history row is not on the current page. HTTP 200 is accepted only when `status` is `reversed`, `replayed` is a boolean, the allocation id matches, the amount and currency match the descriptor, the reason matches the stored reason, and `reversedAt` is a timestamp. A verified new reversal and a verified replay remove the matching descriptor and invalidate the current session's allocation history, including filtered history, that allocation's audit, invoices, bank transactions, matches, and the allocation-context query for the stored pair. An unknown outcome does not. A response from an older session epoch does not clear the current descriptor or refresh the new organization's cache. `already_reversed`, an idempotency payload conflict, and an integrity conflict are not treated as success. `concurrency_conflict` can be sent again with the same key, and it is not treated as success. `actor.required` and HTTP 401 keep the descriptor. **Remove local tracking** requires a second confirmation. It does not undo a server commit, and it discards the key needed to replay that request. A descriptor for another organization stays hidden and is not sent or deleted. Malformed storage is not overwritten. One explicit-amount attempt and one reversal attempt can both be stored. Reconciliation keeps its own recovery dialog and links to Allocation history when a reversal attempt belongs to the current organization. Production browser authentication remains deferred.

The manual allocation dialog uses `src/frontend/src/features/reconciliation/allocation-attempt.ts`. That module remains the authority for one explicit-amount attempt. The dialog does not generate an idempotency key or serialize the POST body. The authoritative recovery call remains `POST /api/allocations` with the original `Idempotency-Key` and the original JSON body. There is no operation-lookup endpoint. A stored attempt can be reviewed from the Reconciliation page after reload, after the suggestion disappears, and when the context read fails or the current remaining amounts are zero. Those later reads do not block sending the original request again. The screen does not send it automatically.

The module validates the amount with `parseAllocationAmount`, builds the body with `serializeAllocationBody`, and writes the descriptor to `sessionStorage` before any POST. The amount is a canonical decimal string. `100.2500` is stored and sent as the JSON number `100.25`. `0.0001` stays `0.0001`. The amount is not a JavaScript number. If `sessionStorage` throws, the full write cannot be read back, or an unresolved descriptor is already present, the module does not send the POST and does not replace the stored attempt.

A restored attempt is not sent on mount, reload, token entry, epoch change, or tenant change. Retry is manual. The same key and the same body are sent again. A lost response, an aborted request, an unreadable 200 or 201, an HTTP 500, and an idempotency payload conflict leave the attempt in place. Aborting the browser request does not prove that SQL Server rolled back the allocation. Only a verified HTTP 201 with `replayed: false`, or HTTP 200 with `replayed: true`, for the same pair, amount, and currency removes the unresolved descriptor. The returned epoch is the session for which a later screen may invalidate its caches. An unverified response does not.

The descriptor is bound to the `org_id` claim of the session that created it. The claim is only a local guard. The server still authorizes the POST. A token for another organization cannot send the attempt, and the screen does not reveal the stored pair or amount for that mismatch. It also does not offer retry or abandonment until the same organization is signed in again. A later token for the same organization, with a usable `sub`, can send it manually. Logout does not send it. Closing the dialog, leaving `/reconciliation`, and reloading the browser leave the descriptor in place. A verified result invalidates the invoice, bank transaction, match, and allocation-context queries for that result's session epoch even if the page has already unmounted. An unknown outcome does not invalidate them. If local cleanup fails and the descriptor is gone, the screen does not invent a replacement key.

`sessionStorage` survives a reload and a return to `/reconciliation` in the same tab. It can disappear when the tab or browser session ends, including when session restore does not bring it back. It does not coordinate other tabs. A duplicated tab may start with a copy and can then diverge. Two tabs can therefore create two keys and two allocations. Clearing storage, a corrupted record, or explicit abandonment also ends local tracking without cancelling a commit that already happened. Abandonment is a local deletion, and it is rejected while that attempt's POST is still in flight. A verified response removes the descriptor only when the stored key and request body still belong to that request. A failure to remove it is not an unreadable server response, and the same attempt can be retried manually. The next attempt gets a new key and can record a second allocation if the abandoned POST had committed. The module does not describe abandonment as cancellation or rollback. It does not use `localStorage`.

The same call can be made directly:

```powershell
$token = (scripts\dev-token.ps1 -Subject operator-1 -OrganizationId $organizationId).Trim()
$csv = "ExternalId,InvoiceNumber,Amount,Currency`r`ndemo-100,INV-100,1250.50,PLN`r`n"
$csvPath = Join-Path $env:TEMP 'finopsdesk-invoices.csv'
[System.IO.File]::WriteAllText($csvPath, $csv, [System.Text.UTF8Encoding]::new($false))
curl.exe -sS -X POST "https://localhost:7257/api/imports/invoices" `
  -H "Authorization: Bearer $token" `
  -F "sourceSystem=DEMO" `
  -F "file=@$csvPath;type=text/csv"
```

Use the public certificate trust from `dev-frontend.ps1` for `curl.exe` as well, or run curl from a session that already trusts the development certificate through Windows.

Open `http://localhost:5173/invoices`. Amounts are read from the JSON number tokens, including a zero allocated amount.

## Browser end-to-end

`npm run dev` still proxies `/api` and `/health` to `https://localhost:7257`. A local end-to-end run can set `FINOPSDESK_E2E_API_ORIGIN` for that one Vite process. The variable is not a `VITE_` variable, so it is not compiled into the browser bundle. Leave it unset for ordinary development. The journey, the disposable database, and the run commands are documented in `docs/e2e-testing.md`. Ordinary `npm test` stays on Vitest and jsdom.

## Checks

From `src/frontend`:

```powershell
npm ci
npm run typecheck
npm run lint
npm test
npm run build
```

The production build must not contain the development token screen. Frontend CI is `.github/workflows/frontend-ci.yml`.
