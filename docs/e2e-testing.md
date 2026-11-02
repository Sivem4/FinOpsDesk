# End-to-end testing

Phase 6I.1 runs one Chromium journey against the real Vite development application, a real ASP.NET Core process, and a disposable SQL Server database. It does not mock HTTP and it does not write to the Development database `FinOpsDesk`.

The test project is `tests/FinOpsDesk.E2ETests`. It is not included in `FinOpsDesk.slnx`. `dotnet test FinOpsDesk.slnx` does not build it, install a browser, or run it. Backend CI and frontend CI are unchanged.

## What the journey covers

One organization, one operator, and one run-scoped fixture:

1. Sign in through the development token screen. The token stays in browser memory. The test types it into **Development access token**.
2. Import an invoice CSV with one valid row and one row whose amount is rejected. The batch status is `CompletedWithErrors`.
3. The valid invoice is visible in the invoice register. The rejected invoice number is not.
4. Import the matching bank-transaction CSV. The batch status is `Completed`.
5. Import the same invoice bytes and the same source system again. The UI reports the earlier batch and SQL Server still has one invoice batch.
6. Reconciliation shows one exact-1 candidate. The test confirms it in the dialog.
7. Allocation history shows that confirmed allocation and its confirmation audit, including the test actor.
8. SQL Server checks the decimal amounts, the allocated balances, the single allocation, the single confirmation audit, and the organization id.

Reversal, a lost HTTP response, explicit-amount recovery, and a second organization are covered by the Phase 6I.2 browser tests in the same project. Smart-1 and production browser sign-in are not part of these journeys.

## Prerequisites

- .NET 10 SDK
- Node.js and npm, with `src/frontend/node_modules` already installed (`npm ci` from `src/frontend`)
- SQL Server LocalDB on Windows, or `FINOPSDESK_TEST_SQLSERVER` pointing at a test instance
- Chromium for the pinned `Microsoft.Playwright` package
- No cloud services

`FINOPSDESK_TEST_SQLSERVER` selects the server only. Omit the database name, or set the catalog to `master`. A catalog of `FinOpsDesk`, `FinOpsDesk_IT...`, or any other name fails the run before a connection is used for migrations. The runner creates `FinOpsDesk_E2E_<guid>` itself.

The journey does not use the ASP.NET HTTPS development certificate. The API process listens on loopback HTTP because no HTTPS address is configured, so the existing HTTPS redirection middleware does not redirect. The browser talks only to Vite. Vite proxies `/api` and `/health` to that loopback origin. `secure` stays `true`. JWT validation and `TenantMember` stay enabled. The signing key is created in memory for the API process and is not a `VITE_` variable, a source file, or a command-line argument.

Do not point `FINOPSDESK_TEST_SQLSERVER` at a production or shared application database.

## Run

From the repository root:

```powershell
dotnet restore tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj
dotnet build tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj
powershell -File tests/FinOpsDesk.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --no-restore
```

A Release build uses `tests/FinOpsDesk.E2ETests/bin/Release/net10.0/playwright.ps1` and `dotnet test -c Release`.

The Playwright browsers are installed outside the repository, under the user profile. Do not commit them.

## Isolation and cleanup

Each run applies the Reconciliation migrations and then the Access migrations on one database. Their histories stay in `reconciliation.__EFMigrationsHistory` and `access.__EFMigrationsHistory`. Each run generates one database name matching `FinOpsDesk_E2E_` plus 32 lowercase hex characters. The runner parses the effective connection string and refuses to migrate unless that name is the catalog. The journey limit is 150 seconds and is enforced by the test host. xUnit 2.9.3 `Fact(Timeout)` is not used, because that runner fails the test and leaves the test method running. When the host limit is reached, the host closes the browser and waits until the journey task has finished. If that task is still running, the owned database stays in place and the failure reports cleanup uncertainty. Cleanup drops a database only after the owned API process is confirmed stopped, and only the database this run created. A Vite or browser cleanup failure remains a test failure even when that drop succeeded. An unconfirmed API stop leaves the database untouched. A startup failure after the child process exists is recorded as confirmed-stopped or unconfirmed. The absence of a returned API or Vite object is not treated as proof that no process started.

