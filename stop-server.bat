@echo off
rem ProjectH: stops the game server and the Monitoring Server started by run-server.bat, then MySQL.
rem   stop-server.bat            server + Monitoring Server + MySQL
rem   stop-server.bat --keep-db  server + Monitoring Server only
rem Only processes running this repository's Release builds (bin\Release\net10.0\ProjectH.Server.dll and
rem ProjectH.Monitoring.dll) are stopped; other dotnet processes are left alone. This is a hard stop: for a clean
rem shutdown (clients told ServerShutdown, pending match history written) press Ctrl+C in the server window instead.
rem MySQL is stopped, not removed: the container and its data stay (docker compose stop, never down -v).
setlocal
cd /d "%~dp0"

echo [ProjectH] Stopping the game server...
powershell -NoProfile -Command "$p = Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*\bin\Release\net10.0\ProjectH.Server.dll*' }; if (-not $p) { Write-Host '[ProjectH] No running server found.' } else { foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force; Write-Host ('[ProjectH] Stopped server process ' + $x.ProcessId + '.') } }"

echo [ProjectH] Stopping the Monitoring Server...
powershell -NoProfile -Command "$p = Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*\bin\Release\net10.0\ProjectH.Monitoring.dll*' }; if (-not $p) { Write-Host '[ProjectH] No running Monitoring Server found.' } else { foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force; Write-Host ('[ProjectH] Stopped Monitoring Server process ' + $x.ProcessId + '.') } }"

if /i "%~1"=="--keep-db" (
    echo [ProjectH] MySQL left running.
    goto done
)
where docker >nul 2>nul
if errorlevel 1 (
    echo [ProjectH] Docker not found: nothing to stop for the database.
    goto done
)
echo [ProjectH] Stopping MySQL...
docker compose stop mysql

:done
echo [ProjectH] Done.
pause
