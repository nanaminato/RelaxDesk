param([switch]$Graphics, [switch]$Wpf, [string]$NativeBuildDirectory = 'build/native')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    & "$PSScriptRoot/build-native.ps1" -BuildDirectory $NativeBuildDirectory
    dotnet build RelaxDesk.sln -c Release
    if ($LASTEXITCODE) { throw 'Managed build failed' }
    $hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
    $smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
    dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
    if ($LASTEXITCODE) { throw 'Core/TLS checks failed' }
    dotnet $smokeDll --windows-contracts
    if ($LASTEXITCODE) { throw 'Windows contracts failed' }
    foreach ($mode in @('--reject-token', '--reject-pin', '--deny-v2', '--reject-media')) {
        dotnet $smokeDll $hostDll $mode
        if ($LASTEXITCODE) { throw "Security smoke failed: $mode" }
    }
    if ($Graphics) {
        foreach ($mode in @('--video-software', '--video-hardware', '--video-recovery')) {
            dotnet $smokeDll $hostDll $mode
            if ($LASTEXITCODE) { throw "Video smoke failed: $mode" }
        }
        dotnet $hostDll --codec-benchmark
        if ($LASTEXITCODE) { throw 'Codec benchmark failed' }
    }
    if ($Wpf) {
        dotnet $smokeDll $hostDll --wpf (Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll')
        if ($LASTEXITCODE) { throw 'WPF render smoke failed' }
    }
}
finally { Pop-Location }