CSV files are written under the user temp directory and deleted with the test host. They are not stored in the repository.

## JWT

The financial API process environment is `E2E`, and `Authentication:Composition` is `DevelopmentJwt`. The process receives an ephemeral HMAC key through its environment. The test builds a short-lived JWT with issuer `https://finopsdesk.local`, audience `finopsdesk-e2e`, a non-empty `org_id`, and subject `e2e-operator`. The key is not the integration-test signing key. The test does not print the key or the token. Screenshots, video, and Playwright traces are off. Do not enable `DEBUG=pw:api` for this run, because Playwright would then log the token typed into the form. The financial tests do not start the production cookie composition, do not require OpenID Connect settings, and do not send a CSRF cookie. Vite for those tests stays on `npm run dev` and `127.0.0.1`.

## Failure output

A failure names the component that stopped: browser, Vite, API, database, or fixtures. API and Vite logs are trimmed and redacted. The report can include browser console errors, page errors, and HTTP 500 responses. It must not include authorization headers, signing keys, JWTs, or connection-string passwords. The runner does not write the process environment.

## Troubleshooting

- `E2E could not open the SQL Server master connection`: start LocalDB, or set `FINOPSDESK_TEST_SQLSERVER` without a database name.
- `E2E refused supplied catalog`: the connection string names a database. Remove `Database` or `Initial Catalog`, or set it to `master`.
- `Vite is not installed`: from `src/frontend`, run `npm ci`.
- `Chromium is not available`: run the `playwright.ps1 install chromium` command above after a build.
- `API health redirected`: the isolated process was given an HTTPS address. The E2E host sets `ASPNETCORE_URLS` to loopback HTTP and does not use a launch profile.
- Vite exits immediately: port collision. The host passes `--strictPort` on a free loopback port and does not use port 7257.
- The ordinary Development API on port 7257 is a different process. Leave `FINOPSDESK_E2E_API_ORIGIN` unset when you run `scripts/dev-frontend.ps1`.

## Cookie sign-in smoke

`CookieOidcBrowserTests` is a separate browser test in the same project and the same non-parallel collection. It is not a financial journey. It starts `OidcProtocolStub` with redirect URI `http://localhost:<vite port>/signin-oidc`, starts the API as `ProductionCookie` on `https://localhost:<api port>`, and starts Vite in cookie mode on `http://localhost:<vite port>`. The API listens on HTTPS because antiforgery will not issue a token for a non-SSL request. The host environment name is `Development` only so Kestrel can use the trusted development certificate; `Authentication:Composition` remains `ProductionCookie`. Vite receives the public certificate through `NODE_EXTRA_CA_CERTS` and keeps `secure: true`. The browser origin stays `http://localhost`. Chromium then signs in through a real form POST, returns through the proxied `/signin-oidc` callback, sees the restricted screen, and signs out locally. The test checks that the `__Host-finopsdesk-session` cookie is stored for `localhost`, that financial controls are absent, that no `Authorization: Bearer` header is sent, and that `/api/invoices` is forbidden before sign-out and unauthorized after it. Screenshots, video, and Playwright traces stay off. The test client secret is not written into the assertion text.

`CookieOrganizationBrowserTests` uses the same host. Before sign-in it inserts two active Access organizations and memberships for the stub subject. Chromium checks that neither organization is selected automatically, that an explicit POST carries a CSRF header and no bearer token, that the next session read is what the screen calls active, that a second tab follows that server result, and that revoking a membership removes it after reload. One test holds the organization response with `Route.FetchAsync` until a second tab signs out, then fulfills that response. The late cookie is rejected because the same server session was revoked. The test does not sleep and does not add a production session endpoint. Financial routes stay forbidden.

On Windows the runner starts `node` with `src/frontend/node_modules/vite/bin/vite.js` and keeps that process handle. Disposal stops that process tree only. It does not search for `node.exe`, `vite.js`, the frontend path, or the reserved port, and it does not stop an unrelated Node, Chrome, or editor process. If that owned process cannot be confirmed stopped, cleanup fails. The database is still dropped only after the owned API process is confirmed stopped. Non-Windows runs continue to start Vite through `npm`.

