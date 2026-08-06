@echo off
where pwsh >nul 2>nul
if errorlevel 1 (
  echo [ERROR] PowerShell 7 is required: https://aka.ms/powershell
  pause
  exit /b 1
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Stop-EdrTest.ps1"
if errorlevel 1 pause
