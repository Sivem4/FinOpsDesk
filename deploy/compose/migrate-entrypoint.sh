#!/bin/sh
set -eu
password=$(tr -d '\r\n' < /run/secrets/sql_password)
export ConnectionStrings__FinOpsDesk="Server=sql,1433;Database=FinOpsDesk_Compose;User ID=sa;Password=${password};Encrypt=True;TrustServerCertificate=True;Connect Timeout=30"
unset password
exec dotnet FinOpsDesk.ComposeMigrate.dll
