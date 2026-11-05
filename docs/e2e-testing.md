# End-to-end testing

Phase 6I.1 runs one Chromium journey against the real Vite development application, a real ASP.NET Core process, and a disposable SQL Server database. It does not mock HTTP and it does not write to the Development database `FinOpsDesk`.

The test project is `tests/FinOpsDesk.E2ETests`. It is not included in `FinOpsDesk.slnx`. `dotnet test FinOpsDesk.slnx` does not build it, install a browser, or run it. Backend CI and frontend CI do not run it. `.github/workflows/e2e-ci.yml` runs the complete project on a pull request and on a push to `main`. See ADR 0022.

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

Reversal, a lost HTTP response, explicit-amount recovery, and a second organization are covered by the Phase 6I.2 browser tests in the same project. Smart-1 is not part of these DevelopmentJwt journeys. Production cookie sign-in is covered by the later Chromium cookie journeys. There is no production identity-provider deployment in this repository.

## Prerequisites

- .NET 10 SDK
- Node.js and npm, with `src/frontend/node_modules` already installed (`npm ci` from `src/frontend`)
- SQL Server LocalDB on Windows, or `FINOPSDESK_TEST_SQLSERVER` pointing at a test instance
- Chromium for the pinned `Microsoft.Playwright` package
- No cloud services

`FINOPSDESK_TEST_SQLSERVER` selects the server only. Omit the database name, or set the catalog to `master`. A catalog of `FinOpsDesk`, `FinOpsDesk_IT...`, or any other name fails the run before a connection is used for migrations. The runner creates `FinOpsDesk_E2E_<guid>` itself.

The DevelopmentJwt journey does not use the ASP.NET HTTPS development certificate. That API process listens on loopback HTTP because no HTTPS address is configured, so the existing HTTPS redirection middleware does not redirect. The browser talks only to Vite. Vite proxies `/api` and `/health` to that loopback origin. `secure` stays `true`. JWT validation and `TenantMember` stay enabled. The signing key is created in memory for the API process and is not a `VITE_` variable, a source file, or a command-line argument. Cookie tests are the exception: their API listens on `https://localhost` and the public development certificate is given only to the Vite process.

Do not point `FINOPSDESK_TEST_SQLSERVER` at a production or shared application database.

## Run

From the repository root:

```powershell
dotnet restore tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj
dotnet build tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj
powershell -File tests/FinOpsDesk.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --no-restore
```

A Release build uses `tests/FinOpsDesk.E2ETests/bin/Release/net10.0/playwright.ps1` and `dotnet test -c Release`. The API harness uses `dotnet run --no-build` in that same configuration, so a Release test run must follow a Release build. On Ubuntu the same `playwright.ps1` is started with `pwsh`. The package does not ship a separate browser-revision pin.

The Playwright browsers are installed outside the repository, under the user profile. Do not commit them.

## Pull request gate

`.github/workflows/e2e-ci.yml` has one job, `Chromium browser security`. The runner is `ubuntu-24.04` because Microsoft.Playwright 1.64.1 treats that release as an officially supported Chromium host. The job timeout is 30 minutes. Permissions are `contents: read`. Concurrency cancels a superseded run of this workflow only.

The job checks out the repository, installs the .NET 10 SDK and Node 24, runs `npm ci` in `src/frontend`, runs `dotnet dev-certs https` without exporting a private key, builds the E2E project in Release, runs `playwright.ps1 install chromium --with-deps`, and runs the complete project once with `dotnet test --configuration Release --no-build`. It does not run `FinOpsDesk.slnx`. It does not filter by category and it does not retry a failed test.

SQL Server 2022 is a service container. `FINOPSDESK_TEST_SQLSERVER` has no catalog. Each test creates `FinOpsDesk_E2E_<guid>` and drops it only after its API process is confirmed stopped. The workflow does not print the connection string. A database connection failure fails the test.

`dotnet dev-certs https` puts the localhost development certificate, including its private key, in the current-user personal store. Kestrel loads that certificate and serves `https://localhost`. The test writes a temporary public PEM outside the repository and passes it to Vite as `NODE_EXTRA_CA_CERTS`; the proxy keeps `secure: true`. The .NET cookie health probe is a third, separate trust path: its `HttpClient` builds a chain with `X509ChainTrustMode.CustomRootTrust` and that same public certificate as the only anchor. The presented certificate must be that certificate, the request host must be `localhost`, and the certificate must still be within its validity period. A substituted certificate, another hostname, or an expired certificate is rejected. A certificate-validation failure stops startup immediately instead of retrying for the readiness window. Connection refusal while Kestrel is still starting is still retried. The probe does not install a system trust anchor. The CLI PEM export is not used, because it also writes a sibling private-key file. The private key is not committed, logged, or given to Chromium or Node. `NODE_TLS_REJECT_UNAUTHORIZED` is not set.

