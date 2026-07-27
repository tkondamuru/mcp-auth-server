@echo off
setlocal enabledelayedexpansion

REM Build and package EcomBot Azure Functions into a zip.
REM Usage:
REM   build-package.bat
REM   build-package.bat Release

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set ROOT=%~dp0
set PROJECT=%ROOT%src\McpServer\McpServer.csproj
set ZIPDIR=%ROOT%artifacts
set PACKAGE_DIR=%ZIPDIR%\publish
set ZIPFILE=%ZIPDIR%\mcp-server-%CONFIG%.zip
set DEFAULT_PUBLISH_DIR=%ROOT%src\McpServer\bin\%CONFIG%\net10.0\publish

if not exist "%PROJECT%" (
  echo [ERROR] Could not find project file: %PROJECT%
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] dotnet SDK is not installed or not on PATH.
  exit /b 1
)

echo [1/5] Cleaning old artifacts...
if exist "%PACKAGE_DIR%" rmdir /s /q "%PACKAGE_DIR%"
if exist "%ZIPFILE%" del /q "%ZIPFILE%"
if not exist "%ZIPDIR%" mkdir "%ZIPDIR%"

echo [2/5] Restoring dependencies...
dotnet restore "%PROJECT%"
if errorlevel 1 (
  echo [ERROR] dotnet restore failed.
  exit /b 1
)

echo [3/5] Publishing (%CONFIG%) to default output...
dotnet publish "%PROJECT%" -c %CONFIG%
if errorlevel 1 (
  echo [ERROR] dotnet publish failed.
  exit /b 1
)

REM Handle potential nested folders or different SDK behaviors
if not exist "%DEFAULT_PUBLISH_DIR%" (
    echo [DEBUG] Checking alternative publish paths...
    if exist "%ROOT%src\McpServer\bin\%CONFIG%\net10.0\win-x64\publish" set DEFAULT_PUBLISH_DIR=%ROOT%src\McpServer\bin\%CONFIG%\net10.0\win-x64\publish
)

echo [DEBUG] Source: %DEFAULT_PUBLISH_DIR%
echo [DEBUG] Target: %PACKAGE_DIR%

if not exist "%DEFAULT_PUBLISH_DIR%" (
  echo [ERROR] Expected publish directory not found: %DEFAULT_PUBLISH_DIR%
  exit /b 1
)

echo [4/5] Copying publish output to artifacts folder...
robocopy "%DEFAULT_PUBLISH_DIR%" "%PACKAGE_DIR%" /E /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
  echo [ERROR] Robocopy failed with error level %errorlevel%.
  exit /b 1
)

REM Verify that the package directory is NOT empty
dir "%PACKAGE_DIR%" /b /a-d >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Package directory is empty. Nothing to zip.
    exit /b 1
)

echo [5/5] Creating zip package using PowerShell...
powershell -Command "if (Test-Path '%ZIPFILE%') { Remove-Item '%ZIPFILE%' }; Compress-Archive -Path '%PACKAGE_DIR%\*' -DestinationPath '%ZIPFILE%' -Force"

if errorlevel 1 (
  echo [ERROR] Failed to create zip package using PowerShell.
  exit /b 1
)

echo.
echo [SUCCESS] Package created:
echo   %ZIPFILE%
echo.
echo Deployment options:
echo   1) Azure Portal - App Service - Deployment Center - Zip Deploy
echo   2) Azure CLI:
echo      az webapp deployment source config-zip --resource-group ^<RG^> --name ^<APP_SERVICE_NAME^> --src "%ZIPFILE%"

exit /b 0