Run it after the E2E build and Chromium install:

```powershell
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --configuration Release --filter "FullyQualifiedName~CookieOidcBrowserTests"
```

The financial filter remains the way to run a money journey without this smoke. A passing E2E build is not this browser run.

## Phase 6I.2

The same project adds browser tests for reversal and recovery. They use the Phase 6I.1 host, a new xUnit collection named `FinOpsDesk.BrowserE2E` with parallelization disabled, and one disposable database per test. `E2ELifecycleTests` stays outside that collection. `CoreFinancialJourneyTests` is in the collection and keeps its original assertions.

The added scenarios are:

1. A confirmed Exact-1 allocation is reversed in the browser. History shows Reversed, the original amount, Confirmation and Reversal audit rows, and no further Reverse action. SQL Server checks the restored balances, the single reversal audit, the reason, and the actor.
2. `POST /api/allocations/{allocationId}/reverse` is sent to the real API with Playwright `Route.FetchAsync` (`MaxRetries = 0`, `MaxRedirects = 0`). The test reads HTTP 200, `replayed: false`, and the committed SQL reversal before `Route.AbortAsync` drops that response. The browser shows the unknown-outcome copy, keeps `finopsdesk.reversal-attempt`, and does not send a second POST. Reload shows the development token screen again. After the operator signs in and chooses **Send the same reversal again**, `Route.FetchAsync` plus `Route.FulfillAsync` delivers the real HTTP 200 `replayed: true` response. The key and raw body match the first POST. SQL still has one reversal.
3. Choose records allocates `10.0000` of `40.2500` PLN. The same lost-response pattern applies to `POST /api/allocations`. The first response is HTTP 201. The manual replay is HTTP 200 `replayed: true`. One partial allocation and one confirmation audit remain.
4. Organization B cannot see, retry, or delete organization A's stored reversal, and no further reversal POST is sent while B is signed in. Returning to A replays the original request.
5. `finopsdesk.explicit-amount-attempt` and `finopsdesk.reversal-attempt` can both be pending. Recovering one leaves the other byte-for-byte in place. A foreign organization's descriptors stay hidden. A malformed explicit-amount value is shown as unreadable and is not replaced.

A lost response is accepted only when SQL already shows the commit and the real HTTP response was a first success. A transport failure without that commit is not treated as the recovery case. The tests do not fabricate session storage for a successful attempt, do not fulfill a synthetic success, and do not add production middleware.

Screenshots, video, and Playwright traces stay off. Intercepted requests are compared in memory. Failure output still goes through the host redaction and does not include authorization headers or JWTs. Idempotency keys and reversal reasons are not written to the host diagnostic log.

Phase 6I.1 and Phase 6I.2 are implemented local browser coverage for the completed Phase 6 workspace. They run against Chromium, real Vite, real ASP.NET Core, and a disposable `FinOpsDesk_E2E_<guid>` SQL Server database. SQL assertions prove the committed financial state. A lost-response test accepts the case only after that commit and then checks that the manual retry repeats the same idempotency key and the same raw body. These tests stay outside `FinOpsDesk.slnx` and outside mandatory pull-request CI. Production browser sign-in remains Phase 7. Phase 7D.3a does not add a cookie financial-read journey. The existing DevelopmentJwt journeys stay in place, and the ProductionCookie screen still keeps finance unmounted while `financialAccess` is false. These tests do not change matching, allocation, or reversal rules.

Run one scenario from the repository root after the build and Chromium install above:

```powershell
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --filter "FullyQualifiedName~Committed_reversal_survives_a_lost_response_and_replays"
```

Replace the filter with `Committed_explicit_amount_survives_a_lost_response_and_replays`, `Other_organization_cannot_retry_a_stored_reversal`, or `Pending_reversal_and_explicit_amount_attempts_stay_independent` for the other new tests. The full project run remains `dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj`. Browser scenarios in the collection run one at a time. Each still creates and drops only its own `FinOpsDesk_E2E_<guid>` database, and only after the owned API process is confirmed stopped.
