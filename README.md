# FinOpsDesk

FinOpsDesk is B2B finance operations and reconciliation platform built with .NET and React.

The project models an accounts-receivable cash application workflow: customer invoices and incoming bank transactions are imported, deterministic matching suggestions are generated, finance users confirm or reverse allocations, exceptions are investigated, and financially relevant actions are audited. Allocation reversal is specified in [ADR 0012](docs/adr/0012-allocation-reversal.md).

## Product vision

FinOpsDesk will help finance operations teams:

- import invoices and incoming bank transactions;
- identify likely matches using explainable deterministic rules;
- handle partial payments, overpayments, underpayments, and incorrect references;
- allocate one payment to multiple invoices and multiple payments to one invoice;
- work with unmatched and partially matched items;
- open and resolve disputes;
- preserve an audit history of financial operations;
- monitor operational queues and reconciliation metrics.

## Backend persistence

Reconciliation owns `ReconciliationDbContext`, SQL schema `reconciliation`, and module migrations.

The API host reads `ConnectionStrings:FinOpsDesk` and passes it into module registration. The Development default is SQL Server LocalDB:

```text
Server=(localdb)\mssqllocaldb;Database=FinOpsDesk;Trusted_Connection=True;TrustServerCertificate=True
```

Override the connection string with configuration or environment variables. Do not commit production secrets.

### API authentication

`Authentication:Composition` selects one authentication scheme. The host does not register both, and it does not choose a scheme from the `Authorization` header.

- `DevelopmentJwt` validates JWT access tokens and does not issue them. It is allowed only when the host environment is `Development` or `E2E`. `appsettings.Development.json` selects it. Set `Issuer`, `Audience`, and `SigningKey` (at least 32 UTF-8 bytes), or set `Authority` and `Audience` instead of a signing key. Do not set `Authority` and `SigningKey` together, and do not commit the signing key. This composition does not register OpenID Connect or cookie CSRF.
- `ProductionCookie` is the default in `appsettings.json`. Cookie authentication is the default authenticate and challenge scheme. OpenID Connect is a named remote scheme for `POST /login` only. JWT bearer is not registered, and an `Authorization` bearer token does not authenticate a request. Unauthenticated API calls return 401. They do not redirect to the identity provider.

`ProductionCookie` does not grant financial access unless `Authentication:FinancialReadAuthorization` is exactly `Enabled`. A financial POST also requires `Authentication:FinancialWriteAuthorization` to be exactly `Enabled`. Both keys are unset in deployed configuration and the write key must stay unset until the cookie write UI and identity-bound recovery have been reviewed. With either key unset, financial POSTs stay forbidden. With only the read key enabled, a cookie GET still requires the active organization's read permission and every financial POST stays forbidden. With both keys enabled, ProductionCookie may execute only `POST /api/matches/confirm`, `POST /api/allocations`, and `POST /api/allocations/{allocationId}/reverse`, and each of those still requires its exact current write permission inside the committing transaction. The other six financial POSTs stay forbidden. Contract v2 can list those three write capabilities when both keys are enabled. The browser stores an identity-bound recovery descriptor before the POST. The write key stays unset in deployed configuration until that bundle is reviewed. See [docs/adr/0020-identity-bound-financial-recovery.md](docs/adr/0020-identity-bound-financial-recovery.md). See [docs/adr/0019-transactional-financial-write-authorization.md](docs/adr/0019-transactional-financial-write-authorization.md). `GET /api/session` negotiates contract v1 by default and contract v2 only when the request sends `X-FinOpsDesk-Session-Contract: 2`. Contract v1 keeps `financialAccess: false` and omits `permissions`, including after activation, so older browser bundles stay compatible. Contract v2 returns `permissions` with only enabled financial read capabilities and sets `financialAccess` from that nonempty list. See [docs/adr/0018-read-only-financial-authorization.md](docs/adr/0018-read-only-financial-authorization.md). The session returns the provider issuer and subject as separate claims, plus `organizations` for active memberships of active organizations and `activeOrganization` when the remembered selection is currently valid. It does not return an email address, a role name, or OAuth tokens. An optional `name` claim is copied only as a display name. Email is not the stable identifier. A completed sign-in stores one `BrowserSession` and puts its id in the cookie as `finopsdesk_sid`. A cookie without that id is rejected. A failed membership or session read is HTTP 503 `auth.membership.unavailable`, not an empty list and not a sign-out. A contract v2 role-table failure is HTTP 503 `auth.authorization.unavailable` and does not clear the cookie. A disabled or missing application user is signed out. Disabling the account retires its session stamp, and restoring the account does not accept the cookie issued before that disable. Sign-in can create an application user and does not create a membership, select an organization, grant a role, or rotate the stamp of an existing user. Role assignments live in Access. The API does not expose a role-administration route. `FinOpsDesk.AccessBootstrap` is a separate console. It reads `ConnectionStrings__FinOpsDesk` and does not print that value. Commands:

- `create-organization --organization-id <guid> --name <name>`
- `grant-membership --issuer <issuer> --subject <subject> --organization-id <guid>`
- `grant-role --issuer <issuer> --subject <subject> --organization-id <guid> --role <Viewer|Operator|ReversalApprover|OrganizationAdmin> --operator <declared-operator>`
- `revoke-role` takes the same arguments as `grant-role`
- `disable-user --issuer <issuer> --subject <subject> --operator <declared-operator>`
- `revoke-membership --issuer <issuer> --subject <subject> --organization-id <guid> --operator <declared-operator>`
- `disable-organization --organization-id <guid> --operator <declared-operator>`

`--operator` is required for the role commands and the three emergency commands. It is declared attribution, not a password and not an authenticated administrator. The console does not read `Environment.UserName` and does not verify the string with the identity provider. Running the console is authorized by the operating system, the deployment environment, and the SQL credential. Restrict who can execute the console and who can connect with its SQL credential. Emergency commands write `access.OperationalSecurityEvents` in the same transaction as the security change. A repeat is `AlreadyApplied` and does not change the security row again. There is no command to re-enable a user, restore a membership, or re-enable an organization. See [docs/adr/0017-organization-roles.md](docs/adr/0017-organization-roles.md) and [docs/adr/0021-operational-access-emergency-controls.md](docs/adr/0021-operational-access-emergency-controls.md).

Browser session routes, registered only for `ProductionCookie`:

- `GET /api/session` is anonymous. It returns `authenticated`, `financialAccess`, `activeOrganization`, and a CSRF request token. An authenticated cookie also returns `issuer`, `subject`, `displayName`, `expiresAt`, and `organizations` when validation succeeds. Contract v2 also returns `permissions`. It does not choose an organization and does not reissue the application cookie. The response uses `Cache-Control: no-store` and `Vary: X-FinOpsDesk-Session-Contract`. Membership validation failure is HTTP 503 `auth.membership.unavailable`. Contract v2 authorization failure is HTTP 503 `auth.authorization.unavailable`.
- `POST /api/session/organization` requires the cookie session and the CSRF token issued for that exact issuer and subject. The JSON body is one `organizationId` string. Success is HTTP 204, keeps the same `finopsdesk_sid`, and keeps the original cookie expiry. It does not insert another browser session. A malformed body is HTTP 400 `auth.organization.invalid`. An organization outside the caller's active memberships is HTTP 403 `auth.organization.forbidden` and does not replace the ticket. The response uses `Cache-Control: no-store`.
- `POST /login` is anonymous and requires a valid anonymous CSRF token. The browser sends it as the form field `__RequestVerificationToken` and navigates to the provider. Other clients send `X-CSRF-Token`. It challenges the OpenID Connect handler. `returnUrl` must be a local path. An external URL is rejected. There is no GET login.
- The callback path is `/signin-oidc`. The OpenID Connect handler redeems the authorization code. The browser redirect URI is `Authentication:Oidc:BrowserOrigin` plus the callback path. It is not taken from `Host` or `X-Forwarded-*`.
- `POST /logout` requires the cookie session and a CSRF token issued for that session. It revokes that browser session in SQL Server and then clears the local cookie. HTTP 204 is returned only after the revocation is persisted. A database failure is HTTP 503 `auth.membership.unavailable` and is not a confirmed logout. Another browser session for the same user stays valid. It does not call the provider end-session endpoint. An anonymous CSRF token is not accepted after sign-in. There is no GET logout.

Unsafe `ProductionCookie` requests (`POST`, `PUT`, `PATCH`, and `DELETE`) on `/login`, `/logout`, `/api/session/organization`, and the `/api` group require the antiforgery cookie plus `X-CSRF-Token`. The token is also bound to the exact issuer and subject. An anonymous token does not authorize an authenticated request, and a token for one identity does not authorize another. Switching organizations does not invalidate the token. Validation runs after authorization and before the endpoint. A missing or invalid token returns 400 with `auth.csrf.invalid` and does not run the handler. A financial request can still return 401 or 403 before that check. `DevelopmentJwt` does not apply this rule.

