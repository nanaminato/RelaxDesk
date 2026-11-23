Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Get-SetupPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Empty setup path' }
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.StartsWith('\\') -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\') -or $full.Contains('"')) { throw 'Setup requires a local, non-root directory' }
    Assert-SetupPath $full
    return $full
}
function Test-SetupChild([string]$Child,[string]$Parent) {
    return $Child.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
}
function Assert-SetupPath([string]$Path) {
    $cursor=$Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point unsupported: $cursor" }
        }
        $parent=[IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor=$parent
    }
}
function Get-SetupFiles([string]$Root,[switch]$Directories) {
    Assert-SetupPath $Root
    $visited=0
    $pending=New-Object 'Collections.Generic.Stack[string]';$pending.Push($Root)
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $visited++;if($visited -gt 20000){throw 'Package tree entry limit exceeded'}
            Assert-SetupPath $entry.FullName
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName);if($Directories){$entry} } elseif(!$Directories) { $entry }
        }
    }
}
function Get-SetupMember([string]$Root,[string]$Relative) {
    if (!$Relative -or $Relative.Length -gt 240 -or $Relative.Contains('\') -or $Relative.Contains(':') -or $Relative.StartsWith('/')) { throw 'Invalid package relative path' }
    foreach ($part in $Relative.Split('/')) {
        if (!$part -or $part -in @('.','..') -or $part -match '[<>"|?*\x00-\x1f]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)') { throw 'Unsafe package path component' }
    }
    $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!(Test-SetupChild $full $Root)) { throw 'Package path escapes directory' }
    Assert-SetupPath $full;return $full
}
function Read-SetupJson([string]$Path) {
    Assert-SetupPath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 4MB) { throw 'Missing/oversized setup metadata' }
    $parsed=Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    return $parsed
}
function Get-SetupHash([string]$Path) {
    Assert-SetupPath $Path
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
    finally { $sha.Dispose();$stream.Dispose() }
}
function Read-SetupPackage([string]$Root,[switch]$AllowMissing) {
    $entries=@(Read-SetupJson (Join-Path $Root 'SHA256SUMS.json'))
    if ($entries.Count -lt 1 -or $entries.Count -gt 10000) { throw 'Invalid package file count' }
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$total=0
    foreach ($entry in $entries) {
        $file=Get-SetupMember $Root $entry.path
        if ($entry.path -ieq 'SHA256SUMS.json' -or !$seen.Add($entry.path) -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $entry.bytes -isnot [ValueType] -or $entry.bytes -is [bool] -or $entry.bytes -lt 0 -or $entry.bytes -gt 2GB -or $entry.bytes -ne [Math]::Truncate([double]$entry.bytes)) { throw 'Invalid/duplicate package entry' }
        $total += [long]$entry.bytes
        if ($total -gt 8GB) { throw 'Package size limit exceeded' }
        if (!$AllowMissing) {
            if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-SetupHash $file) -ne $entry.sha256) { throw "Package hash/size mismatch: $($entry.path)" }
        }
    }
    if (!$AllowMissing) {
        $actual=@(Get-SetupFiles $Root)
        if ($actual.Count -ne $entries.Count+1) { throw 'Package contains unlisted files' }
        foreach ($file in $actual) { $relative=$file.FullName.Substring($Root.Length+1).Replace('\','/');if ($relative -ine 'SHA256SUMS.json' -and !$seen.Contains($relative)) { throw 'Package contains unlisted file' } }
    }
    return ,$entries
}
function Get-SetupContext {
    return @{ Registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RelaxDesk'; Menu=(Join-Path ([Environment]::GetFolderPath('Programs')) 'RelaxDesk'); Desktop=[Environment]::GetFolderPath('DesktopDirectory'); Startup=[Environment]::GetFolderPath('Startup'); Fault='' }
}
function Get-SetupLogonMode($State) {
    if($null -eq $State -or $null -eq $State.PSObject.Properties['logon']){return 'Disabled'}
    if($State.logon -notin @('Disabled','Window','ServiceWindow')){throw 'Invalid logon startup mode'}
    return $State.logon
}
function Get-SetupLogonArguments([string]$Mode) {
    switch($Mode){'Window'{return '--logon'} 'ServiceWindow'{return '--logon --service-agent'} 'Disabled'{return ''} default{throw 'Invalid logon startup mode'}}
}
function Get-SetupLogonLink($Context,[string]$Mode) {
    $directory=Get-SetupPath $Context.Startup
    return [pscustomobject]@{Path=(Join-Path $directory 'RelaxDesk Host.lnk');Relative='HostUi\RelaxDesk.HostShell.exe';Arguments=(Get-SetupLogonArguments $Mode);Remove=($Mode -eq 'Disabled')}
}
function Get-SetupOwner { return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
function Read-SetupState([string]$Root) {
    $file=Join-Path $Root 'setup-state.json'
    if (!(Test-Path -LiteralPath $Root)) { return $null }
    $state=Read-SetupJson $file
    if ($state.format -ne 1 -or $state.owner -ne (Get-SetupOwner) -or $state.root -ine $Root -or $state.current -notmatch '^app-[a-f0-9]{32}$' -or @($state.releases).Count -notin 1..4 -or $state.current -notin $state.releases -or $state.desktop -isnot [bool]) { throw 'Unowned/invalid installation directory' }
    if(@($state.releases | Select-Object -Unique).Count -ne @($state.releases).Count){throw 'Duplicate installed release'}
    foreach ($release in $state.releases) { if ($release -notmatch '^app-[a-f0-9]{32}$') { throw 'Invalid installed release path' };Assert-SetupPath (Join-Path $Root $release) }
    $null=Get-SetupLogonMode $state
    return $state
}
function Write-SetupState([string]$Root,$State) {
    Assert-SetupPath $Root
    $temporary=Join-Path $Root ('state-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    $destination=Join-Path $Root 'setup-state.json'
    try {
        [IO.File]::WriteAllText($temporary,($State | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding $true))
        Assert-SetupPath $destination
        if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary,$destination,[NullString]::Value) } else { [IO.File]::Move($temporary,$destination) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Enter-SetupLock([string]$Root) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $name=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.ToUpperInvariant())))).Replace('-','') } finally { $sha.Dispose() }
    $mutex=New-Object Threading.Mutex $false,('Local\RelaxDesk.Setup.'+(Get-SetupOwner)+'.'+$name)
    try { if (!$mutex.WaitOne(0)) { throw 'Another setup operation is running' } } catch [Threading.AbandonedMutexException] { return $mutex } catch { $mutex.Dispose();throw }
    return $mutex
}
function Assert-SetupStopped([string]$Root) {
    foreach ($process in [Diagnostics.Process]::GetProcesses()) {
        try {
            if ($process.ProcessName -notin @('RelaxDesk.Host','RelaxDesk.HostShell','RelaxDesk.Viewer')) { continue }
            try { $path=$process.MainModule.FileName } catch { throw 'Cannot verify running RelaxDesk process; close it before setup' }
            if (Test-SetupChild $path $Root) { throw 'Close this installation of RelaxDesk before setup; setup never kills active sessions' }
        } finally { $process.Dispose() }
    }
}
function Read-SetupRegistration([string]$Key,[string]$Root) {
    if(!$Key.StartsWith('HKCU:\Software\',[StringComparison]::OrdinalIgnoreCase)){throw 'Setup registration must stay in current-user Software'}
    if (!(Test-Path -LiteralPath $Key)) { return $null }
    $item=Get-Item -LiteralPath $Key
    if ($item.GetValue('InstallLocation') -ine $Root -or @($item.GetSubKeyNames()).Count) { throw 'Existing uninstall registration belongs to another installation' }
    $values=@{}
    foreach ($name in $item.GetValueNames()) { $values[$name]=@{Data=$item.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);Kind=$item.GetValueKind($name)} }
    return $values
}
function Restore-SetupRegistration([string]$Key,$Values) {
    if (Test-Path -LiteralPath $Key) { Remove-Item -LiteralPath $Key -Force }
    if ($null -ne $Values) {
        $item=New-Item -Path $Key -Force
        foreach ($name in $Values.Keys) { $item.SetValue($name,$Values[$name].Data,$Values[$name].Kind) }
    }
}
function Get-SetupLinks($Context,[bool]$Desktop) {
    $directories=@($Context.Menu);if ($Desktop) { $directories+=@($Context.Desktop) }
    foreach ($directory in $directories) {
        $directory=Get-SetupPath $directory
        foreach ($pair in @(@('RelaxDesk Host.lnk','HostUi\RelaxDesk.HostShell.exe'),@('RelaxDesk Viewer.lnk','Viewer\RelaxDesk.Viewer.exe'))) {
            [pscustomobject]@{Path=(Join-Path $directory $pair[0]);Relative=$pair[1];Arguments='';Remove=$false}
        }
    }
}
function Read-SetupLink([string]$Path,[string]$Root,$State,[string]$ExpectedArguments='') {
    Assert-SetupPath $Path
    if (!(Test-Path -LiteralPath $Path)) { return $null }
    if ($null -eq $State) { throw 'Shortcut collision; existing shortcut is not owned' }
    $shell=New-Object -ComObject WScript.Shell
    try {
        $shortcut=$shell.CreateShortcut($Path)
        try {
            $allowed=$false
            $relative=if([IO.Path]::GetFileName($Path) -eq 'RelaxDesk Host.lnk'){'HostUi\RelaxDesk.HostShell.exe'}else{'Viewer\RelaxDesk.Viewer.exe'}
            foreach ($release in $State.releases) { if ($shortcut.TargetPath -ieq (Join-Path (Join-Path $Root $release) $relative)) { $allowed=$true } }
            if (!$allowed -or $shortcut.Arguments -cne $ExpectedArguments) { throw 'Shortcut collision; existing shortcut is not owned' }
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        return ,[IO.File]::ReadAllBytes($Path)
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Write-SetupLink($Link,[string]$Release) {
    Assert-SetupPath $Link.Path
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Link.Path))
    $shell=New-Object -ComObject WScript.Shell
    try {
        $shortcut=$shell.CreateShortcut($Link.Path)
        try { $shortcut.TargetPath=Join-Path $Release $Link.Relative;$shortcut.Arguments=$Link.Arguments;$shortcut.WorkingDirectory=$Release;$shortcut.Description='RelaxDesk — visible UI; sharing requires explicit Host approval';$shortcut.Save() }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Remove-SetupEmptyDirectories([string]$Root,[string[]]$OwnedDirectories) {
    Assert-SetupPath $Root
    $directories=@($OwnedDirectories | Sort-Object Length -Descending)
    foreach ($directory in $directories) { if(!(Test-SetupChild $directory $Root)){throw 'Directory cleanup escapes release'};Assert-SetupPath $directory;if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory) } }
    if (@(Get-ChildItem -LiteralPath $Root -Force).Count -eq 0) { [IO.Directory]::Delete($Root) }
}
function Remove-SetupRelease([string]$Root,[string]$Release) {
    if ($Release -notmatch '^app-[a-f0-9]{32}$') { throw 'Invalid release removal' }
    $directory=Join-Path $Root $Release
    if (!(Test-SetupChild $directory $Root)) { throw 'Release removal escapes installation' }
    Assert-SetupPath $directory
    if (!(Test-Path -LiteralPath $directory)) { return $true }
    $entries=Read-SetupPackage $directory -AllowMissing
    $ownedDirectories=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $entries){$parent=[IO.Path]::GetDirectoryName((Get-SetupMember $directory $entry.path));while(Test-SetupChild $parent $directory){[void]$ownedDirectories.Add($parent);$parent=[IO.Path]::GetDirectoryName($parent)}}
    foreach ($entry in $entries) {
        $file=Get-SetupMember $directory $entry.path
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            if ((Get-Item -LiteralPath $file).Length -eq $entry.bytes -and (Get-SetupHash $file) -eq $entry.sha256) {
                try { Remove-Item -LiteralPath $file } catch { Write-Warning "Retained locked file: $file" }
            } else { Write-Warning "Retained modified file: $file" }
        }
    }
    $remaining=@(Get-SetupFiles $directory | Where-Object { $_.Name -ne 'SHA256SUMS.json' -or $_.DirectoryName -ine $directory })
    $unknownDirectories=@(Get-SetupFiles $directory -Directories | Where-Object { !$ownedDirectories.Contains($_.FullName) })
    if ($remaining.Count -eq 0 -and $unknownDirectories.Count -eq 0) { Remove-Item -LiteralPath (Join-Path $directory 'SHA256SUMS.json');Remove-SetupEmptyDirectories $directory @($ownedDirectories) }
    return !(Test-Path -LiteralPath $directory)
}

