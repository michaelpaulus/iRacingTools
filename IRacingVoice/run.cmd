@echo off
rem Starts the voice agent from this folder so it finds voice.json and the Whisper model.
cd /d "%~dp0"
dotnet run -c Release -- %*
