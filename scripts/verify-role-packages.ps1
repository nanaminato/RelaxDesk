param([Parameter(Mandatory)][string]$HostArchive,[Parameter(Mandatory)][string]$ViewerArchive,[Parameter(Mandatory)][string]$SmokeApplication,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith((Join-Path $repository 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh verification directory under repository/build'}
[void][IO.Directory]::CreateDirectory($output)
$steps=New-Object 'Collections.Generic.List[object]'
$checks=0;$status='FAIL';$details=$null
function Confirm([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Invoke-App([string]$Name,[string]$Application,[string[]]$Arguments,[int]$Seconds=90){
    $applicationPath=[IO.Path]::GetFullPath($Application)
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=if($applicationPath.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)){'dotnet'}else{$applicationPath}
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    if($info.FileName -eq 'dotnet'){$info.ArgumentList.Add($applicationPath)}
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info;$timer=[Diagnostics.Stopwatch]::StartNew()
    try {
        if(!$process.Start()){throw 'Verification process failed to start'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit($Seconds*1000)){$process.Kill($true);$process.WaitForExit();throw "Verification timed out: $Name"}
        [IO.File]::WriteAllText((Join-Path $output ($Name+'.stdout.txt')),$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $output ($Name+'.stderr.txt')),$stderr.GetAwaiter().GetResult())
        $steps.Add([pscustomobject]@{name=$Name;exit_code=$process.ExitCode;seconds=$timer.Elapsed.TotalSeconds})
        if($process.ExitCode){throw "Verification failed: $Name (exit $($process.ExitCode))"}
        Write-Host "$Name : PASS"
        return $stdout.GetAwaiter().GetResult()
    } finally {$process.Dispose()}
}
try {
    $archives=@([IO.Path]::GetFullPath($HostArchive),[IO.Path]::GetFullPath($ViewerArchive))
    $packages=@()
    foreach($archive in $archives){
        $name=[IO.Path]::GetFileNameWithoutExtension($archive);$destination=Join-Path $output ($name+'-extracted')
        $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        $expected=(Get-Content -LiteralPath ($archive+'.sha256') -Raw).Split(' ')[0].Trim()
        Confirm ($hash -ceq $expected) 'ZIP hash sidecar mismatch'
        Expand-Archive -LiteralPath $archive -DestinationPath $destination
        $package=Join-Path $destination $name
        $metadata=Get-Content -LiteralPath (Join-Path $package 'package.json') -Raw | ConvertFrom-Json
        Confirm ($metadata.role -in @('host','viewer')) 'Unknown role metadata'
        $manifest=@(Get-Content -LiteralPath (Join-Path $package 'SHA256SUMS.json') -Raw | ConvertFrom-Json)
        Confirm (@(Get-ChildItem -LiteralPath $package -Recurse -File).Count -eq $manifest.Count+1) 'Unlisted package files'
        Confirm ($metadata.self_contained -and !$metadata.working_tree_dirty -and $metadata.rid -eq 'win-x64') 'Unexpected package provenance/runtime'
        Confirm (Test-Path -LiteralPath (Join-Path $package $metadata.entry) -PathType Leaf) 'Role entry missing'
        $null=Invoke-App ($metadata.role+'-manifest') $SmokeApplication @('--collect-package',$package,'--verify-only')
        $packages+= [pscustomobject]@{root=$package;metadata=$metadata;archive=$archive;sha256=$hash;bytes=(Get-Item -LiteralPath $archive).Length;manifest_files=$manifest.Count}
    }
    $hostPackage=$packages[0];$viewerPackage=$packages[1]
    Confirm ($hostPackage.metadata.role -eq 'host' -and $viewerPackage.metadata.role -eq 'viewer') 'Wrong role assignments'
    Confirm ($hostPackage.metadata.version -ceq $viewerPackage.metadata.version -and $hostPackage.metadata.revision -ceq $viewerPackage.metadata.revision) 'Role build mismatch'
    Confirm (!(Test-Path (Join-Path $hostPackage.root 'Viewer')) -and !(Test-Path (Join-Path $viewerPackage.root 'Host')) -and !(Test-Path (Join-Path $viewerPackage.root 'HostUi')) -and !(Test-Path (Join-Path $viewerPackage.root 'Service'))) 'Cross-role payload found'
    Confirm (@($hostPackage.metadata.components).Count -eq 3 -and @($viewerPackage.metadata.components).Count -eq 1) 'Unexpected component count'
    $hostExe=Join-Path $hostPackage.root 'Host/RelaxDesk.Host.exe';$shellExe=Join-Path $hostPackage.root 'HostUi/RelaxDesk.HostShell.exe';$serviceExe=Join-Path $hostPackage.root 'Service/RelaxDesk.Service.exe';$viewerExe=Join-Path $viewerPackage.root 'Viewer/RelaxDesk.Viewer.exe'
    $deployment=Invoke-App 'host-runtime' $hostExe @('--deployment-info') | ConvertFrom-Json
    Confirm ($deployment.x64 -and $deployment.runtime.StartsWith((Join-Path $hostPackage.root 'Host')+'\',[StringComparison]::OrdinalIgnoreCase) -and $deployment.version -ceq $hostPackage.metadata.version) 'Host did not use its matching bundled x64 runtime'
    Confirm ((Invoke-App 'host-ui-version' $shellExe @('--version')).Trim() -ceq $hostPackage.metadata.version) 'Host UI version mismatch'
    Confirm ((Invoke-App 'viewer-version' $viewerExe @('--version')).Trim() -ceq $viewerPackage.metadata.version) 'Viewer version mismatch'
    Confirm ((Invoke-App 'service-version' $serviceExe @('--version')).Trim() -ceq ('v'+$hostPackage.metadata.version)) 'Service version mismatch'
    $native=@((Join-Path $hostPackage.root 'Host/RelaxDesk.Native.dll'),(Join-Path $hostPackage.root 'HostUi/RelaxDesk.Native.dll'),(Join-Path $viewerPackage.root 'Viewer/RelaxDesk.Native.dll')) | ForEach-Object {(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    Confirm (@($native | Select-Object -Unique).Count -eq 1) 'Native DLL mismatch'
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();$owner=$identity.User.Value;$identity.Dispose()
    $preview=Invoke-App 'host-service-readonly-preflight' $serviceExe @('--install-service','--package',$hostPackage.root,'--owner-sid',$owner,'--what-if') | ConvertFrom-Json
    Confirm (!$preview.Changed -and $preview.Version -ceq $hostPackage.metadata.version) 'Service package readonly preview failed'
    $ui=Invoke-App 'host-one-click-ui' $shellExe @('--service-setup-ui-contracts',$hostExe,(Join-Path $output 'ui-fixtures'))
    Confirm ($ui.Contains('PASS 54 actual WPF')) 'One-click UI acceptance marker missing'
    foreach($mode in @('deflate','approval-fallback-cpu')){
        $session=Invoke-App ('cross-role-'+$mode) $SmokeApplication @($hostExe,'--wpf',$viewerExe,$mode)
        Confirm ($session.Contains('PASS WPF') -and $session.Contains('native_live_resources=0')) 'Cross-role synthetic session/cleanup evidence missing'
    }
    $details=[pscustomobject]@{version=$hostPackage.metadata.version;revision=$hostPackage.metadata.revision;native_sha256=$native[0];packages=@($packages | ForEach-Object {[pscustomobject]@{role=$_.metadata.role;archive=$_.archive;sha256=$_.sha256;bytes=$_.bytes;manifest_files=$_.manifest_files}})}
    $status='PASS_SEPARATE_PACKAGES_SYNTHETIC_ONLY'
} finally {
    [pscustomobject]@{status=$status;assertions=$checks;details=$details;steps=$steps.ToArray();home_dual_pc='NOT_TESTED';uac_and_scm_mutation='NOT_TESTED';physical_capture_input_audio='NOT_TESTED'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
}
Write-Host "PASS separate packages: $checks assertions; result=$output/result.json"
