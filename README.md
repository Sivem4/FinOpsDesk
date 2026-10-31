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

- `DevelopmentJwt` validates JWT access tokens and does not issue them. It is allowed only when the host environment is `Development` or `E2E`. `appsettings.Development.json` selects it. Set `Issuer`, `Audience`, and `SigningKey` (at least 32 UTF-8 bytes), or set `Authority` and `Audience` instead of a signing key. Do not set `Authority` and `SigningKey` together, and do not commit the signing key.
- `ProductionCookie` is the default in `appsettings.json`. It registers only an ASP.NET Core cookie scheme. It does not register JWT bearer and it does not accept a bearer token, including when JWT settings are present in the environment. Phase 7A does not sign a browser in. Financial routes stay closed until organization membership is implemented. OpenID Connect is a later Phase 7 increment.

`OrganizationClaimType` defaults to `org_id` on the development JWT path. The trusted issuer must place the caller's organization id in that claim. Query strings, route values, bodies, and headers such as `X-Organization-Id` are not tenant selectors. A claim on a production cookie is not membership.

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

CI uses a SQL Server 2022 service container.

Run repository verification with:

```bat
scripts\verify.cmd
```

## Local frontend

The React application lives in `src/frontend`. Local setup, the development access token, HTTPS trust, and the invoice register are documented in [docs/frontend-development.md](docs/frontend-development.md). Production browser sign-in remains Phase 7.