`Authentication:Oidc` is required only for `ProductionCookie`. The host fails startup when a required value is missing or invalid. Do not commit a real client secret. Set these values in the host environment:

- `Authority`: absolute HTTPS issuer URL. HTTP is accepted only for a loopback host when `RequireHttpsMetadata` is explicitly false. A remote HTTP authority is rejected.
- `ClientId`: the confidential client id.
- `ClientSecret`: the confidential client secret.
- `BrowserOrigin`: the trusted browser origin, with no path, query, or user info. HTTP is accepted only for `localhost` or `127.0.0.1`.
- `CallbackPath`: optional, default `/signin-oidc`.
- `RequireHttpsMetadata`: optional, default `true`.

The flow is authorization code with PKCE, scope `openid` only, `SaveTokens` false, and no `offline_access`. Provider-wide logout is not implemented: it would need an ID token hint, and tokens are not stored. Local logout does not depend on the provider. It revokes one `BrowserSession` row. More than one API instance needs a shared ASP.NET Core Data Protection key ring and the shared Access database. This increment does not add that key ring or a distributed session cache. See [docs/adr/0016-server-session-revocation.md](docs/adr/0016-server-session-revocation.md).

Ordinary Vite development uses `DevelopmentJwt`. `npm run dev:cookie` uses the cookie session and proxies `/login`, `/logout`, and `/signin-oidc` as well as `/api` and `/health`. The cookie screen shows the server's organization list and posts a selection only after an explicit click. HTTP 204 is not the active organization; the next `GET /api/session` is. A production bundle always uses the cookie session. There is no production identity-provider deployment in this repository. The local startup, form sign-in, selection, and sign-out limits are documented in [docs/frontend-development.md](docs/frontend-development.md).

`OrganizationClaimType` defaults to `org_id` on the development JWT path. The trusted issuer must place the caller's organization id in that claim. Query strings, route values, bodies, and headers such as `X-Organization-Id` are not tenant selectors. On a production cookie, `finopsdesk_org` remembers a selection. It becomes a tenant only after Access accepts that organization for the current user and the `BrowserSession` is still active. See [docs/adr/0015-active-organization-session.md](docs/adr/0015-active-organization-session.md) and [docs/adr/0016-server-session-revocation.md](docs/adr/0016-server-session-revocation.md).

### Migrations

Restore the local EF tool, then add a migration with the cmd helper:

```bat
dotnet tool restore
scripts\ef-migrations-add.cmd MigrationName
```

The startup project is `FinOpsDesk.Api`. Migrations are stored in the Reconciliation module under `Infrastructure/Persistence/Migrations`.

### Integration tests

Persistence tests in `FinOpsDesk.IntegrationTests` run against real SQL Server. They do not skip when a database is missing.

Local options:

- SQL Server LocalDB (Windows default);
- `FINOPSDESK_TEST_SQLSERVER` pointing at another SQL Server instance. That login must be allowed to create and drop temporary integration-test databases.

CI uses a SQL Server 2022 service container. Backend CI runs architecture, unit, and integration tests. The Chromium project is a separate job, `Chromium browser security`, in `.github/workflows/e2e-ci.yml`. It uses the same test SQL contract and creates its own `FinOpsDesk_E2E_<guid>` databases. Local setup, the development certificate boundary, and the Ubuntu run are documented in [docs/e2e-testing.md](docs/e2e-testing.md). The workflow does not make that job a required merge check. Rulesets are not enforced on this private repository under its current GitHub plan, so the owner verifies the backend, frontend, and Chromium checks manually before merging. See [docs/adr/0022-chromium-e2e-pull-request-gate.md](docs/adr/0022-chromium-e2e-pull-request-gate.md).

Run repository verification with:

```bat
scripts\verify.cmd
```

## Local frontend

The React application lives in `src/frontend`. Local setup, the development access token, the cookie session, HTTPS trust, and the invoice register are documented in [docs/frontend-development.md](docs/frontend-development.md). The default local UI uses `DevelopmentJwt`. Cookie mode and a production build call `GET /api/session` and do not unlock financial routes.

