@echo off
rem ProjectH game server launcher (Windows): MySQL (Docker) + Monitoring Server + game server.
rem   run-server.bat                       start MySQL, Release build, Monitoring Server (own window, 127.0.0.1:5080),
rem                                        game server on UDP 7777 (appsettings.json) reporting to it
rem   run-server.bat --Server:Port=7790    any option after the name is passed to the game server
rem   run-server.bat --Server:AirDrop=false --Server:MinPlayers=2
rem   run-server.bat --Monitoring:Enabled=false   the game server does not report (the Monitoring Server still starts)
rem Dashboard: http://127.0.0.1:5080 (Docs/Monitoring.md).
rem Needs the .NET 10 SDK, the ASP.NET Core 10 runtime (Docs/QA.md) and Docker Desktop for the database.
rem Without Docker the server still runs: match history saves fail and are counted.
rem Stop: Ctrl+C in this window stops the game server only (clean shutdown); the Monitoring Server keeps running in its
rem own window, so the dashboard shows the server OFFLINE. stop-server.bat stops the game server, the Monitoring Server
rem and MySQL.
setlocal
cd /d "%~dp0"
set "MONITORING_URL=http://127.0.0.1:5080"
set "MONITORING_DLL=Server\src\ProjectH.Monitoring\bin\Release\net10.0\ProjectH.Monitoring.dll"

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
echo [ProjectH] Building server and Monitoring Server (Release)...
dotnet build Server\src\ProjectH.Server\ProjectH.Server.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo [ProjectH] Build failed.
    pause
    exit /b 1
)
dotnet build Server\src\ProjectH.Monitoring\ProjectH.Monitoring.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo [ProjectH] Monitoring Server build failed.
    pause
    exit /b 1
)

rem ---------- 3. Monitoring Server (own window: it must outlive the game server, Docs/Monitoring.md) ----------
rem A Monitoring Server that already answers /health (an earlier run-server.bat) is reused, not started twice.
call :monitoringup
if not errorlevel 1 (
    echo [ProjectH] Monitoring Server already running at %MONITORING_URL%.
    goto server
)
echo [ProjectH] Starting Monitoring Server at %MONITORING_URL% ^(new window^)...
start "ProjectH Monitoring" dotnet "%MONITORING_DLL%" --Urls=%MONITORING_URL%
set /a WAITED=0
:waitmon
call :monitoringup
if not errorlevel 1 goto monready
if %WAITED% GEQ 15 (
    echo [ProjectH] Monitoring Server did not answer within 15 s. Starting the game server anyway ^(monitoring is best effort^).
    goto server
)
timeout /t 1 /nobreak >nul
set /a WAITED+=1
goto waitmon
:monready
echo [ProjectH] Monitoring Server is ready. Dashboard: %MONITORING_URL%

rem ---------- 4. Server ----------
:server
echo [ProjectH] Starting server. Press Ctrl+C to stop (MySQL and the Monitoring Server keep running; stop-server.bat stops all).
rem Options after the batch name come last, so they override these (e.g. --Monitoring:Enabled=false).
dotnet Server\src\ProjectH.Server\bin\Release\net10.0\ProjectH.Server.dll --Monitoring:Enabled=true --Monitoring:Endpoint=%MONITORING_URL% %*
set EXITCODE=%ERRORLEVEL%
echo [ProjectH] Server exited with code %EXITCODE%.
pause
exit /b %EXITCODE%

rem Exit code 0 when the Monitoring Server answers GET /health, 1 otherwise.
:monitoringup
powershell -NoProfile -Command "try { $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 '%MONITORING_URL%/health'; if ($r.StatusCode -eq 200) { exit 0 } } catch { }; exit 1" >nul 2>nul
exit /b %ERRORLEVEL%