Windows and Ubuntu both start Vite as `node` plus `src/frontend/node_modules/vite/bin/vite.js`. Disposal stops that process tree and does not hunt for an unrelated Node or Chrome process.

The workflow does not change GitHub branch protection. Executing CI and enforcing a ruleset are separate. GitHub does not enforce rulesets on this private repository under its current plan, so `Chromium browser security` is not an enforced required check. The owner manually verifies successful backend, frontend, and Chromium checks before merging. That limit is repository governance, not a runtime authentication defect. See ADR 0022.

## Isolation and cleanup

Each run applies the Reconciliation migrations and then the Access migrations on one database. Their histories stay in `reconciliation.__EFMigrationsHistory` and `access.__EFMigrationsHistory`. Each run generates one database name matching `FinOpsDesk_E2E_` plus 32 lowercase hex characters. The runner parses the effective connection string and refuses to migrate unless that name is the catalog. The journey limit is 150 seconds and is enforced by the test host. xUnit 2.9.3 `Fact(Timeout)` is not used, because that runner fails the test and leaves the test method running. When the host limit is reached, the host closes the browser and waits until the journey task has finished. If that task is still running, the owned database stays in place and the failure reports cleanup uncertainty. Cleanup drops a database only after the owned API process is confirmed stopped, and only the database this run created. A failed confirmation leaves that process handle in place so a later dispose can try again. The handle is released only after the stop is confirmed. An unconfirmed API stop leaves the database untouched. A cookie startup failure carries that API process on the exception, so the caller applies the same rule instead of treating a missing return value as permission to drop the database. A Vite or browser cleanup failure remains a test failure even when that drop succeeded. A startup failure after the child process exists is recorded as confirmed-stopped or unconfirmed. The absence of a returned API or Vite object is not treated as proof that no process started.

CSV files are written under the user temp directory and deleted with the test host. They are not stored in the repository.

## JWT

The financial API process environment is `E2E`, and `Authentication:Composition` is `DevelopmentJwt`. The process receives an ephemeral HMAC key through its environment. The test builds a short-lived JWT with issuer `https://finopsdesk.local`, audience `finopsdesk-e2e`, a non-empty `org_id`, and subject `e2e-operator`. The key is not the integration-test signing key. The test does not print the key or the token. Screenshots, video, and Playwright traces are off. Do not enable `DEBUG=pw:api` for this run, because Playwright would then log the token typed into the form. The financial tests do not start the production cookie composition, do not require OpenID Connect settings, and do not send a CSRF cookie. Vite for those tests stays on `npm run dev` and `127.0.0.1`.

## Failure output

A failure names the component that stopped: browser, Vite, API, database, or fixtures. API and Vite logs are trimmed and redacted. The report can include browser console errors, page errors, and HTTP 500 responses. It must not include authorization headers, signing keys, JWTs, or connection-string passwords. The runner does not write the process environment.

## Troubleshooting

- `E2E could not open the SQL Server master connection`: start LocalDB, or set `FINOPSDESK_TEST_SQLSERVER` without a database name.
- `E2E refused supplied catalog`: the connection string names a database. Remove `Database` or `Initial Catalog`, or set it to `master`.
- `Vite is not installed`: from `src/frontend`, run `npm ci`.
- `Chromium is not available`: the test fails. It is not skipped. Run `playwright.ps1 install chromium` from the test output directory after a build. On Ubuntu, start that script with `pwsh` and add `--with-deps` for the system libraries.
- `dotnet dev-certs https failed` or a missing localhost certificate: cookie E2E fails. It does not fall back to HTTP or to an unverified TLS proxy.
- `API health redirected`: the isolated process was given an HTTPS address. The E2E host sets `ASPNETCORE_URLS` to loopback HTTP and does not use a launch profile.
- Vite exits immediately: port collision. The host passes `--strictPort` on a free loopback port and does not use port 7257.
- The ordinary Development API on port 7257 is a different process. Leave `FINOPSDESK_E2E_API_ORIGIN` unset when you run `scripts/dev-frontend.ps1`.

## Cookie sign-in smoke

