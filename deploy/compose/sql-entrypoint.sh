#!/bin/sh
set -eu
if [ "$(id -u)" -eq 0 ]; then
  echo "SQL Server must start as the image user, not root." >&2
  exit 1
fi
if [ ! -r /run/secrets/sql_password ]; then
  echo "SQL password file is not readable by the image user." >&2
  exit 1
fi
if [ ! -d /var/opt/mssql ] || [ ! -w /var/opt/mssql ]; then
  echo "SQL data directory is not writable by the image user." >&2
  exit 1
fi
password=$(tr -d '\r\n' < /run/secrets/sql_password)
export MSSQL_SA_PASSWORD="$password"
unset password
exec /opt/mssql/bin/launch_sqlservr.sh "$@"
