param([switch]$Graphics, [switch]$Wpf, [switch]$Capture, [switch]$SkipBuild, [string]$NativeBuildDirectory = 'build/native', [string]$PackageRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot); $SkipBuild = $true }
function Run-App([string]$Application, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments) {
    if ([IO.Path]::GetExtension($Application) -eq '.exe') { & $Application @Arguments }
    else { & dotnet $Application @Arguments }
    $global:LASTEXITCODE = $LASTEXITCODE
}
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    if (!$SkipBuild) {
        & "$PSScriptRoot/build-native.ps1" -BuildDirectory $NativeBuildDirectory
        dotnet build RelaxDesk.sln -c Release
        if ($LASTEXITCODE) { throw 'Managed build failed' }
    }
    $hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
    $smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
    $viewer = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
    $tests = Join-Path $root 'src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll'
    $probe = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe"
    if ($PackageRoot) {
        $hostDll = Join-Path $root 'Host/RelaxDesk.Host.exe'; $viewer = Join-Path $root 'Viewer/RelaxDesk.Viewer.exe'
        $smokeDll = Join-Path $root 'Tools/Smoke/RelaxDesk.Smoke.exe'; $tests = Join-Path $root 'Tools/Tests/RelaxDesk.Tests.exe'
        $probe = Join-Path $root 'Tools/Native/RelaxDesk.NativeProbe.exe'
    }
    Run-App $tests
    if ($LASTEXITCODE) { throw 'Core/TLS checks failed' }
    Run-App $smokeDll --windows-contracts
    if ($LASTEXITCODE) { throw 'Windows contracts failed' }
    & $probe --present-contracts
    if ($LASTEXITCODE) { throw 'Present state contracts failed' }
    & $probe --capture-contracts
    if ($LASTEXITCODE) { throw 'Capture classification/WARP contracts failed' }
    foreach ($mode in @('--reject-token', '--reject-pin', '--deny-v2', '--reject-media', '--deny-approval')) {
        Run-App $smokeDll $hostDll $mode
        if ($LASTEXITCODE) { throw "Security smoke failed: $mode" }
    }
    if ($Wpf) {
        $shell = Join-Path $root 'src/RelaxDesk.HostShell/bin/Release/net11.0-windows/RelaxDesk.HostShell.dll'
        if ($PackageRoot) { $shell = Join-Path $root 'HostUi/RelaxDesk.HostShell.exe' }
        Run-App $shell --shell-contracts $hostDll (Join-Path $root 'build/host-shell-verification')
        if ($LASTEXITCODE) { throw 'WPF Host approval/stop contracts failed' }
    }
    if ($Graphics) {
        foreach ($mode in @('--video-software', '--video-hardware', '--video-recovery', '--video-stalled', '--video-producer-failure', '--video-runtime-fallback')) {
            Run-App $smokeDll $hostDll $mode
            if ($LASTEXITCODE) { throw "Video smoke failed: $mode" }
        }
        Run-App $hostDll --codec-benchmark
        if ($LASTEXITCODE) { throw 'Codec benchmark failed' }
    }
    if ($Wpf) {
        Run-App $viewer --ui-contracts
        if ($LASTEXITCODE) { throw 'Viewer connection/preview contracts failed' }
        Run-App $viewer --hwnd-contracts
        if ($LASTEXITCODE) { throw 'WPF HWND lifecycle contracts failed' }
        & $probe --render-test
        if ($LASTEXITCODE) { throw 'Native render/pointer/resize/release test failed' }
        foreach ($mode in @('approval-cpu','hardware-cpu','cpu-idle-long','gpu','gpu-window','gpu-idle','gpu-resolution','gpu-recovery','gpu-device-fallback','gpu-fallback','gpu-close')) {
            Run-App $smokeDll $hostDll --wpf $viewer $mode
            if ($LASTEXITCODE) { throw "WPF render smoke failed: $mode" }
        }
    }
    if ($Capture) {
        Run-App $hostDll --capture-diagnose
        if ($LASTEXITCODE) { throw 'Capture diagnostics failed' }
        & $probe
        if ($LASTEXITCODE) { throw 'Real capture unavailable; inspect diagnostics and run on authorized local console' }
        Run-App $smokeDll $hostDll --wpf $viewer desktop-gpu
        if ($LASTEXITCODE) { throw 'Real desktop-to-GPU-display proof failed' }
    }
}
finally { Pop-Location }