function Install-RelaxDeskPackage {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param([Parameter(Mandatory)][string]$PackageRoot,[Parameter(Mandatory)][string]$InstallRoot,[switch]$DesktopShortcuts,[ValidateSet('Keep','Disabled','Window','ServiceWindow')][string]$LogonStartup='Keep',$Context=(Get-SetupContext))
    $source=Get-SetupPath $PackageRoot;$root=Get-SetupPath $InstallRoot
    if(![Environment]::Is64BitOperatingSystem){throw 'RelaxDesk requires Windows x64'}
    foreach($machineDirectory in @($env:SystemRoot,$env:ProgramFiles,${env:ProgramFiles(x86)},$env:ProgramData)){
        if($machineDirectory){$machineDirectory=[IO.Path]::GetFullPath($machineDirectory).TrimEnd('\');if($root -ieq $machineDirectory -or (Test-SetupChild $root $machineDirectory)){throw 'User setup cannot install into machine/system directories'}}
    }
    if ($root -ieq $source -or (Test-SetupChild $root $source) -or (Test-SetupChild $source $root)) { throw 'Package and installation directories must not overlap' }
    $entries=Read-SetupPackage $source
    $package=Read-SetupJson (Join-Path $source 'package.json')
    if ($package.format -ne 1 -or $package.rid -ne 'win-x64' -or $package.self_contained -isnot [bool] -or $package.self_contained -ne $true -or $package.version -isnot [string] -or $package.version.Length -notin 1..128 -or $package.version -match '[\x00-\x1f]') { throw 'Unsupported package metadata' }
    foreach ($required in @('Host/RelaxDesk.Host.exe','HostUi/RelaxDesk.HostShell.exe','Viewer/RelaxDesk.Viewer.exe','scripts/RelaxDesk.Setup.psm1','scripts/Uninstall-RelaxDesk.ps1','package.json')) {
        if ($required -notin $entries.path) { throw "Required package file missing: $required" }
    }
    if (!$PSCmdlet.ShouldProcess($root,('Install/upgrade RelaxDesk '+$package.version+' for current user; startup choice '+$LogonStartup+'; no firewall changes'))) { return [pscustomobject]@{Installed=$false;Root=$root;Version=$package.version} }
    $mutex=Enter-SetupLock $root
    try {
        $state=Read-SetupState $root;Assert-SetupStopped $root
        if($null -ne $state){foreach($old in $state.releases){$oldDirectory=Join-Path $root $old;if(Test-Path -LiteralPath $oldDirectory){$null=Read-SetupPackage $oldDirectory -AllowMissing;$null=@(Get-SetupFiles $oldDirectory)}}}
        if ($null -ne $state -and @($state.releases).Count -ge 4) { throw 'Too many retained releases; review modified files and uninstall before upgrading' }
        $registration=Read-SetupRegistration $Context.Registry $root
        $desktop=[bool]$DesktopShortcuts -or ($null -ne $state -and [bool]$state.desktop)
        $oldLogon=Get-SetupLogonMode $state
        $logon=if($LogonStartup -eq 'Keep'){$oldLogon}else{$LogonStartup}
        $links=@(Get-SetupLinks $Context $desktop);$snapshots=@{}
        foreach ($link in $links) { $snapshots[$link.Path]=Read-SetupLink $link.Path $root $state }
        if($logon -ne 'Disabled' -or $oldLogon -ne 'Disabled'){
            $startupLink=Get-SetupLogonLink $Context $logon
            if($startupLink.Path -in $links.Path){throw 'Startup and ordinary shortcut locations overlap'}
            if($oldLogon -eq 'Disabled' -and (Test-Path -LiteralPath $startupLink.Path)){throw 'Existing startup shortcut is not owned'}
            $snapshots[$startupLink.Path]=Read-SetupLink $startupLink.Path $root $state (Get-SetupLogonArguments $oldLogon)
            $links+=@($startupLink)
        }
        [void][IO.Directory]::CreateDirectory($root)
        $release='app-'+[Guid]::NewGuid().ToString('N');$destination=Join-Path $root $release
        [void][IO.Directory]::CreateDirectory($destination)
        $activated=$false;$integrationStarted=$false
        try {
            $copied=0
            foreach ($relative in @('SHA256SUMS.json')+@($entries.path)) {
                $from=Get-SetupMember $source $relative;$to=Get-SetupMember $destination $relative
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to));[IO.File]::Copy($from,$to,$false)
                $copied++;if($Context.Fault -eq 'mid-copy' -and $copied -eq 3){throw 'Injected setup failure during copy'}
            }
            $null=Read-SetupPackage $destination
            if ($Context.Fault -eq 'after-copy') { throw 'Injected setup failure after copy' }
            $integrationStarted=$true
            foreach ($link in $links) { if($link.Remove){if(Test-Path -LiteralPath $link.Path){Remove-Item -LiteralPath $link.Path}}else{Write-SetupLink $link $destination} }
            $values=@{
                DisplayName='RelaxDesk (current user)';DisplayVersion=$package.version;InstallLocation=$root;Publisher='RelaxDesk';
                UninstallString=('"'+$env:SystemRoot+'\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "'+$destination+'\scripts\Uninstall-RelaxDesk.ps1" -InstallRoot "'+$root+'"');
                DisplayIcon=(Join-Path $destination 'Viewer\RelaxDesk.Viewer.exe');NoModify=1;NoRepair=1;InstallDate=(Get-Date -Format yyyyMMdd)
            }
            $key=New-Item -Path $Context.Registry -Force
            foreach ($name in $values.Keys) { $kind=if($values[$name] -is [int]){[Microsoft.Win32.RegistryValueKind]::DWord}else{[Microsoft.Win32.RegistryValueKind]::String};$key.SetValue($name,$values[$name],$kind) }
            if ($Context.Fault -eq 'after-registration') { throw 'Injected setup failure after registration' }
            $releases=@($release);if ($null -ne $state) { $releases+=@($state.releases) }
            $next=[pscustomobject]@{format=1;owner=(Get-SetupOwner);root=$root;current=$release;releases=$releases;version=$package.version;desktop=$desktop;logon=$logon}
            Write-SetupState $root $next;$activated=$true
        } finally {
            if (!$activated) {
                if ($integrationStarted) {
                    Restore-SetupRegistration $Context.Registry $registration
                    foreach ($link in $links) { Assert-SetupPath $link.Path;if ($null -ne $snapshots[$link.Path]) { [IO.File]::WriteAllBytes($link.Path,$snapshots[$link.Path]) } elseif (Test-Path -LiteralPath $link.Path) { Remove-Item -LiteralPath $link.Path } }
                }
                if (Test-Path -LiteralPath (Join-Path $destination 'SHA256SUMS.json')) { $null=Remove-SetupRelease $root $release }
                if($null -eq $state -and @(Get-ChildItem -LiteralPath $root -Force).Count -eq 0){[IO.Directory]::Delete($root)}
            }
        }
        $retained=@($release)
        if ($null -ne $state) { foreach ($old in $state.releases) { try { if (!(Remove-SetupRelease $root $old)) { $retained+=@($old) } } catch { Write-Warning "Retained previous release: $old";$retained+=@($old) } } }
        $next.releases=$retained;Write-SetupState $root $next
        Write-Output ([pscustomobject]@{Installed=$true;Root=$root;Version=$package.version;ApplicationRoot=$destination;RetainedPreviousReleases=$retained.Count-1;Logs=(Join-Path $env:LOCALAPPDATA 'RelaxDesk\logs');Firewall='Unchanged; approve only a trusted private network if Windows prompts';Startup=$logon})
    } finally { $mutex.ReleaseMutex();$mutex.Dispose() }
}

