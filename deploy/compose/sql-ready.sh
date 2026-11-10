#!/bin/sh
set -eu
password=$(tr -d '\r\n' < /run/secrets/sql_password)
export SQLCMDPASSWORD="$password"
unset password
exec /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -Q "SELECT 1"
