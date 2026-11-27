param([Parameter(Mandatory)][string]$InstallerDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$RunUserIntegration,[switch]$SkipMachineServicePreview,[switch]$AllowHostedRunAliasLookupFailure)
$ErrorActionPreference='Stop'
if(!$RunUserIntegration){throw 'Explicit -RunUserIntegration required: temporary current-user Apps entries, shortcuts and the Viewer Run alias will be exercised'}
$repository=Split-Path $PSScriptRoot -Parent;$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith((Join-Path $repository 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh workspace/build fixture'}
[void][IO.Directory]::CreateDirectory($output)
$installers=@(Get-Content -LiteralPath (Join-Path $InstallerDirectory 'installers.json') -Raw | ConvertFrom-Json)
$runAliasLookup='NOT_TESTED';$checks=0;$steps=[Collections.Generic.List[object]]::new();$state='FAIL';$roles=@();$launched=$null;$restorePath=$null;$restoreBytes=$null
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message};$script:checks++}
function Run([string]$Name,[string]$Exe,[string[]]$Arguments,[bool]$Success=$true){
    $info=[Diagnostics.ProcessStartInfo]::new($Exe);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    try {
        if(!$process.Start()){throw 'Installer did not start'}
        # Never terminate a running installer transaction on observation timeout.
        if(!$process.WaitForExit(180000)){throw "Installer still running: $Name; retain process and fixture for inspection"}
        $steps.Add([pscustomobject]@{name=$Name;exit_code=$process.ExitCode})
        if (($process.ExitCode -eq 0) -ne $Success) {
            $log = Join-Path $output ($Name + '.log')
            if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 20 | Write-Host }
        }
        Check (($process.ExitCode -eq 0) -eq $Success) "$Name returned unexpected exit $($process.ExitCode)"
        Write-Host "$Name : PASS ($($process.ExitCode))"
    } finally {$process.Dispose()}
}
function Install($Role,[string]$Name,[string]$Directory,[bool]$Success=$true){
    Run $Name $Role.installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS=desktopicon',('/DIR='+$Directory),('/LOG='+ (Join-Path $output ($Name+'.log')))) $Success
}
function Uninstall($Role,[string]$Name,[bool]$Success=$true){
    Run $Name (Join-Path $Role.root 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+(Join-Path $output ($Name+'.log')))) $Success
}
function Until([scriptblock]$Predicate){$watch=[Diagnostics.Stopwatch]::StartNew();while(!(& $Predicate)){if($watch.Elapsed.TotalSeconds -gt 30){throw 'Fixture state did not settle'};Start-Sleep -Milliseconds 100}}
# Use the native Shell link reader. WScript.Shell in pwsh on the hosted
# Windows runner can return an empty TargetPath for an installed link.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

