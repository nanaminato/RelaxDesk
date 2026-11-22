param([int]$Frames = 120, [string]$OutputDirectory = 'benchmarks/latest-renderers')
$ErrorActionPreference = 'Stop'
if ($Frames -lt 60 -or $Frames -gt 1800) { throw 'Frames must be 60..1800' }
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
[IO.Directory]::CreateDirectory($output) | Out-Null
$hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
$viewerDll = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
$smokeDll = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
foreach ($mode in @('deflate', 'software', 'hardware-cpu', 'gpu')) {
    $directory = Join-Path $output $mode
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $arguments = @(('"{0}"' -f $smokeDll), ('"{0}"' -f $hostDll), '--wpf-budget', ('"{0}"' -f $viewerDll), $mode, $Frames)
    $driver = Start-Process dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'video.txt') -RedirectStandardError (Join-Path $directory 'errors.txt')
    try {
        $children = @()
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($driver.Id)" | Where-Object { $_.CommandLine -like '*RelaxDesk.Host.dll*' -or $_.CommandLine -like '*RelaxDesk.Viewer.dll*' })
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
    finally { if (!$driver.HasExited) { $driver.Kill($true) }; $driver.Dispose() }
}
