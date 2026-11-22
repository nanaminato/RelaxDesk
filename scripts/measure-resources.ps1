param(
    [Parameter(Mandatory)][int[]]$ProcessIds,
    [int]$Seconds = 60,
    [string]$OutputPath = 'benchmarks/resources.json',
    [int]$StopWhenProcessId = 0
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 1 -or $Seconds -gt 3600) { throw 'Seconds must be 1..3600' }
$samples = [System.Collections.Generic.List[object]]::new()
$previous = @{}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    if ($StopWhenProcessId -and !(Get-Process -Id $StopWhenProcessId -ErrorAction SilentlyContinue)) { break }
    $engines = @()
    $gpuError = $null
    try { $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine) }
    catch { $gpuError = $_.Exception.Message }
    $memory = @()
    try { $memory = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory) }
    catch { if (!$gpuError) { $gpuError = $_.Exception.Message } }
    foreach ($processId in $ProcessIds) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (!$process) { continue }
        $now = $clock.Elapsed.TotalSeconds
        $cpu = $process.TotalProcessorTime.TotalSeconds
        $percent = $null
        if ($previous.ContainsKey($processId)) {
            $before = $previous[$processId]
            $percent = 100 * ($cpu - $before.Cpu) / [Math]::Max(.001, $now - $before.Time) / [Environment]::ProcessorCount
        }
        $previous[$processId] = @{ Cpu = $cpu; Time = $now }
        $gpu = @($engines | Where-Object { $_.Name -like "pid_${processId}_*" } | ForEach-Object {
            @{ Instance = $_.Name; UtilizationPercent = $_.UtilizationPercentage }
        })
        $gpuMemory = @($memory | Where-Object { $_.Name -like "pid_${processId}_*" } | ForEach-Object { @{ Instance = $_.Name; DedicatedBytes = $_.DedicatedUsage; SharedBytes = $_.SharedUsage; TotalCommittedBytes = $_.TotalCommitted } })
        $samples.Add(@{ Utc = [DateTime]::UtcNow.ToString('O'); ProcessId = $processId; CpuPercent = $percent; WorkingSetBytes = $process.WorkingSet64; GpuEngines = $gpu; GpuMemory = $gpuMemory; GpuError = $gpuError })
    }
    Start-Sleep -Milliseconds 1000
}
$absolute = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
$samples | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $absolute -Encoding utf8
Write-Output "Resource samples saved to $absolute; GPU engine instances are separate counters, not one aggregate GPU percentage."
