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

The API validates JWT access tokens. It does not issue them.

`Authentication:JwtBearer` is required before the host will start:

- Production: set `Authority` and `Audience`. The authority's OpenID Connect metadata supplies signing keys. `RequireHttpsMetadata` defaults to `true`. An optional `Issuer` overrides the issuer published by that metadata.
- Local or test validation without an identity provider: set `Issuer`, `Audience`, and `SigningKey` (at least 32 UTF-8 bytes). Do not set `Authority` in that mode, and do not commit the signing key.

`OrganizationClaimType` defaults to `org_id`. The trusted issuer must place the caller's organization id in that claim. Query strings, route values, bodies, and headers such as `X-Organization-Id` are not tenant selectors.

Example production environment variables:

```text
Authentication__JwtBearer__Authority=https://login.example.com
Authentication__JwtBearer__Audience=finopsdesk-api
Authentication__JwtBearer__OrganizationClaimType=org_id
```

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

