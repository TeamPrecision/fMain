@echo off
title fMain — PCBA Test System
echo ============================================================
echo  fMain PCBA Test System
echo ============================================================
echo.

cd /d "%~dp0fMain"

echo Starting server...
start "fMain Server" cmd /k "dotnet run --project fMain.csproj --configuration Release"

echo Waiting for server to be ready on http://localhost:49600 ...
:WAIT
timeout /t 2 /nobreak >nul
powershell -Command "try { Invoke-WebRequest -Uri 'http://localhost:49600' -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop | Out-Null; exit 0 } catch { exit 1 }" >nul 2>&1
if errorlevel 1 goto WAIT

echo Server is ready — opening browser...
start "" "http://localhost:49600"
