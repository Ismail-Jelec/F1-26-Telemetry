@echo off
rem Double-click this to download the latest changes from GitHub and reinstall the app.
cd /d "%~dp0"
git pull
if errorlevel 1 (
  echo.
  echo git pull failed - see the message above.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
