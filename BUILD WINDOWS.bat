@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Roblox Price Tracker - Build Windows EXE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Windows.ps1"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Build failed with error code %ERR%.
  echo See: logs\build.log
  echo.
  pause
)
exit /b %ERR%
