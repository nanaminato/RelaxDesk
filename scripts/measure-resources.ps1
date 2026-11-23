param(
    [Parameter(Mandatory)][int[]]$ProcessIds,
    [int]$Seconds = 60,
    [string]$OutputPath = 'benchmarks/resources.json',
    [int]$StopWhenProcessId = 0,
    [hashtable]$ExpectedProcessStarts = @{}
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 1 -or $Seconds -gt 3600) { throw 'Seconds must be 1..3600' }
if (!$ProcessIds.Count -or $ProcessIds.Count -gt 16 -or @($ProcessIds | Where-Object { $_ -le 0 }).Count -or @($ProcessIds | Select-Object -Unique).Count -ne $ProcessIds.Count) { throw 'Choose 1..16 distinct positive process IDs' }
$samples = [System.Collections.Generic.List[object]]::new()
$previous = @{}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    if ($StopWhenProcessId) {
        $stopProcess = Get-Process -Id $StopWhenProcessId -ErrorAction SilentlyContinue
        if (!$stopProcess) { break }
        $stopProcess.Dispose()
    }
    $engines = @()
    $gpuError = $null
    try { $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -OperationTimeoutSec 2) }
    catch { $gpuError = $_.Exception.Message }
    $memory = @()
    try { $memory = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -OperationTimeoutSec 2) }
    catch { if (!$gpuError) { $gpuError = $_.Exception.Message } }
    foreach ($processId in $ProcessIds) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (!$process) { continue }
        try {
            $started = $process.StartTime.ToUniversalTime().Ticks
            if ($ExpectedProcessStarts.ContainsKey($processId) -and $ExpectedProcessStarts[$processId] -ne $started) { continue }
            $now = $clock.Elapsed.TotalSeconds
            $cpu = $process.TotalProcessorTime.TotalSeconds
            $workingSet = $process.WorkingSet64
        } catch { continue } finally { $process.Dispose() }
        $percent = $null
        if ($previous.ContainsKey($processId)) {
            $before = $previous[$processId]
            if ($before.Started -eq $started -and $cpu -ge $before.Cpu -and $now -gt $before.Time) {
                $percent = 100 * ($cpu - $before.Cpu) / ($now - $before.Time) / [Environment]::ProcessorCount
            }
        }
        $previous[$processId] = @{ Cpu = $cpu; Time = $now; Started = $started }
        $gpu = @($engines | Where-Object { $_.Name -like "pid_${processId}_*" } | ForEach-Object {
            @{ Instance = $_.Name; UtilizationPercent = $_.UtilizationPercentage }
        })
        $gpuMemory = @($memory | Where-Object { $_.Name -like "pid_${processId}_*" } | ForEach-Object { @{ Instance = $_.Name; DedicatedBytes = $_.DedicatedUsage; SharedBytes = $_.SharedUsage; TotalCommittedBytes = $_.TotalCommitted } })
        $samples.Add(@{ Utc = [DateTime]::UtcNow.ToString('O'); ProcessId = $processId; StartedUtcTicks = $started; CpuPercent = $percent; WorkingSetBytes = $workingSet; GpuEngines = $gpu; GpuMemory = $gpuMemory; GpuError = $gpuError })
    }
    Start-Sleep -Milliseconds 1000
}
$absolute = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
ConvertTo-Json -InputObject @($samples.ToArray()) -Depth 6 | Set-Content -LiteralPath $absolute -Encoding utf8
Write-Output "Resource samples saved to $absolute; GPU engine instances are separate counters, not one aggregate GPU percentage."
