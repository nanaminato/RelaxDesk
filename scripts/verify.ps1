param([switch]$Graphics, [switch]$Wpf, [switch]$Capture, [switch]$SkipBuild, [string]$NativeBuildDirectory = 'build/native')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
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
    dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
    if ($LASTEXITCODE) { throw 'Core/TLS checks failed' }
    dotnet $smokeDll --windows-contracts
    if ($LASTEXITCODE) { throw 'Windows contracts failed' }
    & (Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe") --present-contracts
    if ($LASTEXITCODE) { throw 'Present state contracts failed' }
    foreach ($mode in @('--reject-token', '--reject-pin', '--deny-v2', '--reject-media')) {
        dotnet $smokeDll $hostDll $mode
        if ($LASTEXITCODE) { throw "Security smoke failed: $mode" }
    }
    if ($Graphics) {
        foreach ($mode in @('--video-software', '--video-hardware', '--video-recovery', '--video-stalled')) {
            dotnet $smokeDll $hostDll $mode
            if ($LASTEXITCODE) { throw "Video smoke failed: $mode" }
        }
        dotnet $hostDll --codec-benchmark
        if ($LASTEXITCODE) { throw 'Codec benchmark failed' }
    }
    if ($Wpf) {
        dotnet (Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll') --hwnd-contracts
        if ($LASTEXITCODE) { throw 'WPF HWND lifecycle contracts failed' }
        & (Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe") --render-test
        if ($LASTEXITCODE) { throw 'Native render/pointer/resize/release test failed' }
        foreach ($mode in @('hardware-cpu','gpu','gpu-window','gpu-idle','gpu-resolution','gpu-recovery','gpu-device-fallback','gpu-fallback','gpu-close')) {
            dotnet $smokeDll $hostDll --wpf (Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll') $mode
            if ($LASTEXITCODE) { throw "WPF render smoke failed: $mode" }
        }
    }
    if ($Capture) {
        dotnet $hostDll --capture-diagnose
        if ($LASTEXITCODE) { throw 'Capture diagnostics failed' }
        & (Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe")
        if ($LASTEXITCODE) { throw 'Real capture unavailable; inspect diagnostics and run on authorized local console' }
        dotnet $smokeDll $hostDll --wpf (Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll') desktop-gpu
        if ($LASTEXITCODE) { throw 'Real desktop-to-GPU-display proof failed' }
    }
}
finally { Pop-Location }
