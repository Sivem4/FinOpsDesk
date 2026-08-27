@echo off
setlocal

pushd "%~dp0.."

echo.
echo ========================================
echo FinOpsDesk verification
echo ========================================
echo.

echo [1/4] Restoring dependencies...
dotnet restore FinOpsDesk.slnx
if errorlevel 1 goto :failed

echo.
echo [2/4] Verifying formatting...
dotnet format FinOpsDesk.slnx --no-restore --verify-no-changes
if errorlevel 1 goto :failed

echo.
echo [3/4] Building Release...
dotnet build FinOpsDesk.slnx --configuration Release --no-restore
if errorlevel 1 goto :failed

echo.
echo [4/4] Running tests...
dotnet test FinOpsDesk.slnx --configuration Release --no-build --no-restore
if errorlevel 1 goto :failed

echo.
echo ========================================
echo SUCCESS: All verification steps passed.
echo ========================================
echo.

popd
exit /b 0

:failed
echo.
echo ========================================
echo FAILED: Verification stopped.
echo ========================================
echo.

popd
exit /b 1