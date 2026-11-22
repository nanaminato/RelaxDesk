@echo off
setlocal
"%~dp0Tools\Smoke\RelaxDesk.Smoke.exe" --collect-package "%~dp0." %*
set "result=%errorlevel%"
echo.
echo Exit code: %result%. See logs folder and RUN-ME.zh-CN.md.
pause
exit /b %result%
