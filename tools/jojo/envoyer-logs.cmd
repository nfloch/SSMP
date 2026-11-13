@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0envoyer-logs.ps1" %*
pause
