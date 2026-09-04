@echo off
setlocal

if "%~1"=="" (
    echo Usage: scripts\ef-migrations-add.cmd MigrationName
    exit /b 1
)

pushd "%~dp0.."

dotnet ef migrations add %1 --project src\backend\Modules\FinOpsDesk.Modules.Reconciliation\FinOpsDesk.Modules.Reconciliation.csproj --startup-project src\backend\FinOpsDesk.Api\FinOpsDesk.Api.csproj --output-dir Infrastructure\Persistence\Migrations --context ReconciliationDbContext
set EXIT_CODE=%ERRORLEVEL%

popd
exit /b %EXIT_CODE%