`CookieOidcBrowserTests` is a separate browser test in the same project and the same non-parallel collection. It is not a financial journey. It starts `OidcProtocolStub` with redirect URI `http://localhost:<vite port>/signin-oidc`, starts the API as `ProductionCookie` on `https://localhost:<api port>`, and starts Vite in cookie mode on `http://localhost:<vite port>`. The API listens on HTTPS because antiforgery will not issue a token for a non-SSL request. The host environment name is `Development` only so Kestrel can load the ASP.NET Core HTTPS development certificate from the current-user personal store; `Authentication:Composition` remains `ProductionCookie`. The private key stays in that store. Vite receives a temporary public certificate through `NODE_EXTRA_CA_CERTS` and keeps `secure: true`. Linux does not require the Windows Root store. `scripts/dev-frontend.ps1` is a separate local path and still exports the public certificate from CurrentUser\Root for the ordinary launch profile. The browser origin stays `http://localhost`. Chromium then signs in through a real form POST, returns through the proxied `/signin-oidc` callback, sees the restricted screen, and signs out locally. The test checks that the `__Host-finopsdesk-session` cookie is stored for `localhost`, that financial controls are absent, that no `Authorization: Bearer` header is sent, and that `/api/invoices` is forbidden before sign-out and unauthorized after it. Screenshots, video, and Playwright traces stay off. The test client secret is not written into the assertion text.

`CookieOrganizationBrowserTests` uses the same host. Before sign-in it inserts two active Access organizations and memberships for the stub subject. Chromium checks that neither organization is selected automatically, that an explicit POST carries a CSRF header and no bearer token, that the next session read is what the screen calls active, that a second tab follows that server result, and that revoking a membership removes it after reload. One test holds the organization response with `Route.FetchAsync` until a second tab signs out, then fulfills that response. The late cookie is rejected because the same server session was revoked. The test does not sleep and does not add a production session endpoint. Financial routes stay forbidden.

Windows and Ubuntu start `node` with `src/frontend/node_modules/vite/bin/vite.js` and keep that process handle. Disposal stops that process tree only. It does not search for `node`, `vite.js`, the frontend path, or the reserved port, and it does not stop an unrelated Node, Chrome, or editor process. If that owned process cannot be confirmed stopped, cleanup fails. The database is still dropped only after the owned API process is confirmed stopped.

Run it after the E2E build and Chromium install:

```powershell
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --configuration Release --filter "FullyQualifiedName~CookieOidcBrowserTests"
```

The financial filter remains the way to run a money journey without this smoke. Building the project does not run the browser. The pull-request job runs the whole project.

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

Phase 6I.1 and Phase 6I.2 are implemented local browser coverage for the completed Phase 6 workspace. They run against Chromium, real Vite, real ASP.NET Core, and a disposable `FinOpsDesk_E2E_<guid>` SQL Server database. SQL assertions prove the committed financial state. A lost-response test accepts the case only after that commit and then checks that the manual retry repeats the same idempotency key and the same raw body. These tests stay outside `FinOpsDesk.slnx`. The complete project, including these tests, is the Chromium pull-request job in `.github/workflows/e2e-ci.yml`. That job is not an enforced required check: rulesets are not enforced for this private repository under its current GitHub plan. The owner verifies the check manually before merging. Production cookie sign-in is part of the Chromium cookie journeys. There is no production identity-provider deployment in this repository, and local logout does not end the provider session. Phase 7D.3b adds cookie financial-read journeys against the OIDC stub when that test host enables `Authentication:FinancialReadAuthorization`. `Viewer_reads_a_seeded_review_case_without_posting` uses that same host. The fixture inserts one competing-match review case whose invoice outstanding is zero and whose bank transaction still has `10.0001` PLN unapplied. The browser signs in, selects the organization, and reads the list and detail. The read flag is enabled only in that test process. The write flag stays unset, and the journey fails if Chromium sends a POST to a review-case route. The existing DevelopmentJwt journeys stay in place. Deployed configuration leaves both financial activation keys unset. A cookie browser test host may enable the read key, or both keys, for that process only. That test configuration is not deployed configuration. Phase 7D.4a SQL Server tests enable both keys only inside that test host. When the write key is unset, cookie financial POSTs return 403 without a SQL mutation. When a test host enables both keys, it may exercise exact confirmation, explicit allocation, and reversal. The other six cookie POSTs stay denied. These tests do not change matching, allocation, or reversal rules.

Run one scenario from the repository root after the build and Chromium install above:

```powershell
dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj --filter "FullyQualifiedName~Committed_reversal_survives_a_lost_response_and_replays"
```

Replace the filter with `Committed_explicit_amount_survives_a_lost_response_and_replays`, `Other_organization_cannot_retry_a_stored_reversal`, `Pending_reversal_and_explicit_amount_attempts_stay_independent`, or `Viewer_reads_a_seeded_review_case_without_posting` for the other named tests. The full project run remains `dotnet test tests/FinOpsDesk.E2ETests/FinOpsDesk.E2ETests.csproj`. Browser scenarios in the collection run one at a time. Each still creates and drops only its own `FinOpsDesk_E2E_<guid>` database, and only after the owned API process is confirmed stopped.
