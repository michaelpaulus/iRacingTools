@echo off
rem Starts the coach from this folder so it finds coach.json, corners.json and coach_notes.md.
cd /d "%~dp0"
dotnet run -c Release -- %*
