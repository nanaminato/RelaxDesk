Set-StrictMode -Version Latest

function ConvertTo-BenchmarkArgument([string]$Value) {
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Get-BenchmarkProcesses {
    param([Parameter(Mandatory)]$Driver, [Parameter(Mandatory)][string]$Log,
          [Parameter(Mandatory)][string[]]$Roles)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 5) {
        $records = @{}
        if (Test-Path -LiteralPath $Log) {
            $lines = ([string](Get-Content -LiteralPath $Log -Raw)) -split "`n"
            # A file writer can be between JSON bytes and its newline. Only
            # complete records are evidence; wait for the next poll otherwise.
            for ($lineIndex = 0; $lineIndex -lt $lines.Length - 1; $lineIndex++) {
                $line = $lines[$lineIndex].TrimEnd("`r")
                if (!$line.StartsWith('benchmark_process ')) { continue }
                $entry = $line.Substring(18) | ConvertFrom-Json
                if ($Roles -notcontains $entry.Role -or $entry.ProcessId -le 0 -or $entry.StartedUtcTicks -le 0 -or $records.ContainsKey($entry.Role)) { throw 'Invalid/duplicate benchmark process identity' }
                $records[$entry.Role] = $entry
            }
        }
        if ($records.Count -eq $Roles.Count) {
            foreach ($role in $Roles) {
                $entry = $records[$role]
                $owned = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
                if (!$owned) { throw 'Benchmark child already exited' }
                try {
                    if ($owned.StartTime.ToUniversalTime().Ticks -ne $entry.StartedUtcTicks) { throw 'Benchmark process identity changed' }
                } finally { $owned.Dispose() }
            }
            return @($Roles | ForEach-Object { $records[$_] })
        }
        if ($Driver.HasExited) { throw 'Benchmark driver exited before reporting live child identities; inspect errors.txt' }
        Start-Sleep -Milliseconds 50
    }
    throw 'Benchmark child identity deadline exceeded; inspect errors.txt'
}

function Write-BenchmarkManifest {
    param([string]$Output, [int]$Frames, [string[]]$Modes, [string[]]$Applications)
    $graphics = @(); $graphicsError = $null
    try { $graphics = @(Get-CimInstance Win32_VideoController -OperationTimeoutSec 2 | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate) }
    catch { $graphicsError = $_.Exception.Message }
    $revision = $null
    try { $revision = (& git -C $PSScriptRoot rev-parse HEAD 2>$null).Trim() } catch { }
    $powerScheme = $null
    try { $powerScheme = (& "$env:SystemRoot/System32/powercfg.exe" /GetActiveScheme 2>$null | Out-String).Trim() } catch { }
    $edition = $null
    try { $edition = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop).EditionID } catch { }
    $binaries = @($Applications)
    foreach ($application in $Applications) {
        $binaries += @(Get-ChildItem -LiteralPath (Split-Path $application) -Filter 'RelaxDesk.*.dll' -File | Select-Object -ExpandProperty FullName)
    }
    $binaries = @($binaries | Select-Object -Unique)
    $manifest = [ordered]@{
        SchemaVersion = 1; CollectedUtc = [DateTime]::UtcNow.ToString('O'); Revision = $revision
        Source = 'synthetic-loopback'; PhysicalAcceptance = 'NOT_TESTED'
        Frames = $Frames; Modes = $Modes; OsVersion = [Environment]::OSVersion.VersionString; WindowsEdition = $edition
        PowerScheme = $powerScheme; LogicalProcessors = [Environment]::ProcessorCount
        Graphics = $graphics; GraphicsQueryError = $graphicsError
        Applications = @(Get-FileHash -LiteralPath $binaries -Algorithm SHA256 | Select-Object Path,Hash)
        CpuUnits = 'percent of all logical processors'; GpuUnits = 'separate driver engine labels; no aggregate'
    }
    ConvertTo-Json -InputObject $manifest -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'run.json') -Encoding utf8
}

Export-ModuleMember -Function ConvertTo-BenchmarkArgument,Get-BenchmarkProcesses,Write-BenchmarkManifest
