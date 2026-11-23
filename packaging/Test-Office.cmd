@echo off
setlocal
cd /d "%~dp0"
echo Synthetic office capability checks. No physical desktop or input acceptance.
"Tools\Smoke\RelaxDesk.Smoke.exe" --collect-package "%CD%" --office-only %*
set "result=%errorlevel%"
echo.
echo Exit code: %result%. See logs\office-*\result.json for individual results.
echo Physical Home, visible image, real input and audible audio remain unverified.
exit /b %result%
