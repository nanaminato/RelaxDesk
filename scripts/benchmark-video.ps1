param([int]$Frames = 300, [string]$OutputDirectory = 'benchmarks/latest-video')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
[IO.Directory]::CreateDirectory($output) | Out-Null
$smoke = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
$hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
$driver = Start-Process dotnet -ArgumentList @($smoke, $hostDll, '--video-hardware', $Frames) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'video.txt') -RedirectStandardError (Join-Path $output 'errors.txt')
try {
    $hostProcessId = $null
    for ($attempt = 0; $attempt -lt 30 -and !$hostProcessId; $attempt++) {
        $hostProcessId = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($driver.Id)" | Where-Object { $_.CommandLine -like '*RelaxDesk.Host.dll*' } | Select-Object -First 1 -ExpandProperty ProcessId
        if (!$hostProcessId) { Start-Sleep -Milliseconds 100 }
    }
    if (!$hostProcessId) { throw 'Could not find benchmark child host' }
    & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($driver.Id, $hostProcessId) -Seconds ([Math]::Max(2, [Math]::Floor($Frames / 30))) -OutputPath (Join-Path $output 'resources.json')
    $driver.WaitForExit()
    if ($driver.ExitCode) { throw "Video benchmark failed; see $output/errors.txt" }
    Write-Output "Synthetic loopback video evidence: $output"
}
finally { if (!$driver.HasExited) { $driver.Kill($true) }; $driver.Dispose() }
