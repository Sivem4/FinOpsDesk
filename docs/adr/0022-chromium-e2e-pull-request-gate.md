# ADR 0022: Chromium end-to-end pull-request gate

- Status: Accepted
- Date: 2026-11-04

## Context

`tests/FinOpsDesk.E2ETests` already runs the browser security flows: OpenID Connect authorization code with PKCE, the cookie session, organization selection, cross-organization access, role and membership revocation, the ProductionCookie financial allowlist, exact confirmation, explicit allocation, reversal, idempotency, a lost response after a committed SQL write, and identity-bound recovery. The project has 37 tests. It is outside `FinOpsDesk.slnx`. Backend CI covers architecture, unit, and SQL integration tests. Frontend CI covers typecheck, lint, Vitest, and the production build. Neither job started Chromium.

A filtered security subset would leave the financial browser journeys unguarded. Retry-until-green would hide a real TLS, CSRF, session, or SQL failure. Windows-only execution would leave the pull-request gate dependent on a hosted Windows runner and on the CurrentUser\Root certificate store.

## Decision

### Full suite, one job

`.github/workflows/e2e-ci.yml` runs the complete E2E project once, in Release, on `ubuntu-24.04`. The job name is `Chromium browser security`. It runs for a pull request targeting `main` and for a push to `main`. Its concurrency group is `e2e-ci-${{ github.workflow }}-${{ github.ref }}`, so a newer run cancels only an older run of this workflow. It does not cancel Backend CI or Frontend CI.

The job timeout is 30 minutes. A failing test, a failing Chromium install, a failing certificate command, a failing SQL connection, or a timeout fails the check. There is no `continue-on-error`, no skipped Chromium fallback, and no automatic retry.

Microsoft.Playwright 1.64.1 is the package already referenced by the E2E project. Playwright 1.64.1 treats Ubuntu 24.04 x64 as an officially supported host and ships the Chromium system dependencies for that platform. The job installs Chromium with the package's `playwright.ps1` from the Release output, including `--with-deps`. The browser revision comes from that package. The workflow does not pin a separate browser build and does not install a distro Chrome package in its place. The browser stays headless. Screenshots, video, and traces stay off.

The job does not run `dotnet test FinOpsDesk.slnx`. It builds the E2E project in Release and then runs `dotnet test` with `--no-build`, because the harness starts the API with `dotnet run --configuration Release --no-build`.

### SQL Server

The job uses one SQL Server 2022 service container and the existing `FINOPSDESK_TEST_SQLSERVER` contract: a test login that can create and drop databases, no catalog name, and no LocalDB. Each test still creates and drops its own `FinOpsDesk_E2E_<guid>` database. The API process is stopped before that database is dropped. The workflow does not print the connection string. The test-only SQL credential is the same value already used by Backend CI. SQL Server's container certificate remains under that existing contract. It is not the application HTTPS certificate.

### Application HTTPS

Cookie tests listen on `https://localhost:<port>` because antiforgery will not issue a token for a non-SSL request. `dotnet dev-certs https` ensures the ASP.NET Core localhost development certificate in the current-user personal store. That store keeps the private key. Kestrel in the `Development` host environment loads that certificate. The E2E exporter does not call `dotnet dev-certs https --export-path`, because a PEM export also writes a sibling private-key file.

The exporter writes only the public certificate, `X509ContentType.Cert`, to a temporary file outside the repository. Vite receives that path as `NODE_EXTRA_CA_CERTS`. On Linux the file mode is owner read/write. The test deletes the file. The browser talks to Vite over HTTP and does not receive the private key. `secure` stays `true`. `NODE_TLS_REJECT_UNAUTHORIZED` is removed from the Vite process. Hostname verification stays enabled. The development certificate's subject alternative name includes `localhost`. Linux does not read the Windows Root store and does not install a system trust anchor.

The DevelopmentJwt journeys stay on loopback HTTP. Their API process does not use this certificate.

### Vite process

Windows and Ubuntu both start `node` with `src/frontend/node_modules/vite/bin/vite.js`. The retained process id is that Node process. Disposal stops that process tree only. It does not search for another Node or Chrome process. An early exit fails readiness. A start exception disposes the process object.

### Branch protection is separate

A workflow file does not make a check required. After this job has gone green on GitHub-hosted Ubuntu, the main branch ruleset can require these check names:

- `Build, test and verify formatting`
- `Typecheck, lint, test, and build`
- `Chromium browser security`

This repository change does not modify GitHub settings.

### Out of scope

No change to financial refresh, Vitest worker limits, `FinancialCommitGuard`, operational security events, recovery v1 or v2, authorization policies, OpenAPI, production OIDC settings, or the deployed financial activation flags. Both flags stay unset. The test host may still enable them for an individual scenario. The six excluded financial POST operations stay forbidden. There is no new browser framework and no production hosting.

## Consequences

- A pull request runs the same 37 Chromium tests that developers run locally, on real SQL Server and through the real cookie and JWT browser flows.
- The job is slower and larger than the backend and frontend jobs. That cost is accepted instead of a reduced security subset or a green retry.
- Ubuntu 24.04 is the verified Playwright host platform for package 1.64.1. A green GitHub-hosted run is still required before the check is made mandatory. This ADR does not record that run as already green.
- Local Windows execution no longer depends on the CurrentUser\Root trust copy. Ordinary `scripts/dev-frontend.ps1` still exports the trusted public certificate for the launch-profile API.
- The private key remains in the .NET development-certificate store. It is not a source file, a workflow artifact, or a Vite environment value.
