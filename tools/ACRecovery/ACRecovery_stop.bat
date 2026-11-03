@echo off
rem Stops the AC crash/relaunch logger.
powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*ACRecovery.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host ('Stopped ACRecovery pid ' + $_.ProcessId) }"
pause
