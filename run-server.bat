@echo off
rem ProjectH game server launcher (Windows): MySQL (Docker) + game server.
rem   run-server.bat                       start MySQL, Release build, run on UDP 7777 (appsettings.json)
rem   run-server.bat --Server:Port=7790    any option after the name is passed to the server
rem   run-server.bat --Server:AirDrop=false --Server:MinPlayers=2
rem Needs the .NET 10 SDK, the ASP.NET Core 10 runtime (Docs/QA.md) and Docker Desktop for the database.
rem Without Docker the server still runs: match history saves fail and are counted.
rem Stop: Ctrl+C in this window (clean shutdown), or stop-server.bat (also stops MySQL).
setlocal
cd /d "%~dp0"

rem ---------- 1. MySQL (docker-compose.yml service "mysql", container projecth-mysql) ----------
where docker >nul 2>nul
if errorlevel 1 (
    echo [ProjectH] Docker not found: starting the server without the database.
    goto build
)
echo [ProjectH] Starting MySQL...
docker compose up -d mysql
if errorlevel 1 (
    echo [ProjectH] Could not start MySQL ^(is Docker Desktop running?^). Starting the server without the database.
    goto build
)
set /a WAITED=0
:waitdb
set "DBHEALTH="
for /f "delims=" %%H in ('docker inspect -f "{{.State.Health.Status}}" projecth-mysql 2^>nul') do set "DBHEALTH=%%H"
if /i "%DBHEALTH%"=="healthy" goto dbready
if %WAITED% GEQ 180 (
    echo [ProjectH] MySQL is not healthy after 180 s ^(status: %DBHEALTH%^). Starting the server anyway.
    goto build
)
timeout /t 2 /nobreak >nul
set /a WAITED+=2
goto waitdb
:dbready
echo [ProjectH] MySQL is ready.

rem ---------- 2. Build ----------
:build
echo [ProjectH] Building server (Release)...
dotnet build Server\src\ProjectH.Server\ProjectH.Server.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo [ProjectH] Build failed.
    pause
    exit /b 1
)

rem ---------- 3. Server ----------
echo [ProjectH] Starting server. Press Ctrl+C to stop (MySQL keeps running; stop-server.bat stops both).
dotnet Server\src\ProjectH.Server\bin\Release\net10.0\ProjectH.Server.dll %*
set EXITCODE=%ERRORLEVEL%
echo [ProjectH] Server exited with code %EXITCODE%.
pause
exit /b %EXITCODE%
