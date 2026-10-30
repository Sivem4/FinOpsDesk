# Frontend development

The operations workspace is a Vite application in `src/frontend`. It shows the invoice register from `GET /api/invoices`, CSV import at `/imports`, and the reconciliation queue at `/reconciliation`. It does not issue tokens. The import screen sends the selected file to the existing import endpoints and does not change amounts in the browser. The reconciliation screen reads `GET /api/matches` and `GET /api/matches/smart`. An exact match can be confirmed with `POST /api/matches/confirm`. Smart suggestions are not confirmed, and the screen does not call `POST /api/allocations`.

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

A confirmed invoice import or replay refreshes the invoice register and the reconciliation queries for that session. Open **Invoices** to see rows from `GET /api/invoices`. A confirmed bank-transaction import or replay refreshes the reconciliation queries for that session. It does not refresh the invoice register, and there is no bank-transaction list endpoint. An import whose outcome is unknown does not refresh either cache.

## Reconciliation

Open `http://localhost:5173/reconciliation`. **Exact** calls `GET /api/matches` and shows `exact-1` pairs, ambiguous groups, unmatched rows, and ineligible rows in the returned order. **Smart** calls `GET /api/matches/smart` with no query string and shows `smart-1` suggestions. Scores are points. The screen does not turn them into percentages. Only the selected view requests its read endpoint. A book larger than 5,000 open rows on either side returns HTTP 409 and is not matched. Inconsistent stored money returns the matching HTTP 500 problem and is not matched. Exact and Smart are separate reads.

**Confirm** is available only on an exact-match row. Ambiguous groups, unmatched rows, ineligible rows, and smart suggestions have no confirmation action. The dialog shows the reference, the amount from that read, and both identifiers. The displayed pair is not reserved. The books can change before the server accepts the request, so that amount can be stale. The request body is only `invoiceId` and `bankTransactionId`. It does not send an amount, a currency, an actor, or an organization id. `Idempotency-Key` is one new UUID in `D` format for a new confirmation. The server calculates the full current open amount and checks that pair. It does not check whether the pair is still the only exact match in the book.

HTTP 201 with `replayed` false means the allocation was recorded. HTTP 200 with `replayed` true means the same key and the same pair were already recorded. The screen shows the allocation id and the amount from that response. A verified 201 or 200 refreshes the invoice register and both reconciliation reads for the current session. An unreadable success or a lost response keeps the same key and does not refresh the caches as a confirmed success. The operator can send that same request again. The screen does not create a new key after an error. The attempt stays in memory only while the Reconciliation page stays mounted. Reloading the browser or leaving `/reconciliation` discards it. The application does not keep an unresolved attempt across that navigation. Opening the page again can start a new confirmation with a new idempotency key, and that new operation cannot tell whether the earlier POST was saved. Refreshing the list is a GET and does not prove the result of a particular POST.

Manual allocation through `POST /api/allocations` is not part of this screen. `GET /api/allocations/context` can read the current context of one invoice and one bank transaction, and this screen does not call it. That read does not reserve either balance. Exact-match confirmation remains an in-memory attempt and is unchanged.

The future manual allocation dialog will use `src/frontend/src/features/reconciliation/allocation-attempt.ts`. That module is the recovery foundation for one explicit-amount attempt. It does not render a dialog. The authoritative recovery call remains `POST /api/allocations` with the original `Idempotency-Key` and the original JSON body. There is no operation-lookup endpoint.

The module validates the amount with `parseAllocationAmount`, builds the body with `serializeAllocationBody`, and writes the descriptor to `sessionStorage` before any POST. The amount is a canonical decimal string. `100.2500` is stored and sent as the JSON number `100.25`. `0.0001` stays `0.0001`. The amount is not a JavaScript number. If `sessionStorage` throws, the full write cannot be read back, or an unresolved descriptor is already present, the module does not send the POST and does not replace the stored attempt.

A restored attempt is not sent on mount, reload, token entry, epoch change, or tenant change. Retry is manual. The same key and the same body are sent again. A lost response, an aborted request, an unreadable 200 or 201, an HTTP 500, and an idempotency payload conflict leave the attempt in place. Aborting the browser request does not prove that SQL Server rolled back the allocation. Only a verified HTTP 201 with `replayed: false`, or HTTP 200 with `replayed: true`, for the same pair, amount, and currency removes the unresolved descriptor. The returned epoch is the session for which a later screen may invalidate its caches. An unverified response does not.

The descriptor is bound to the `org_id` claim of the session that created it. The claim is only a local guard. The server still authorizes the POST. A token for another organization cannot send the attempt, and the module does not reveal the stored pair or amount for that mismatch. A later token for the same organization, with a usable `sub`, can send it manually. Logout does not send it.

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
