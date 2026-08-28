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
curl -s -f -o nul http://localhost:49600 2>nul
if errorlevel 1 goto WAIT

echo Server is ready — opening browser...
start "" "http://localhost:49600"