public static class InstallerLinkTest
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr fileData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
    }

    public static string[] Read(string file)
    {
        var cls = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true);
        object obj = Activator.CreateInstance(cls);
        try
        {
            ((IPersistFile)obj).Load(file, 0); // STGM_READ; loading never edits the link.
            var link = (IShellLinkW)obj;
            var path = new StringBuilder(260);
            var args = new StringBuilder(32768);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            if (path.Length == 0) link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            link.GetArguments(args, args.Capacity);
            return new[] { path.ToString(), args.ToString() };
        }
        finally { Marshal.FinalReleaseComObject(obj); }
    }
}
'@
$aliasSubkey='Software\Microsoft\Windows\CurrentVersion\App Paths\rdesk.exe'
$aliasKey='HKCU:\'+$aliasSubkey
try {
    Check (!(Test-Path -LiteralPath $aliasKey)) 'Existing current-user rdesk alias found; integration refused'
    foreach($view in @('Registry32','Registry64')){
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
        try {
            $existing=$base.OpenSubKey($aliasSubkey)
            if($existing){$existing.Dispose();throw 'Existing machine rdesk alias found; integration refused'}
        } finally {$base.Dispose()}
    }
    Check ($installers.Count -eq 2) 'Expected two independent EXE installers'
    foreach($item in $installers){
        $role=if($item.role -eq 'host'){'Host'}elseif($item.role -eq 'viewer'){'Viewer'}else{throw 'Unknown installer role'}
        $title=if($role -eq 'Host'){'RelaxDesk 被控端'}else{'RelaxDesk 连接端'}
        $entry=if($role -eq 'Host'){'HostUi\RelaxDesk.HostShell.exe'}else{'Viewer\RelaxDesk.Viewer.exe'}
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RelaxDesk.'+$role+'_is1'
        $registration='HKCU:\Software\RelaxDesk\Installations\'+$role
        $desktop=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($title+'.lnk')
        $menu=Join-Path ([Environment]::GetFolderPath('Programs')) ('RelaxDesk\'+$title+'.lnk')
        Check (!(Test-Path -LiteralPath $key) -and !(Test-Path -LiteralPath $registration) -and !(Test-Path -LiteralPath $desktop) -and !(Test-Path -LiteralPath $menu)) 'Existing user installation/shortcut found; integration refused without changing it'
        Check ((Get-FileHash -LiteralPath $item.file -Algorithm SHA256).Hash -ceq $item.sha256) 'Installer hash mismatch'
        $roles+= [pscustomobject]@{name=$role;installer=$item.file;root=(Join-Path $output ($role+' 中文 & (Apps)'));key=$key;registration=$registration;desktop=$desktop;menu=$menu;release=$item.release;entry=$entry}
    }
    $hostRole=$roles | Where-Object name -EQ Host;$viewerRole=$roles | Where-Object name -EQ Viewer
    $foreign=Join-Path $output 'foreign-directory';[void][IO.Directory]::CreateDirectory($foreign)
    [IO.File]::WriteAllText((Join-Path $foreign 'keep.txt'),'not installer-owned')
    Install $viewerRole 'reject-foreign-directory' $foreign $false
    Check ((Get-Content -LiteralPath (Join-Path $foreign 'keep.txt') -Raw) -eq 'not installer-owned' -and !(Test-Path $viewerRole.key)) 'Foreign directory was modified/adopted'
    $junction=Join-Path $output 'junction-target';$null=New-Item -ItemType Junction -Path $junction -Target $foreign
    Install $hostRole 'reject-junction' $junction $false
    Check (!(Test-Path $hostRole.key) -and (Get-Content -LiteralPath (Join-Path $foreign 'keep.txt') -Raw) -eq 'not installer-owned') 'Junction escape was not rejected'
    $foreignAlias=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($aliasSubkey)
    try {
        $foreignAlias.SetValue('', 'C:\foreign\rdesk.exe')
        Install $viewerRole 'reject-foreign-run-alias' $viewerRole.root $false
        Check ($foreignAlias.GetValue('') -ceq 'C:\foreign\rdesk.exe' -and !(Test-Path $viewerRole.key)) 'Viewer overwrote a foreign Run alias'
    } finally {
        $ownsFixture=$foreignAlias.ValueCount -eq 1 -and $foreignAlias.SubKeyCount -eq 0 -and $foreignAlias.GetValue('') -ceq 'C:\foreign\rdesk.exe'
        $foreignAlias.Dispose()
        if($ownsFixture){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($aliasSubkey)}
    }
    foreach($role in $roles){
        Install $role ($role.name+'-install') $role.root
        Check ((Test-Path $role.key) -and (Test-Path $role.registration) -and (Test-Path $role.desktop) -and (Test-Path $role.menu)) 'Missing Apps registration or shortcuts'
        $registered=Get-ItemProperty -LiteralPath $role.key
        Check ($registered.DisplayVersion -eq $installers[0].package_version -and [IO.Path]::GetFullPath($registered.InstallLocation).TrimEnd('\') -ieq $role.root) 'Wrong uninstall identity/location/version'
        $expected=Join-Path (Join-Path $role.root $role.release) $role.entry
        $expectedAlias=Join-Path (Split-Path $expected -Parent) 'rdesk.exe'
        if($role.name -eq 'Host'){
            Check (!(Test-Path -LiteralPath $aliasKey)) 'Host installer registered the Viewer alias'
        }else{
            Check ((Get-Item -LiteralPath $aliasKey).GetValue('') -ieq $expectedAlias -and (Test-Path -LiteralPath $expectedAlias -PathType Leaf)) 'Viewer Run alias points to the wrong EXE'
        }
        foreach($path in @($role.desktop,$role.menu)){
            $target,$arguments=[InstallerLinkTest]::Read($path)
            $resolved=if($target -and [IO.Path]::IsPathFullyQualified($target)) { [IO.Path]::GetFullPath($target) } else { '' }
            Check ($resolved -ieq $expected -and $arguments.Length -eq 0 -and (Test-Path -LiteralPath $resolved -PathType Leaf)) ("Shortcut does not launch the role EXE directly: {0}; target=[{1}]; expected=[{2}]; argument length={3}" -f $path,$target,$expected,$arguments.Length)
        }
        $payload=Join-Path $role.root $role.release
        $manifest=@(Get-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.json') -Raw | ConvertFrom-Json)
        foreach($member in $manifest){if((Get-FileHash -LiteralPath (Join-Path $payload $member.path) -Algorithm SHA256).Hash -ine $member.sha256){throw 'Installed payload hash mismatch'}}
        Check (@(Get-ChildItem -LiteralPath $payload -File -Recurse).Count -eq $manifest.Count+1) 'Installed runtime closure has unexpected files'
        $appInfo=[Diagnostics.ProcessStartInfo]::new($expected);$appInfo.UseShellExecute=$false;$appInfo.WindowStyle='Minimized';$appInfo.WorkingDirectory=$payload
        $launched=[Diagnostics.Process]::Start($appInfo)
        Until {$launched.Refresh();$launched.HasExited -or $launched.MainWindowHandle -ne 0}
        Check (!$launched.HasExited -and $launched.MainWindowHandle -ne 0) 'Installed role did not open its normal WPF window'
        if($role.name -eq 'Host'){
            $children=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId = '+$launched.Id) | Where-Object Name -EQ 'RelaxDesk.Host.exe')
            Check ($children.Count -eq 0) 'Installing/opening Host implicitly started desktop sharing'
            if ($SkipMachineServicePreview) {
                $steps.Add([pscustomobject]@{name='installed-host-service-readonly-preview';status='NOT_TESTED';reason='Machine service preview explicitly skipped'})
            } else {
                $identity=[Security.Principal.WindowsIdentity]::GetCurrent();$owner=$identity.User.Value;$identity.Dispose()
                Run 'installed-host-service-readonly-preview' (Join-Path $payload 'Service/RelaxDesk.Service.exe') @('--install-service','--package',$payload,'--owner-sid',$owner,'--what-if')
            }
        }
        Install $role ($role.name+'-reject-running-update') $role.root $false
        Check (!$launched.HasExited) 'Installer terminated an active application'
        $null=$launched.CloseMainWindow();if(!$launched.WaitForExit(15000)){throw 'Owned installed app did not close'};$launched.Dispose();$launched=$null
        Install $role ($role.name+'-repeat-install') $role.root
        Check ((Get-ItemProperty -LiteralPath $role.registration).Receipts.Split(';').Count -eq 1) 'Repeated install duplicated receipt/version state'
        if($role.name -eq 'Viewer'){
            Check ((Get-Item -LiteralPath $aliasKey).GetValue('') -ieq $expectedAlias -and (Test-Path -LiteralPath $expectedAlias -PathType Leaf)) 'Viewer reinstall did not preserve the Run alias'
        }
    }
    Install $hostRole 'reject-second-install-root' (Join-Path $output 'second-host-root') $false
    Check ((Get-ItemProperty $hostRole.registration).Root -ieq $hostRole.root) 'Existing Host installation was taken over'
    $restorePath=Join-Path (Join-Path $viewerRole.root $viewerRole.release) '使用说明.txt';$restoreBytes=[IO.File]::ReadAllBytes($restorePath)
    [IO.File]::WriteAllText($restorePath,'operator changed this file')
    Install $viewerRole 'reject-modified-update' $viewerRole.root $false
    Uninstall $viewerRole 'reject-modified-uninstall' $false
    Check ((Get-Content -LiteralPath $restorePath -Raw) -eq 'operator changed this file' -and (Test-Path $viewerRole.key)) 'Modified file was overwritten/deleted'
    [IO.File]::WriteAllBytes($restorePath,$restoreBytes);$restorePath=$null;$restoreBytes=$null
    $unknown=Join-Path $hostRole.root 'operator-notes.txt';[IO.File]::WriteAllText($unknown,'retain unknown content')
    $viewerExe=Join-Path (Join-Path $viewerRole.root $viewerRole.release) 'Viewer/rdesk.exe'
    $info=[Diagnostics.ProcessStartInfo]::new('rdesk.exe');$info.UseShellExecute=$true;$info.Verb='open';$info.WindowStyle='Minimized';$info.WorkingDirectory=$output
    try {
        $shellProcess=[Diagnostics.Process]::Start($info)
        $runAliasLookup='PASS_SHELL_EXECUTE'
    } catch {
        $cause=$_.Exception.GetBaseException()
        if (!$AllowHostedRunAliasLookupFailure -or $env:GITHUB_ACTIONS -ne 'true' -or
            $cause -isnot [ComponentModel.Win32Exception] -or $cause.NativeErrorCode -ne 2) { throw }
        $runAliasLookup='FAILED_HOSTED_SHELL_FILE_NOT_FOUND'
        Write-Warning 'Hosted Windows Shell did not resolve the registered short command. Verify the installed apphost and lifecycle by absolute path; no Win+R claim.'
        $info=[Diagnostics.ProcessStartInfo]::new($viewerExe)
        $info.UseShellExecute=$false
        $info.WorkingDirectory=Split-Path $viewerExe -Parent
        $shellProcess=[Diagnostics.Process]::Start($info)
    }
    if(!$shellProcess){throw 'Shell launch via rdesk.exe returned no process'}
    try {
        if($shellProcess.MainModule.FileName -ine $viewerExe){throw 'Shell resolved rdesk.exe to a different application'}
        $launched=$shellProcess
    } finally {
        if(!$launched){$shellProcess.Dispose()}
    }
    Until {$launched.Refresh();$launched.HasExited -or $launched.MainWindowHandle -ne 0}
    Check (!$launched.HasExited -and $launched.MainWindowHandle -ne 0) 'Shell launch via rdesk.exe did not open its WPF window'
    Uninstall $viewerRole 'reject-running-viewer' $false
    Check ((Test-Path $viewerRole.key) -and !$launched.HasExited) 'Uninstaller terminated active Viewer'
    Uninstall $hostRole 'host-remove-while-viewer-running'
    Until {!(Test-Path $hostRole.key) -and !(Test-Path (Join-Path $hostRole.root $hostRole.release))}
    Check ((Test-Path $viewerRole.key) -and (Test-Path $viewerRole.menu) -and (Test-Path -LiteralPath $aliasKey) -and !$launched.HasExited) 'Host uninstall affected Viewer'
    Check (!(Test-Path $hostRole.desktop) -and !(Test-Path $hostRole.menu) -and !(Test-Path $hostRole.registration) -and (Get-Content -LiteralPath $unknown -Raw) -eq 'retain unknown content') 'Host removal lost retained data or left owned integration'
    $null=$launched.CloseMainWindow();if(!$launched.WaitForExit(15000)){throw 'Owned test Viewer did not close'};$launched.Dispose();$launched=$null
    Uninstall $viewerRole 'viewer-remove'
    Until {!(Test-Path $viewerRole.key) -and !(Test-Path $viewerRole.root)}
    Check (!(Test-Path $viewerRole.desktop) -and !(Test-Path $viewerRole.menu) -and !(Test-Path $viewerRole.registration)) 'Viewer left owned integration'
    Check (!(Test-Path -LiteralPath $aliasKey)) 'Viewer left its Run alias after uninstall'
    $state=if($SkipMachineServicePreview){'PASS_ACTUAL_PER_USER_INSTALLER_LIFECYCLE_MACHINE_PREVIEW_SKIPPED'}else{'PASS_ACTUAL_PER_USER_INSTALLER_LIFECYCLE'}
} finally {
    if($launched){try{if(!$launched.HasExited){$null=$launched.CloseMainWindow();$null=$launched.WaitForExit(10000)}}finally{$launched.Dispose()}}
    if($restorePath -and $restoreBytes){[IO.File]::WriteAllBytes($restorePath,$restoreBytes)}
    # On failure, leave receipts/logs for inspection instead of forcing deletion.
    [pscustomobject]@{status=$state;assertions=$checks;steps=$steps.ToArray();fixture=$output;run_alias_lookup=$runAliasLookup;real_scm_or_uac='NOT_TESTED';home_dual_pc='NOT_TESTED'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
}
Write-Host "PASS actual EXE installer lifecycle: $checks checks; $output/result.json"
