# FinOpsDesk

FinOpsDesk is B2B finance operations and reconciliation platform built with .NET and React.

The project models an accounts-receivable cash application workflow: customer invoices and incoming bank transactions are imported, deterministic matching suggestions are generated, finance users confirm or reject allocations, exceptions are investigated, and financially relevant actions are audited.

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

