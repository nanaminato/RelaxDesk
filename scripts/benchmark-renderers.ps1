param([int]$Frames = 120, [string]$OutputDirectory = 'benchmarks/latest-renderers', [string]$PackageRoot = '')
$ErrorActionPreference = 'Stop'
if ($Frames -lt 60 -or $Frames -gt 1800) { throw 'Frames must be 60..1800' }
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot) }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
[IO.Directory]::CreateDirectory($output) | Out-Null
$hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
$viewerDll = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
$smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
if ($PackageRoot) {
    $hostDll = Join-Path $root 'Host/RelaxDesk.Host.exe'; $viewerDll = Join-Path $root 'Viewer/RelaxDesk.Viewer.exe'
    $smokeDll = Join-Path $root 'Tools/Smoke/RelaxDesk.Smoke.exe'
}
foreach ($mode in @('deflate', 'software', 'hardware-cpu', 'gpu')) {
    $directory = Join-Path $output $mode
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $arguments = @(('"{0}"' -f $smokeDll), ('"{0}"' -f $hostDll), '--wpf-budget', ('"{0}"' -f $viewerDll), $mode, $Frames)
    $executable = 'dotnet'
    if ($PackageRoot) { $executable = $smokeDll; $arguments = $arguments[1..($arguments.Count-1)] }
    $driver = Start-Process $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'video.txt') -RedirectStandardError (Join-Path $directory 'errors.txt')
    try {
        $children = @()
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($driver.Id)" | Where-Object { $_.CommandLine -like '*RelaxDesk.Host.dll*' -or $_.CommandLine -like '*RelaxDesk.Viewer.dll*' -or $_.ExecutablePath -eq $hostDll -or $_.ExecutablePath -eq $viewerDll })
            if ($children.Count -eq 2) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($children.Count -ne 2) { throw "Missing Host/Viewer for $mode; inspect errors.txt" }
        $children | Select-Object ProcessId, Name, CommandLine | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'processes.json') -Encoding utf8
        & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($children.ProcessId) -Seconds ([Math]::Max(10, $Frames / 10 + 10)) -StopWhenProcessId $driver.Id -OutputPath (Join-Path $directory 'resources.json')
        $driver.WaitForExit()
        if ($driver.ExitCode) { throw "Renderer benchmark failed: $mode; inspect $directory" }
        Write-Output "PASS renderer benchmark mode=$mode received_media_budget=$Frames"
    }
    finally { if (!$driver.HasExited) { & taskkill.exe /PID $driver.Id /T /F | Out-Null }; $driver.Dispose() }
}
