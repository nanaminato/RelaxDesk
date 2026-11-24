param([Parameter(Mandatory)][string]$HostArchive,[Parameter(Mandatory)][string]$ViewerArchive,[Parameter(Mandatory)][string]$SmokeApplication,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$SkipMachineServicePreview)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith((Join-Path $repository 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh verification directory under repository/build'}
[void][IO.Directory]::CreateDirectory($output)
$steps=New-Object 'Collections.Generic.List[object]'
$checks=0;$status='FAIL';$details=$null
function Confirm([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Invoke-App([string]$Name,[string]$Application,[string[]]$Arguments,[int]$Seconds=90,[int]$ExpectedExitCode=0){
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
        if($process.ExitCode -ne $ExpectedExitCode){throw "Verification failed: $Name (exit $($process.ExitCode), expected $ExpectedExitCode)"}
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
        Confirm ($metadata.version -and $metadata.file_version -and $metadata.revision -cmatch '^[0-9a-f]{40}([0-9a-f]{24})?$') 'Missing or invalid role build provenance'
        Confirm (Test-Path -LiteralPath (Join-Path $package $metadata.entry) -PathType Leaf) 'Role entry missing'
        $packages+= [pscustomobject]@{root=$package;metadata=$metadata;archive=$archive;sha256=$hash;bytes=(Get-Item -LiteralPath $archive).Length;manifest_files=$manifest.Count}
    }
    $hostPackage=$packages[0];$viewerPackage=$packages[1]
    Confirm ($hostPackage.metadata.role -eq 'host' -and $viewerPackage.metadata.role -eq 'viewer') 'Wrong role assignments'
    Confirm ($hostPackage.metadata.version -ceq $viewerPackage.metadata.version -and
        $hostPackage.metadata.file_version -ceq $viewerPackage.metadata.file_version -and
        $hostPackage.metadata.revision -ceq $viewerPackage.metadata.revision) 'Role build mismatch'
    foreach($package in $packages){
        $null=Invoke-App ($package.metadata.role+'-manifest') $SmokeApplication @('--collect-package',$package.root,'--verify-only')
    }
    Confirm (!(Test-Path (Join-Path $hostPackage.root 'Viewer')) -and !(Test-Path (Join-Path $viewerPackage.root 'Host')) -and !(Test-Path (Join-Path $viewerPackage.root 'HostUi')) -and !(Test-Path (Join-Path $viewerPackage.root 'Service')) -and !(Test-Path (Join-Path $viewerPackage.root 'ElevatedInput'))) 'Cross-role payload found'
    Confirm (@($hostPackage.metadata.components).Count -eq 4 -and $hostPackage.metadata.components -contains 'RelaxDesk.ElevatedInput' -and @($viewerPackage.metadata.components).Count -eq 1) 'Unexpected role components'
    Confirm (Test-Path -LiteralPath (Join-Path $hostPackage.root 'ElevatedInput/RelaxDesk.ElevatedInput.exe') -PathType Leaf) 'Administrator input worker missing from Host package'
    $hostExe=Join-Path $hostPackage.root 'Host/RelaxDesk.Host.exe';$shellExe=Join-Path $hostPackage.root 'HostUi/RelaxDesk.HostShell.exe';$serviceExe=Join-Path $hostPackage.root 'Service/RelaxDesk.Service.exe';$viewerExe=Join-Path $viewerPackage.root 'Viewer/RelaxDesk.Viewer.exe'
    $deployment=Invoke-App 'host-runtime' $hostExe @('--deployment-info') | ConvertFrom-Json
    Confirm ($deployment.x64 -and $deployment.runtime.StartsWith((Join-Path $hostPackage.root 'Host')+'\',[StringComparison]::OrdinalIgnoreCase) -and $deployment.version -ceq $hostPackage.metadata.version) 'Host did not use its matching bundled x64 runtime'
    Confirm ((Invoke-App 'host-ui-version' $shellExe @('--version')).Trim() -ceq $hostPackage.metadata.version) 'Host UI version mismatch'
    Confirm ((Invoke-App 'viewer-version' $viewerExe @('--version')).Trim() -ceq $viewerPackage.metadata.version) 'Viewer version mismatch'
    Confirm ((Invoke-App 'service-version' $serviceExe @('--version')).Trim() -ceq ('v'+$hostPackage.metadata.version)) 'Service version mismatch'
    $native=@((Join-Path $hostPackage.root 'Host/RelaxDesk.Native.dll'),(Join-Path $hostPackage.root 'HostUi/RelaxDesk.Native.dll'),(Join-Path $viewerPackage.root 'Viewer/RelaxDesk.Native.dll')) | ForEach-Object {(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    Confirm (@($native | Select-Object -Unique).Count -eq 1) 'Native DLL mismatch'
    $hostQuic = Join-Path $hostPackage.root 'Host/msquic.dll'
    $viewerQuic = Join-Path $viewerPackage.root 'Viewer/msquic.dll'
    Confirm ((Test-Path -LiteralPath $hostQuic -PathType Leaf) -and (Test-Path -LiteralPath $viewerQuic -PathType Leaf)) 'Bundled MsQuic runtime missing'
    Confirm ((Get-FileHash -LiteralPath $hostQuic -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $viewerQuic -Algorithm SHA256).Hash) 'Bundled MsQuic runtime mismatch'
    $provider=$hostPackage.metadata.quic_tls_provider
    $runtimeSource=$hostPackage.metadata.quic_runtime_source
    if($runtimeSource -or $viewerPackage.metadata.quic_runtime_source){
        Confirm ($runtimeSource -cin @('dotnet-runtime','microsoft-nuget') -and $runtimeSource -ceq $viewerPackage.metadata.quic_runtime_source) 'MsQuic runtime source mismatch'
    }
    if($provider -or $viewerPackage.metadata.quic_tls_provider){
        Confirm ($provider -cin @('schannel','openssl') -and $provider -ceq $viewerPackage.metadata.quic_tls_provider) 'MsQuic provider mismatch'
        Confirm ($deployment.quicTlsProvider -ceq $provider) 'Host loaded a different QUIC TLS provider than its manifest'
        $hostQuicVersion=(Get-Item -LiteralPath $hostQuic).VersionInfo.FileVersion
        Confirm ($hostQuicVersion -ceq $hostPackage.metadata.quic_runtime_version -and $hostQuicVersion -ceq $viewerPackage.metadata.quic_runtime_version) 'MsQuic version metadata mismatch'
        if($provider -ceq 'openssl'){
            Confirm ((Get-FileHash -LiteralPath $hostQuic -Algorithm SHA256).Hash -ceq '3FEA76E87287FEE7C5069D90BD4816BA8DB1F0712AB97215D4297749AEE31BE3') 'Unreviewed OpenSSL MsQuic binary'
            foreach($package in $packages){
                foreach($notice in @('LICENSE','THIRD-PARTY-NOTICES')){
                    Confirm (Test-Path -LiteralPath (Join-Path $package.root "ThirdParty/MsQuic-OpenSSL-2.5.13/$notice") -PathType Leaf) 'OpenSSL redistribution notice missing'
                }
            }
        }
        if($provider -ceq 'schannel' -and $runtimeSource -ceq 'microsoft-nuget'){
            Confirm ((Get-FileHash -LiteralPath $hostQuic -Algorithm SHA256).Hash -ceq 'ADDFF9F940BD2DFE914D390EA508242EE11D89EF963BD7D9160AF33AC1243CF2') 'Unreviewed Schannel MsQuic binary'
            foreach($package in $packages){
                Confirm (Test-Path -LiteralPath (Join-Path $package.root 'ThirdParty/MsQuic-schannel-2.5.13/LICENSE') -PathType Leaf) 'Schannel redistribution notice missing'
            }
        }
    }
    $servicePayloadVersion = Invoke-App 'host-service-payload' $SmokeApplication @('--service-package-contract', $hostPackage.root)
    Confirm ($servicePayloadVersion.Trim() -ceq $hostPackage.metadata.version) 'Package-only service validation failed'
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal=[Security.Principal.WindowsPrincipal]::new($identity)
        if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
            $steps.Add([pscustomobject]@{name='ordinary-token-worker-rejection';status='NOT_TESTED';reason='Run package checks from an ordinary Windows console'})
        } else {
            $workerExe=Join-Path $hostPackage.root 'ElevatedInput/RelaxDesk.ElevatedInput.exe'
            $null=Invoke-App 'ordinary-token-worker-rejection' $workerExe @([guid]::NewGuid().ToString('N'),[string]$PID,$identity.User.Value,'all') 10 4
        }
    } finally {$identity.Dispose()}
    if($SkipMachineServicePreview){
        $steps.Add([pscustomobject]@{name='host-service-readonly-preflight';status='NOT_TESTED';reason='Machine service preview explicitly skipped'})
    } else {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent();$owner=$identity.User.Value;$identity.Dispose()
        $preview=Invoke-App 'host-service-readonly-preflight' $serviceExe @('--install-service','--package',$hostPackage.root,'--owner-sid',$owner,'--what-if') | ConvertFrom-Json
        Confirm (!$preview.Changed -and $preview.Version -ceq $hostPackage.metadata.version) 'Service package readonly preview failed'
    }
    $ui=Invoke-App 'host-one-click-ui' $shellExe @('--service-setup-ui-contracts',$hostExe,(Join-Path $output 'ui-fixtures'))
    Confirm ($ui.Contains('PASS 54 actual WPF')) 'One-click UI acceptance marker missing'
    foreach($mode in @('deflate','approval-fallback-cpu','udp-cpu','auto-recovery-cpu')){
        $session=Invoke-App ('cross-role-'+$mode) $SmokeApplication @($hostExe,'--wpf',$viewerExe,$mode)
        Confirm ($session.Contains('PASS WPF') -and $session.Contains('native_live_resources=0')) 'Cross-role synthetic session/cleanup evidence missing'
        if($mode -eq 'auto-recovery-cpu'){
            Confirm ($session -match 'recovery_tcp_frames=[1-9][0-9]* recovery_udp_frames=[1-9][0-9]*') 'Packaged Viewer Auto transport switch missing'
        }
    }
    $quicSession = Invoke-App 'packaged-host-quic' $SmokeApplication @($hostExe, '--quic-production') 150
    Confirm ($quicSession.Contains('PASS production Host/SessionClient: UDP, Auto UDP, Auto fallback') -and
        $quicSession.Contains('PASS persistent QUIC loss caused Host TCP fallback') -and
        $quicSession.Contains('PASS Auto recovered UDP again after previously recovering and falling back') -and
        $quicSession.Contains('PASS recovered-path loss triggered Host TCP fallback') -and
        $quicSession.Contains('PASS independently approved TLS audio continued through repeated TCP/UDP video switches') -and
        $quicSession.Contains('PASS healthy static UDP video stayed connected through the datagram silence interval') -and
        $quicSession.Contains('PASS requested static desktop refresh delivered a decodable key frame over the same UDP path')) 'Packaged Host QUIC, fallback, or static refresh evidence missing'
    Confirm ($quicSession -match '(?m)^PASS shaped UDP 524288 B/s cap, 8/20 ms delay pulses, shaped_packets=[1-9][0-9]*, reordered_pairs=[1-9][0-9]*, QUIC_reordered=[1-9][0-9]*, advancing H\.264 and jitter statistics') 'Packaged Host shaped UDP delay, reordering or jitter evidence missing'
    Confirm ($quicSession -match '(?m)^PASS datagram loss relay dropped=[1-9][0-9]*, host_estimated_lost=[1-9][0-9]*, sent=[1-9][0-9]*' -and
        $quicSession.Contains('PASS loss relay drove the running H.264 encoder from 8 to 6 Mbit/s target')) 'Packaged Host measured UDP loss or bitrate adaptation evidence missing'
    $autoFiles = Invoke-App 'packaged-auto-files' $SmokeApplication @($hostExe, '--file-transfer-auto', (Join-Path $output 'auto-file-fixtures')) 150
    Confirm ($autoFiles.Contains('PASS Auto switched from TCP to UDP during the same download, file SHA-256 verified and TLS control kept alive')) 'Packaged Host file integrity during Auto recovery evidence missing'
    $udpFiles = Invoke-App 'packaged-udp-files' $SmokeApplication @($hostExe, '--file-transfer-udp', (Join-Path $output 'udp-file-fixtures')) 150
    Confirm ($udpFiles -match '(?m)^PASS shared downstream cap=[1-9][0-9]* B/s: active download video_transport=UDP, video_forwarded_bytes=[1-9][0-9]*, file_tls_bytes=[1-9][0-9]*' -and
        $udpFiles.Contains('PASS concurrent video and files with UDP media capped')) 'Packaged Host shared UDP video and TLS file bandwidth evidence missing'
    $resumeFiles = Invoke-App 'packaged-file-resume' $viewerExe @('--file-resume-session-contracts', $hostExe, (Join-Path $output 'file-resume-fixtures')) 150
    Confirm ($resumeFiles.Contains('PASS production Host/Viewer/TLS directory upload recovery') -and
        $resumeFiles.Contains('PASS production TLS directory download recovery') -and
        $resumeFiles.Contains('PASS revoke before resume confirmation')) 'Packaged file resume or revocation evidence missing'
    $fallbackStatistics = Invoke-App 'packaged-viewer-fallback-statistics' $viewerExe @('--statistics-contracts', $hostExe, (Join-Path $output 'fallback-statistics-fixtures'), 'oversized') 150
    Confirm ($fallbackStatistics.Contains('PASS WPF shows the authenticated oversized UDP frame fallback reason')) 'Packaged Viewer fallback statistics evidence missing'
    $sessionEnd = Invoke-App 'packaged-host-session-end' $SmokeApplication @($hostExe, '--session-unavailable')
    foreach ($reason in @('WindowsSessionUnavailable','WindowsLocked','WindowsSessionChanged','WindowsDesktopUnavailable')) {
        Confirm ($sessionEnd.Contains("PASS actual Host/TLS synthetic $reason")) "Packaged Host session termination evidence missing for $reason"
    }
    $adminLifecycle = Invoke-App 'packaged-admin-lifecycle' $viewerExe @('--admin-lifecycle-contracts',$hostExe)
    Confirm ($adminLifecycle.Contains('PASS WPF Viewer twice entered Admin and routed input to protected non-injecting pipe workers')) 'Packaged Admin lifecycle evidence missing'
    $adminWorkerLoss = Invoke-App 'packaged-admin-worker-loss' $viewerExe @('--admin-worker-loss-contracts', $hostExe)
    Confirm ($adminWorkerLoss.Contains('PASS WPF administrator worker pipe loss: original grant ended, Viewer required fresh approval and restored Work on a new TLS connection')) 'Packaged Host/Viewer administrator worker loss or fresh Work approval evidence missing'
    Confirm ($adminWorkerLoss.Contains('PASS Work input revoked in place with continued video and no Admin request action') -and
        $adminWorkerLoss.Contains('PASS late local administrator approval ignored after revocation')) 'Packaged Work input revocation or stale administrator approval evidence missing'
    $workTrust = Invoke-App 'packaged-work-trust' $shellExe @('--work-trust-contracts',$hostExe,(Join-Path $output 'work-trust-fixtures'))
    Confirm ($workTrust.Contains('PASS WPF Work device trust') -and $workTrust.Contains('PASS WPF one-use Work preapproval')) 'Packaged Work device trust and one-use approval evidence missing'
    $details=[pscustomobject]@{version=$hostPackage.metadata.version;revision=$hostPackage.metadata.revision;native_sha256=$native[0];quic_tls_provider=$provider;quic_runtime_version=$hostPackage.metadata.quic_runtime_version;packages=@($packages | ForEach-Object {[pscustomobject]@{role=$_.metadata.role;archive=$_.archive;sha256=$_.sha256;bytes=$_.bytes;manifest_files=$_.manifest_files}})}
    $status=if($SkipMachineServicePreview){'PASS_SEPARATE_PACKAGES_SYNTHETIC_ONLY_MACHINE_PREVIEW_SKIPPED'}else{'PASS_SEPARATE_PACKAGES_SYNTHETIC_ONLY'}
} finally {
    [pscustomobject]@{status=$status;assertions=$checks;details=$details;steps=$steps.ToArray();home_dual_pc='NOT_TESTED';uac_and_scm_mutation='NOT_TESTED';physical_capture_input_audio='NOT_TESTED'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
}
Write-Host "PASS separate packages: $checks assertions; result=$output/result.json"
