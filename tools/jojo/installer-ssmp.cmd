@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer-ssmp.ps1" %*
pause