function Uninstall-RelaxDeskPackage {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param([Parameter(Mandatory)][string]$InstallRoot,$Context=(Get-SetupContext))
    $root=Get-SetupPath $InstallRoot
    if (!$PSCmdlet.ShouldProcess($root,'Uninstall current-user RelaxDesk; preserve modified/unknown files and external logs')) { return }
    $mutex=Enter-SetupLock $root
    try {
        $state=Read-SetupState $root;if ($null -eq $state) { throw 'Installation not found' };Assert-SetupStopped $root
        foreach($release in $state.releases){$directory=Join-Path $root $release;if(Test-Path -LiteralPath $directory){$null=Read-SetupPackage $directory -AllowMissing;$null=@(Get-SetupFiles $directory)}}
        $null=Read-SetupRegistration $Context.Registry $root
        $removalLinks=@(Get-SetupLinks $Context ([bool]$state.desktop));$logon=Get-SetupLogonMode $state
        if($logon -ne 'Disabled'){$removalLinks+=@(Get-SetupLogonLink $Context $logon)}
        foreach ($link in $removalLinks) {
            try { $owned=Read-SetupLink $link.Path $root $state $link.Arguments;if ($null -ne $owned) { Remove-Item -LiteralPath $link.Path } } catch { Write-Warning "Retained changed shortcut: $($link.Path)" }
        }
        Restore-SetupRegistration $Context.Registry $null
        $retained=@()
        foreach ($release in $state.releases) { if (!(Remove-SetupRelease $root $release)) { $retained+=@($release) } }
        if ($retained.Count) { $state.releases=$retained;$state.current=$retained[0];Write-SetupState $root $state }
        else { Remove-Item -LiteralPath (Join-Path $root 'setup-state.json');if(@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0){[IO.Directory]::Delete($root)} }
        if ((Test-Path -LiteralPath $Context.Menu) -and @(Get-ChildItem -LiteralPath $Context.Menu -Force).Count -eq 0) { Assert-SetupPath $Context.Menu;[IO.Directory]::Delete($Context.Menu) }
        [pscustomobject]@{Uninstalled=$true;Root=$root;RemainingFiles=(Test-Path -LiteralPath $root);LogsPreserved=(Join-Path $env:LOCALAPPDATA 'RelaxDesk\logs')}
    } finally { $mutex.ReleaseMutex();$mutex.Dispose() }
}
Export-ModuleMember -Function Install-RelaxDeskPackage,Uninstall-RelaxDeskPackage
