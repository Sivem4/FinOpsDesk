#!/bin/sh
set -eu
password=$(tr -d '\r\n' < /run/secrets/sql_password)
key=$(tr -d '\r\n' < /run/secrets/jwt_signing_key)
export ConnectionStrings__FinOpsDesk="Server=sql,1433;Database=FinOpsDesk_Compose;User ID=sa;Password=${password};Encrypt=True;TrustServerCertificate=True;Connect Timeout=30"
export Authentication__JwtBearer__SigningKey="$key"
unset password
unset key
exec dotnet FinOpsDesk.Api.dll
