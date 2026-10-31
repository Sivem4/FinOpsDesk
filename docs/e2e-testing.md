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

Reversal, a lost HTTP response, explicit-amount recovery, Smart-1, and a second organization are not part of this journey. Those remain Phase 6I.2 or later.

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

Each run generates one database name matching `FinOpsDesk_E2E_` plus 32 lowercase hex characters. The runner parses the effective connection string and refuses to migrate unless that name is the catalog. The journey limit is 150 seconds and is enforced by the test host. xUnit 2.9.3 `Fact(Timeout)` is not used, because that runner fails the test and leaves the test method running. When the host limit is reached, the host closes the browser and waits until the journey task has finished. If that task is still running, the owned database stays in place and the failure reports cleanup uncertainty. Cleanup drops a database only after the owned API process is confirmed stopped, and only the database this run created. A Vite or browser cleanup failure remains a test failure even when that drop succeeded. An unconfirmed API stop leaves the database untouched. A startup failure after the child process exists is recorded as confirmed-stopped or unconfirmed. The absence of a returned API or Vite object is not treated as proof that no process started.

CSV files are written under the user temp directory and deleted with the test host. They are not stored in the repository.

## JWT

The API process receives an ephemeral HMAC key through its environment. The test builds a short-lived JWT with issuer `https://finopsdesk.local`, audience `finopsdesk-e2e`, a non-empty `org_id`, and subject `e2e-operator`. The key is not the integration-test signing key. The test does not print the key or the token. Screenshots, video, and Playwright traces are off. Do not enable `DEBUG=pw:api` for this run, because Playwright would then log the token typed into the form.

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

## Deferred

Phase 6I.2 is expected to add reversal, replay after a response that the browser does not receive, one explicit-amount recovery, and one cross-tenant browser check. Production browser sign-in remains Phase 7. This journey does not change matching or allocation rules.
