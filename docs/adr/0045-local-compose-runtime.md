# ADR 0045: Local Compose runtime

- Status: Accepted
- Date: 2026-11-10

## Context

Local development already runs the API from the `https` launch profile and Vite from `scripts\dev-frontend.ps1`, with SQL Server LocalDB catalog `FinOpsDesk`. The host does not apply migrations at startup. Phase 9C keeps three rate-limit budgets inside one process. ADR 0044 records that two API instances do not share those budgets, and the infrastructure notes defer Redis until a measurement shows a shared-state requirement. ADR 0016 requires a shared Access database and a shared Data Protection key ring only when more than one API instance serves the cookie session.

Operators asked for one command that starts the development frontend, one API, and SQL Server after the local secrets exist. The production frontend bundle selects the cookie session and does not contain the development token screen, so this stack has to run Vite in development.

## Decision

`finopsdesk-local` is the Compose project. `docker compose -f deploy/compose/compose.yaml up --wait --wait-timeout 600 --build` is the daily command. `--wait-timeout 600` is Compose's wait for dependency health and for the one-shot migrator. It does not cap the image build. `FINOPSDESK_WEB_PUBLISH` is the complete published-port mapping. Compose does not append another container port. The daily value is `127.0.0.1:5173:5173`. API and SQL are not published. The web container runs `npm run dev` with `--host 0.0.0.0 --port 5173 --strictPort` and sets `FINOPSDESK_E2E_API_ORIGIN=https://api:8443`. The Vite proxy keeps `secure: true`.

The operator prepares `%USERPROFILE%\.finopsdesk\compose\` with `scripts\compose-prepare.ps1`. The set is `sql-password.txt`, `jwt-signing-key.txt`, `api.crt`, and `api.key`. The SQL password is random and meets the SQL Server complexity rules without using `;`. The JWT key is at least 32 UTF-8 bytes and is not the integration-test key. The certificate is PEM with SAN `DNS:api`, and the private key is matching PEM PKCS#8. A valid set is reused byte for byte. A partial or invalid set fails without overwriting or deleting files. When none of those four files exist and the resolved path is outside the repository, the script creates the directory and applies the operator ACL before the first secret byte. A failed create deletes only the files that attempt created. The directory ACL belongs to the current operator. Existing junctions, symlinks, and secret files are resolved before that create, ACL change, or write. A result inside the repository, or a redirect that cannot be resolved, is refused with no changes.

Compose `file` secrets are bind mounts. This stack does not use `uid`, `gid`, or `mode`. Each service mounts only the files it needs. `sql`, `migrate`, and `api` mount the SQL password. Only `api` mounts the JWT key and the TLS private key. `web` mounts the public certificate for `NODE_EXTRA_CA_CERTS`. Secrets are not build arguments, image layers, `VITE_` variables, or `Config.Env` entries. Entrypoints read the files and do not echo them. SQL uses `SQLCMDPASSWORD` and does not pass `-P`.

`FinOpsDesk.ComposeMigrate` is a separate process. Its final image is the pinned ASP.NET image, because the published `runtimeconfig.json` requires `Microsoft.AspNetCore.App` 10.0.0 as well as `Microsoft.NETCore.App` 10.0.0. It does not reference `FinOpsDesk.Api` and it does not receive the JWT key. One execution applies the existing Reconciliation migrations, then the Access migrations, using `reconciliation.__EFMigrationsHistory` and `access.__EFMigrationsHistory`. Exit 0 means both contexts report no pending migrations. The API process still does not migrate. Both connection strings use `sa`, catalog `FinOpsDesk_Compose`, and `Encrypt=True;TrustServerCertificate=True` on the private network. The SQL volume is the project-scoped `sql_data` volume.

Order is healthy `sql`, completed `migrate`, healthy `api`, then healthy `web`. The SQL probe is `SELECT 1`. The API probe is `https://api:8443/health` with `curl --cacert` pointed at the public PEM. The Vite probe is HTTP `GET /` on `127.0.0.1:5173` inside the web container. Probe timing is the agreed start periods, intervals, timeouts, and retries. `GET /ready` remains the API's SQL connectivity check.

Smoke uses a new project name `finopsdesk-smoke-<guid>`, the volume that project creates on its first start, and `FINOPSDESK_WEB_PUBLISH=127.0.0.1:5174:5173`. The failure file replaces only the migrator password, at the same target `sql_password`, using `!override`. Docker Compose 2.24.4 or newer is required for that tag. A container left `created` with the zero `StartedAt` has not run. This phase does not add Redis or a second API.

The derived SQL image returns to the pinned image user `mssql` before its entrypoint and sets `CMD ["/opt/mssql/bin/sqlservr"]`. That entrypoint refuses to start SQL as root. It also requires the password file to be readable and `/var/opt/mssql` to be writable by that user, and it does not print either path's contents. It then runs `exec /opt/mssql/bin/launch_sqlservr.sh "$@"` so the image command reaches the pinned launcher.

Image references are pinned to manifest-list digests resolved on 2026-11-10: .NET SDK and ASP.NET `10.0`, Node `24.18.0`, and SQL Server `2022-CU22-ubuntu-22.04`. The API and the migrator both use the ASP.NET digest. The required platform is Linux `amd64`. The build context excludes `.env` and `.env.*` at the repository root and in subdirectories. Compose runtime settings stay in the Compose file and the process environment.

## Consequences

- The LocalDB workflow and `scripts\dev-token.ps1` stay in place. Compose is an additional local entry.
- A restart of the one API clears the Phase 9C counters and keeps the SQL volume when the volume is not removed.
- The first run on Docker Desktop still has to confirm `/opt/mssql/bin/launch_sqlservr.sh` and `/opt/mssql-tools18/bin/sqlcmd`, that the ASP.NET image can install `curl` and can host the migrator, that `!override` renders a single migrator secret, and that user `mssql` can read the SQL secret and write `/var/opt/mssql` without printing the secret.
- Roadmap Phase 10A stays Planned until that smoke, including the authentication gate and the retained invoice, has passed.
