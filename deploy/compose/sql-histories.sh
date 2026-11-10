#!/bin/sh
set -eu
password=$(tr -d '\r\n' < /run/secrets/sql_password)
export SQLCMDPASSWORD="$password"
unset password
exec /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -h -1 -W -Q "SET NOCOUNT ON; IF DB_ID(N'FinOpsDesk_Compose') IS NULL BEGIN SELECT N'absent'; RETURN; END; IF OBJECT_ID(N'FinOpsDesk_Compose.reconciliation.__EFMigrationsHistory', N'U') IS NULL AND OBJECT_ID(N'FinOpsDesk_Compose.access.__EFMigrationsHistory', N'U') IS NULL SELECT N'absent'; ELSE SELECT N'present';"
