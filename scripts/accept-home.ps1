param(
    [string]$OutputDirectory = '',
    [string]$NativeBuildDirectory = 'build/native-home',
    [switch]$SkipBuild,
    [string]$PackageRoot = '',
    [switch]$DiagnosticOnly,
    [switch]$SelfTest,
    [ValidateRange(60,1800)][int]$Frames = 300
)
$ErrorActionPreference = 'Stop'
function Get-AcceptanceStatus([bool]$Eligible, [object[]]$Steps) {
    if (@($Steps | Where-Object { $_.status -eq 'FAIL' }).Count) { return 'FAIL' }
    if (!$Eligible) { return 'NOT_ACCEPTED_ENVIRONMENT' }
    if (@($Steps | Where-Object { $_.status -eq 'BLOCKED' }).Count) { return 'BLOCKED' }
    if (!$Steps.Count) { return 'INCOMPLETE' }
    return 'PASS_LOCAL_AUTOMATION_ONLY'
}
function Quote-Argument([string]$Value) {
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Test-HomeConsole([string]$Edition, $Environment, [bool]$Diagnostics) {
    return !$Diagnostics -and $Edition -like 'Core*' -and $Environment.environment -eq 'local-console' -and $Environment.session_id -eq $Environment.active_console_session -and $Environment.station_visible -and $Environment.window_station -eq 'WinSta0' -and $Environment.input_desktop -eq 'Default' -and $Environment.thread_desktop -eq 'Default' -and $Environment.input_desktop_error -eq 0 -and $Environment.connection_state -eq 0
}
if ($SelfTest) {
    $cases = @(
        @($true, @([pscustomobject]@{status='PASS'}), 'PASS_LOCAL_AUTOMATION_ONLY'),
        @($false, @([pscustomobject]@{status='PASS'}), 'NOT_ACCEPTED_ENVIRONMENT'),
        @($true, @([pscustomobject]@{status='BLOCKED'}), 'BLOCKED'),
        @($true, @([pscustomobject]@{status='FAIL'}), 'FAIL'),
        @($true, @(), 'INCOMPLETE')
    )
    foreach ($case in $cases) { if ((Get-AcceptanceStatus $case[0] $case[1]) -ne $case[2]) { throw 'Acceptance reducer contract' } }
    if ((Quote-Argument 'C:\with space\') -ne '"C:\with space\\"') { throw 'Windows argument quoting contract' }
    $environment=[pscustomobject]@{environment='local-console';session_id=1;active_console_session=1;station_visible=$true;window_station='WinSta0';input_desktop='Default';thread_desktop='Default';input_desktop_error=0;connection_state=0}
    if (!(Test-HomeConsole 'Core' $environment $false)) { throw 'Home local-console eligibility contract' }
    if (Test-HomeConsole 'ServerStandard' $environment $false) { throw 'Server must not count as Home' }
    if (Test-HomeConsole 'Core' $environment $true) { throw 'Diagnostics must not claim acceptance' }
    $environment.environment='rdp';if (Test-HomeConsole 'Core' $environment $false) { throw 'RDP must not count as console' }
    $environment.environment='local-console';$environment.input_desktop_error=5;if (Test-HomeConsole 'Core' $environment $false) { throw 'Denied desktop must not pass' }
    Write-Output 'PASS 11 acceptance eligibility/classification/argument contracts; no hardware exercised'
    exit 0
}
$root = Split-Path $PSScriptRoot -Parent
if ($PackageRoot) { $root = [IO.Path]::GetFullPath($PackageRoot); $SkipBuild = $true }
if (!$OutputDirectory) { $OutputDirectory = $(if ($PackageRoot) { 'logs/home-' } else { 'benchmarks/home-' }) + (Get-Date -Format 'yyyyMMdd-HHmmss') }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; prior evidence will not be overwritten.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$steps = New-Object 'System.Collections.Generic.List[object]'
$eligible = $false
$environmentStop = $false
function Run-Step([string]$Name, [string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 120, [int[]]$BlockedCodes = @()) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $File; $info.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $info.WorkingDirectory = $root; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew(); $process = New-Object Diagnostics.Process; $process.StartInfo = $info
    $code = -1; $state = 'FAIL'; $errorText = ''
    try {
        if (!$process.Start()) { throw 'Launch failed' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            # Kill only the process tree launched by this step, never a global dotnet name.
            & taskkill.exe /PID $process.Id /T /F | Out-Null
            if (!$process.WaitForExit(5000)) { throw 'Timed-out child tree could not be terminated within 5s.' }
            $errorText = "Timed out after ${TimeoutSeconds}s."
        } else { $code = $process.ExitCode }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "$Name.stdout.txt") -Encoding utf8
        ($stderr.GetAwaiter().GetResult() + $errorText) | Set-Content -LiteralPath (Join-Path $output "$Name.stderr.txt") -Encoding utf8
        if ($code -eq 0) { $state='PASS' } elseif ($BlockedCodes -contains $code) { $state='BLOCKED' }
    } catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $output "$Name.stderr.txt") -Encoding utf8 }
    finally { $process.Dispose() }
    $steps.Add([pscustomobject]@{name=$Name;status=$state;exit_code=$code;elapsed_seconds=$watch.Elapsed.TotalSeconds})
    Write-Host "$Name : $state ($code)"
    return $state
}
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $shell = (Get-Process -Id $PID).Path
    $os = Get-CimInstance Win32_OperatingSystem
    $gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,PNPDeviceID,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
    $powerScheme = $null
    try { $powerScheme = (& "$env:SystemRoot/System32/powercfg.exe" /GetActiveScheme 2>$null | Out-String).Trim() } catch { }
    $revision = if ($PackageRoot) { (Get-Content -Raw (Join-Path $root 'package.json') | ConvertFrom-Json).revision } else { & git rev-parse HEAD }
    [pscustomobject]@{collected_at=(Get-Date -Format o);revision=$revision;os=$os.Caption;version=$os.Version;build=$os.BuildNumber;power_scheme=$powerScheme;gpu=$gpu;diagnostic_only=[bool]$DiagnosticOnly;manual_physical_scanout='NOT_TESTED';manual_input='NOT_TESTED';dual_pc_lan='NOT_TESTED'} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'machine.json') -Encoding utf8
    if (!$PackageRoot) { $null = Run-Step 'sdk' 'dotnet' @('--info') }
    if (!$SkipBuild) {
        if ((Run-Step 'native-build' $shell @('-NoProfile','-File',"$PSScriptRoot/build-native.ps1",'-BuildDirectory',$NativeBuildDirectory) 300) -ne 'PASS') { throw 'Native build failed; dependent tests not run.' }
        $native = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.Native.dll"
        if ((Run-Step 'managed-build' 'dotnet' @('build','RelaxDesk.sln','-c','Release',"-p:RelaxDeskNativePath=$native") 300) -ne 'PASS') { throw 'Managed build failed; dependent tests not run.' }
    }
    $probe = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe"
    $hostDll = Join-Path $root 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
    $smoke = Join-Path $root 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
    $viewer = Join-Path $root 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
    if ($PackageRoot) {
        $probe = Join-Path $root 'Tools/Native/RelaxDesk.NativeProbe.exe'; $hostDll = Join-Path $root 'Host/RelaxDesk.Host.exe'
        $viewer = Join-Path $root 'Viewer/RelaxDesk.Viewer.exe'; $smoke = Join-Path $root 'Tools/Smoke/RelaxDesk.Smoke.exe'
        Copy-Item -LiteralPath (Join-Path $root 'package.json') -Destination (Join-Path $output 'package.json')
        Copy-Item -LiteralPath (Join-Path $root 'SHA256SUMS.json') -Destination (Join-Path $output 'package-hashes.json')
    }
    $managedLauncher = if ($PackageRoot) { $hostDll } else { 'dotnet' }
    $diagnosticArguments = if ($PackageRoot) { @('--capture-diagnose') } else { @($hostDll,'--capture-diagnose') }
    $packageArguments = if ($PackageRoot) { @('-PackageRoot',$root) } else { @() }
    $hashes = @(Get-FileHash -Algorithm SHA256 -LiteralPath $probe,(Join-Path (Split-Path $probe) 'RelaxDesk.Native.dll'),(Join-Path (Split-Path $viewer) 'RelaxDesk.Native.dll'),(Join-Path (Split-Path $hostDll) 'RelaxDesk.Native.dll'))
    $hashes | ConvertTo-Json | Set-Content (Join-Path $output 'binary-hashes.json') -Encoding utf8
    if (@($hashes | Select-Object -Skip 1 | Select-Object -ExpandProperty Hash -Unique).Count -ne 1) { throw 'Native Probe/Host/Viewer DLL hashes differ; rebuild with explicit RelaxDeskNativePath.' }
    $null = Run-Step 'present-contracts' $probe @('--present-contracts')
    if ((Run-Step 'capture-diagnostics' $managedLauncher $diagnosticArguments) -ne 'PASS') { throw 'Cannot establish session environment.' }
    $environment = Get-Content -Raw (Join-Path $output 'capture-diagnostics.stdout.txt') | ConvertFrom-Json
    # Core/Home/CoreSingleLanguage/Home China. Caption is localized: use registry EditionID.
    $edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
    $eligible = Test-HomeConsole $edition $environment ([bool]$DiagnosticOnly)
    [pscustomobject]@{edition=$edition;eligible=$eligible;environment=$environment} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'eligibility.json') -Encoding utf8
    $null = Run-Step 'security-and-contracts' $shell (@('-NoProfile','-File',"$PSScriptRoot/verify.ps1",'-SkipBuild','-NativeBuildDirectory',$NativeBuildDirectory) + $packageArguments)
    if ($PackageRoot) {
        if ((Run-Step 'deployment' $hostDll @('--deployment-info')) -ne 'PASS') { throw 'Packaged runtime launch failed' }
        $deployment = Get-Content -Raw (Join-Path $output 'deployment.stdout.txt') | ConvertFrom-Json
        if (!$deployment.x64 -or !([IO.Path]::GetFullPath($deployment.runtime)).StartsWith((Join-Path $root 'Host')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Host did not load the packaged x64 runtime' }
        $null = Run-Step 'wpf-hwnd-contracts' $viewer @('--hwnd-contracts') 30
    }
    else { $null = Run-Step 'wpf-hwnd-contracts' 'dotnet' @($viewer,'--hwnd-contracts') 30 }
    if (!$eligible -and !$DiagnosticOnly) { $environmentStop=$true;throw 'Not an unlocked Home local console. Logs collected; run -DiagnosticOnly for a bounded presentation comparison without claiming acceptance.' }
    $comparison = Run-Step 'present-comparison' $probe @('--present-diagnose') 45 @(2)
    if ($DiagnosticOnly) { Write-Host 'Diagnostic-only run: Home acceptance and full graphics suite intentionally not claimed.' }
    elseif ($comparison -ne 'PASS') { $environmentStop=$true;throw 'Independent presentation blocked/failed; no repeated WPF/benchmark attempts.' }
    else {
        if ((Run-Step 'graphics-wpf-capture' $shell (@('-NoProfile','-File',"$PSScriptRoot/verify.ps1",'-SkipBuild','-Graphics','-Wpf','-Capture','-NativeBuildDirectory',$NativeBuildDirectory) + $packageArguments) 1800) -eq 'PASS') {
            $null = Run-Step 'renderer-benchmark' $shell (@('-NoProfile','-File',"$PSScriptRoot/benchmark-renderers.ps1",'-Frames',"$Frames",'-OutputDirectory',"$OutputDirectory/renderers") + $packageArguments) 600
            # Summaries are optional when Python is unavailable; raw logs are always retained.
            if (Get-Command python -ErrorAction SilentlyContinue) { $null = Run-Step 'renderer-summary' 'python' @("$PSScriptRoot/summarize-renderers.py",(Join-Path $output 'renderers')) }
        }
    }
} catch {
    $_ | Out-String | Set-Content (Join-Path $output 'stop-reason.txt') -Encoding utf8
    # Ineligible environments are not a passing Home run, nor a code-failure assertion.
    if (!$environmentStop) { $steps.Add([pscustomobject]@{name='acceptance-stopped';status='FAIL';exit_code=-1}) }
} finally {
    $status = Get-AcceptanceStatus $eligible @($steps.ToArray())
    [pscustomobject]@{status=$status;home_console_eligible=$eligible;steps=$steps.ToArray();physical_scanout='NOT_TESTED';input_to_photon='NOT_TESTED';dual_pc_lan='NOT_TESTED'} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'result.json') -Encoding utf8
    if ($PackageRoot) { Compress-Archive -LiteralPath $output -DestinationPath ($output + '.zip') -CompressionLevel Optimal }
    Pop-Location
}
Write-Host "Result: $status; logs: $output"
if ($status -eq 'PASS_LOCAL_AUTOMATION_ONLY') { exit 0 }
exit 2
