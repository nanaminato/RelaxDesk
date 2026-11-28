param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/RelaxDesk.Benchmark.psm1" -Force
if (!$OutputDirectory) { $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ('build/benchmark-tools-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh fixture directory' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$checks = 0
function Check([bool]$Value,[string]$Message) { if (!$Value) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Message) { $denied=$false; try { & $Action | Out-Null } catch { $denied=$true }; Check $denied $Message }
Check ((ConvertTo-BenchmarkArgument 'C:\with space\') -eq '"C:\with space\\"') 'Trailing slash argument quoting'
$self = Get-Process -Id $PID
try {
    $entry = [pscustomobject]@{Role='host';ProcessId=$PID;StartedUtcTicks=$self.StartTime.ToUniversalTime().Ticks}
    $log = Join-Path $OutputDirectory 'driver.txt'
    $record = 'benchmark_process ' + ($entry | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText($log,$record + "`n" + 'benchmark_process {incomplete')
    $observed = @(Get-BenchmarkProcesses -Driver $self -Log $log -Roles @('host'))
    Check ($observed.Count -eq 1 -and $observed[0].ProcessId -eq $PID) 'Live identity/partial-line handling'
    [IO.File]::WriteAllText($log,$record + "`n" + $record + "`n")
    Reject { Get-BenchmarkProcesses -Driver $self -Log $log -Roles @('host') } 'Duplicate role accepted'
    $entry.StartedUtcTicks--
    [IO.File]::WriteAllText($log,'benchmark_process ' + ($entry | ConvertTo-Json -Compress) + "`n")
    Reject { Get-BenchmarkProcesses -Driver $self -Log $log -Roles @('host') } 'Reused process identity accepted'
    [IO.File]::WriteAllText($log,"benchmark_process {broken}`n")
    Reject { Get-BenchmarkProcesses -Driver $self -Log $log -Roles @('host') } 'Malformed complete identity accepted'
    $empty = Join-Path $OutputDirectory 'reused.json'
    $starts=@{}; $starts[$PID]=$entry.StartedUtcTicks
    & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($PID) -ExpectedProcessStarts $starts -Seconds 1 -OutputPath $empty
    Check ((Get-Content -LiteralPath $empty -Raw) -match '^\s*\[\s*\]\s*$') 'Mismatched identity produced samples/invalid empty JSON'
    $one = Join-Path $OutputDirectory 'one.json'
    $starts[$PID]=$self.StartTime.ToUniversalTime().Ticks
    & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($PID) -ExpectedProcessStarts $starts -Seconds 1 -OutputPath $one
    $text=Get-Content -LiteralPath $one -Raw; $rows=@($text | ConvertFrom-Json)
    Check ($text.TrimStart().StartsWith('[') -and $rows.Count -eq 1) 'Single sample is not an array'
    Check ($null -eq $rows[0].CpuPercent -and $rows[0].WorkingSetBytes -gt 0 -and $rows[0].StartedUtcTicks -eq $starts[$PID]) 'First CPU sample/working set/identity contract'
    Reject { & "$PSScriptRoot/measure-resources.ps1" -ProcessIds @($PID,$PID) -Seconds 1 -OutputPath $one } 'Duplicate PID accepted'
} finally { $self.Dispose() }
Write-Output "PASS $checks benchmark identity/quoting/actual resource sampling contracts; no desktop or input exercised; fixtures=$OutputDirectory"
