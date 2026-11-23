@echo off
setlocal
echo Install RelaxDesk for the current user, with desktop shortcuts.
echo This does not start sharing, enable startup, or change firewall rules.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0scripts\Install-RelaxDesk.ps1" -DesktopShortcuts
if errorlevel 1 (
  echo Installation failed. Review the error above; no execution policy is changed.
  pause
  exit /b 1
)
echo Installation complete. Start RelaxDesk from the desktop or Start menu.
pause
