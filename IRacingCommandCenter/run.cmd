@echo off
rem Builds the Command Center and starts it from its build output. Options: -NoStart
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 pause
