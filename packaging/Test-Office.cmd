@echo off
setlocal
cd /d "%~dp0"
echo Synthetic loopback reliability checks. No physical desktop or input acceptance.
"Tools\Tests\RelaxDesk.Tests.exe"
if errorlevel 1 goto failed
for %%M in (--video-capture-recovery --video-capture-exhausted --video-control-timeout --video-idle-control) do (
  "Tools\Smoke\RelaxDesk.Smoke.exe" "%CD%\Host\RelaxDesk.Host.exe" %%M
  if errorlevel 1 goto failed
)
echo PASS office reliability automation. Physical Home, visible image and real input remain unverified.
exit /b 0
:failed
echo FAIL office reliability automation. Preserve this output and Host logs.
exit /b 1
