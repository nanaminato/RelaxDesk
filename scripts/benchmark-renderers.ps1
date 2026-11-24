param([int]$Frames = 120, [string]$OutputDirectory = 'benchmarks/latest-renderers', [string]$PackageRoot = '', [string]$ArtifactsDirectory = '',
      [ValidateSet('deflate','software','hardware-cpu','gpu','desktop-gpu-udp')][string[]]$Modes = @('deflate','software','hardware-cpu','gpu'))
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/RelaxDesk.Benchmark.psm1" -Force
if ($ArtifactsDirectory -and $PackageRoot) { throw 'Choose an artifacts directory or a package root' }
if (!$Modes.Count -or @($Modes | Select-Object -Unique).Count -ne $Modes.Count) { throw 'Choose distinct renderer modes' }
if ($Frames -lt 60 -or $Frames -gt 1800) { throw 'Frames must be 60..1800' }
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot) }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; prior evidence is retained' }
$hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
$viewerDll = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
$smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
if ($ArtifactsDirectory) {
    $artifacts = [IO.Path]::GetFullPath((Join-Path $root $ArtifactsDirectory))
    $hostDll = Join-Path $artifacts 'bin/RelaxDesk.Host/release/RelaxDesk.Host.dll'
    $viewerDll = Join-Path $artifacts 'bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll'
    $smokeDll = Join-Path $artifacts 'bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll'
}
if ($PackageRoot) {
    $hostDll = Join-Path $root 'Host/RelaxDesk.Host.exe'; $viewerDll = Join-Path $root 'Viewer/RelaxDesk.Viewer.exe'
    $smokeDll = Join-Path $root 'Tools/Smoke/RelaxDesk.Smoke.exe'
}
foreach ($application in @($smokeDll,$hostDll,$viewerDll)) { if (!(Test-Path -LiteralPath $application -PathType Leaf)) { throw "Missing benchmark application: $application" } }
[IO.Directory]::CreateDirectory($output) | Out-Null
Write-BenchmarkManifest -Output $output -Frames $Frames -Modes $Modes -Applications @($smokeDll,$hostDll,$viewerDll)
foreach ($mode in $Modes) {
    $directory = Join-Path $output $mode
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $desktopSample = $mode -eq 'desktop-gpu-udp'
    $arguments = if ($desktopSample) { @($smokeDll,$hostDll,'--wpf',$viewerDll,$mode) }
        else { @($smokeDll,$hostDll,'--wpf-budget',$viewerDll,$mode,"$Frames") }
    $executable = 'dotnet'
    if ($PackageRoot) { $executable = $smokeDll; $arguments = $arguments[1..($arguments.Count-1)] }
    $quoted = @($arguments | ForEach-Object { ConvertTo-BenchmarkArgument $_ })
    $driver = Start-Process $executable -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'video.txt') -RedirectStandardError (Join-Path $directory 'errors.txt')
    $completed = $false
    try {
        $null = $driver.Handle
        $children = @(Get-BenchmarkProcesses -Driver $driver -Log (Join-Path $directory 'video.txt') -Roles @('host','viewer'))
        ConvertTo-Json -InputObject $children -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'processes.json') -Encoding utf8
        $starts = @{}; foreach ($entry in $children) { $starts[[int]$entry.ProcessId] = [long]$entry.StartedUtcTicks }
        & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($children.ProcessId) -ExpectedProcessStarts $starts -Seconds ([Math]::Max(30, $Frames / 10 + 15)) -StopWhenProcessId $driver.Id -OutputPath (Join-Path $directory 'resources.json')
        if (!$driver.WaitForExit(5000)) { throw 'Benchmark did not exit within its bounded sample window' }
        if ($null -eq $driver.ExitCode -or $driver.ExitCode -ne 0) { throw "Renderer benchmark failed or exit code unavailable: $mode; inspect $directory" }
        $completed = $true
        if ($desktopSample) { Write-Output "PASS renderer benchmark mode=$mode timed_desktop_sample=true" }
        else { Write-Output "PASS renderer benchmark mode=$mode received_media_budget=$Frames" }
    }
    finally {
        [pscustomobject]@{Status=$(if($completed){'PASS'}else{'FAIL'});ExitCode=$(if($driver.HasExited){$driver.ExitCode}else{$null})} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'result.json') -Encoding utf8
        if (!$driver.HasExited) { & taskkill.exe /PID $driver.Id /T /F | Out-Null }; $driver.Dispose()
    }
}
