@echo off
rem Presses iRacing's Mute Voice Chat keys two ways, to find which one iRacing takes.
rem Play a bit of replay where someone is talking first.
"%~dp0bin\Release\net10.0-windows\IRacingBroadcast.exe" --test-mute
pause
