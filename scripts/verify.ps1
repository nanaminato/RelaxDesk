param([switch]$Graphics, [switch]$Wpf, [switch]$Capture, [switch]$SkipBuild, [string]$NativeBuildDirectory = 'build/native', [string]$PackageRoot = '', [string]$ArtifactsDirectory = '', [switch]$SkipInteractiveInput, [switch]$SkipPhysicalGraphics)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot); $SkipBuild = $true }
function Run-App([string]$Application, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments) {
    function Quote-Native([string]$Value) { if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }; return '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
    $taskArguments = @($Arguments)
    $taskApplication = $Application
    if ([IO.Path]::GetExtension($Application) -ne '.exe') { $taskApplication = 'dotnet'; $taskArguments = @($Application) + $taskArguments }
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskApplication; $taskStart.Arguments = ($taskArguments | ForEach-Object { Quote-Native $_ }) -join ' '
    $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true; $taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
    $taskProcess = New-Object Diagnostics.Process; $taskProcess.StartInfo = $taskStart
    try {
        if (!$taskProcess.Start()) { throw "Cannot start $Application" }
        $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync(); $taskError = $taskProcess.StandardError.ReadToEndAsync()
        if (!$taskProcess.WaitForExit(180000)) {
            try { $taskProcess.Kill($true) } catch { & "$env:SystemRoot/System32/taskkill.exe" /PID $taskProcess.Id /T /F | Out-Null }
            throw "Test timed out after 180s: $Application"
        }
        $taskText = $taskOutput.GetAwaiter().GetResult(); if ($taskText) { Write-Output $taskText.TrimEnd() }
        $taskText = $taskError.GetAwaiter().GetResult(); if ($taskText) { Write-Output $taskText.TrimEnd() }
        $global:LASTEXITCODE = $taskProcess.ExitCode
    } finally { $taskProcess.Dispose() }
}
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    if (!$SkipBuild) {
        & "$PSScriptRoot/build-native.ps1" -BuildDirectory $NativeBuildDirectory
        if ($ArtifactsDirectory) { dotnet build RelaxDesk.sln -c Release --artifacts-path $ArtifactsDirectory "-p:RelaxDeskNativePath=$root/$NativeBuildDirectory/Release/RelaxDesk.Native.dll" }
        else { dotnet build RelaxDesk.sln -c Release }
        if ($LASTEXITCODE) { throw 'Managed build failed' }
    }
    $hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
    $smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
    $viewer = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
    $tests = Join-Path $root 'src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll'
    $probe = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe"
    if ($ArtifactsDirectory -and !$PackageRoot) {
        $artifacts = if ([IO.Path]::IsPathRooted($ArtifactsDirectory)) { [IO.Path]::GetFullPath($ArtifactsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $ArtifactsDirectory)) }
        $hostDll = Join-Path $artifacts 'bin/RelaxDesk.Host/release/RelaxDesk.Host.dll'
        $smokeDll = Join-Path $artifacts 'bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll'
        $viewer = Join-Path $artifacts 'bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll'
        $tests = Join-Path $artifacts 'bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll'
    }
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
        if ($ArtifactsDirectory -and !$PackageRoot) { $shell = Join-Path $artifacts 'bin/RelaxDesk.HostShell/release/RelaxDesk.HostShell.dll' }
        if ($PackageRoot) { $shell = Join-Path $root 'HostUi/RelaxDesk.HostShell.exe' }
        Run-App $shell --shell-contracts $hostDll (Join-Path $root 'build/host-shell-verification')
        if ($LASTEXITCODE) { throw 'WPF Host approval/stop contracts failed' }
    }
    if ($Graphics) {
        Run-App $smokeDll $hostDll --persistent-host (Join-Path $root 'build/persistent-host-verification')
        if ($LASTEXITCODE) { throw 'Persistent Host authorization/lifecycle failed' }
        $fileFixtures = Join-Path $root 'build/file-transfer-validation'
        Run-App $smokeDll --file-root-contracts $fileFixtures
        if ($LASTEXITCODE) { throw 'Scoped file contracts failed' }
        foreach ($mode in @('--file-transfer', '--file-denied', '--reject-data')) {
            Run-App $smokeDll $hostDll $mode $fileFixtures
            if ($LASTEXITCODE) { throw "File transfer smoke failed: $mode" }
        }
        foreach ($mode in @('--clipboard-text', '--clipboard-denied', '--clipboard-unnegotiated', '--clipboard-failure')) {
            Run-App $smokeDll $hostDll $mode
            if ($LASTEXITCODE) { throw "Clipboard smoke failed: $mode" }
        }
        foreach ($mode in @('--video-software', '--video-hardware', '--video-recovery', '--video-stalled', '--video-producer-failure', '--video-runtime-fallback', '--video-capture-recovery', '--video-capture-exhausted', '--video-control-timeout', '--video-idle-control')) {
            Run-App $smokeDll $hostDll $mode
            if ($LASTEXITCODE) { throw "Video smoke failed: $mode" }
        }
        Run-App $hostDll --codec-benchmark
        if ($LASTEXITCODE) { throw 'Codec benchmark failed' }
    }
    if ($Wpf) {
        if ($SkipInteractiveInput) { Write-Output 'SKIP keyboard foreground/interactive-input contracts (explicitly requested)' }
        else { Run-App $viewer --keyboard-contracts; if ($LASTEXITCODE) { throw 'Keyboard routing contracts failed' } }
        Run-App $viewer --file-ui-contracts $hostDll (Join-Path $root 'build/file-ui-verification')
        if ($LASTEXITCODE) { throw 'File panel/queue/drop contracts failed' }
        Run-App $viewer --file-session-contracts $hostDll (Join-Path $root 'build/file-ui-verification')
        if ($LASTEXITCODE) { throw 'Viewer file-session integration failed' }
        Run-App $viewer --toolbar-contracts
        if ($LASTEXITCODE) { throw 'Toolbar lifecycle failed' }
        Run-App $viewer --ui-contracts
        if ($LASTEXITCODE) { throw 'Viewer connection/preview contracts failed' }
        Run-App $viewer --hwnd-contracts
        if ($LASTEXITCODE) { throw 'WPF HWND lifecycle contracts failed' }
        if ($SkipPhysicalGraphics) { Write-Output 'SKIP physical/native GPU render acceptance (explicitly requested)' }
        else { Run-App $probe --render-test; if ($LASTEXITCODE) { throw 'Native render/pointer/resize/release test failed' } }
        $renderModes = @('approval-cpu','hardware-cpu','cpu-idle-long')
        if (!$SkipPhysicalGraphics) { $renderModes += @('gpu','gpu-window','gpu-idle','gpu-resolution','gpu-recovery','gpu-device-fallback','gpu-fallback','gpu-close') }
        foreach ($mode in $renderModes) {
            Run-App $smokeDll $hostDll --wpf $viewer $mode
            if ($LASTEXITCODE) { throw "WPF render smoke failed: $mode" }
        }
    }
    if ($Capture -and $SkipPhysicalGraphics) { Write-Output 'SKIP real desktop capture/physical-display proof (explicitly requested)' }
    elseif ($Capture) {
        Run-App $hostDll --capture-diagnose
        if ($LASTEXITCODE) { throw 'Capture diagnostics failed' }
        & $probe
        if ($LASTEXITCODE) { throw 'Real capture unavailable; inspect diagnostics and run on authorized local console' }
        Run-App $smokeDll $hostDll --wpf $viewer desktop-gpu
        if ($LASTEXITCODE) { throw 'Real desktop-to-GPU-display proof failed' }
    }
}
finally { Pop-Location }
