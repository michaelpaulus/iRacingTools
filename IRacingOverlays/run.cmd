@echo off
rem Starts the overlays from this folder so they find overlays.json.
cd /d "%~dp0"
dotnet run -c Release -- %*
