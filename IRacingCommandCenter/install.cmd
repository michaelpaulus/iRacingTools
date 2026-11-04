@echo off
rem Installs or updates the iRacing Command Center. Double-click to run. Options: -NoStart
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
