@echo off
rem Opens the iRacing Broadcast window (drag an .rpy onto this file to open it with that replay).
cd /d "%~dp0"
dotnet build -c Release -v q -nologo || (pause & exit /b 1)
start "" "bin\Release\net10.0-windows\IRacingBroadcast.exe" %*
