@echo off
rem Starts the AC crash/relaunch logger hidden (only one copy ever runs).
start "" powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\Games\ACRecovery\ACRecovery.ps1"
