param([ValidateRange(60,1800)][int]$Frames = 300, [string]$OutputDirectory = 'benchmarks/latest-video', [string]$ArtifactsDirectory = '', [string]$PackageRoot = '')
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/RelaxDesk.Benchmark.psm1" -Force
if ($ArtifactsDirectory -and $PackageRoot) { throw 'Choose an artifacts directory or a package root' }
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot) }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; prior evidence is retained' }
$smoke = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
$hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
if ($ArtifactsDirectory) {
    $artifacts = [IO.Path]::GetFullPath((Join-Path $root $ArtifactsDirectory))
    $smoke = Join-Path $artifacts 'bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll'
    $hostDll = Join-Path $artifacts 'bin/RelaxDesk.Host/release/RelaxDesk.Host.dll'
}
if ($PackageRoot) { $smoke = Join-Path $root 'Tools/Smoke/RelaxDesk.Smoke.exe'; $hostDll = Join-Path $root 'Host/RelaxDesk.Host.exe' }
foreach ($application in @($smoke,$hostDll)) { if (!(Test-Path -LiteralPath $application -PathType Leaf)) { throw "Missing benchmark application: $application" } }
[IO.Directory]::CreateDirectory($output) | Out-Null
Write-BenchmarkManifest -Output $output -Frames $Frames -Modes @('hardware') -Applications @($smoke,$hostDll)
$executable = 'dotnet'; $arguments = @($smoke,$hostDll,'--video-hardware',"$Frames")
if ($PackageRoot) { $executable = $smoke; $arguments = $arguments[1..($arguments.Count-1)] }
$quoted = @($arguments | ForEach-Object { ConvertTo-BenchmarkArgument $_ })
$driver = Start-Process $executable -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'video.txt') -RedirectStandardError (Join-Path $output 'errors.txt')
$completed = $false
try {
    # Retain the process handle before exit; Windows PowerShell 5.1 otherwise
    # may expose a null ExitCode when sampling observes termination first.
    $null = $driver.Handle
    $children = @(Get-BenchmarkProcesses -Driver $driver -Log (Join-Path $output 'video.txt') -Roles @('host'))
    ConvertTo-Json -InputObject $children -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'processes.json') -Encoding utf8
    $starts = @{}; foreach ($entry in $children) { $starts[[int]$entry.ProcessId] = [long]$entry.StartedUtcTicks }
    & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($children.ProcessId) -ExpectedProcessStarts $starts -Seconds ([Math]::Max(30, $Frames / 10 + 15)) -StopWhenProcessId $driver.Id -OutputPath (Join-Path $output 'resources.json')
    if (!$driver.WaitForExit(5000)) { throw 'Benchmark did not exit within its bounded sample window' }
    if ($null -eq $driver.ExitCode -or $driver.ExitCode -ne 0) { throw "Video benchmark failed or exit code unavailable; see $output/errors.txt" }
    $completed = $true
    Write-Output "PASS synthetic loopback video evidence: $output; no physical desktop/performance acceptance"
}
finally {
    [pscustomobject]@{Status=$(if($completed){'PASS'}else{'FAIL'});ExitCode=$(if($driver.HasExited){$driver.ExitCode}else{$null})} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    if (!$driver.HasExited) { & taskkill.exe /PID $driver.Id /T /F | Out-Null }; $driver.Dispose()
}
