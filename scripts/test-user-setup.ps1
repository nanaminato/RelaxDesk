param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $repository ('build/setup-verification-'+[Guid]::NewGuid().ToString('N'))}
$fixture=[IO.Path]::GetFullPath($OutputDirectory)
if(!$fixture.StartsWith((Join-Path $repository 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixture)){throw 'Tests require a fresh directory inside repository/build'}
[void][IO.Directory]::CreateDirectory($fixture)
Import-Module (Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1') -Force
$module=Get-Module RelaxDesk.Setup
$context=@{Registry=('HKCU:\Software\RelaxDesk.Setup.Tests.'+[Guid]::NewGuid().ToString('N'));Menu=(Join-Path $fixture 'menu');Desktop=(Join-Path $fixture 'desktop');Startup=(Join-Path $fixture 'startup');Fault=''}
$script:checks=0
function Check([bool]$Result,[string]$Message){if(!$Result){throw "Setup check failed: $Message"};$script:checks++}
function Reject([scriptblock]$Work,[string]$Message){$denied=$false;try{& $Work | Out-Null}catch{$denied=$true};Check $denied $Message}
function Put([string]$Path,[string]$Text){[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding $true))}
function Manifest([string]$Directory){
    $entries=@(Get-ChildItem -LiteralPath $Directory -File -Recurse | Where-Object Name -ne 'SHA256SUMS.json' | ForEach-Object {[pscustomobject]@{path=$_.FullName.Substring($Directory.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}})
    ConvertTo-Json -InputObject $entries -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.json') -Encoding UTF8
}
function Package([string]$Name,[string]$Version){
    $directory=Join-Path $fixture $Name
    foreach($file in @('Host/RelaxDesk.Host.exe','HostUi/RelaxDesk.HostShell.exe','Viewer/RelaxDesk.Viewer.exe','ThirdParty/NOTICE.txt','说明/中文 空格.txt')){Put (Join-Path $directory $file) ('generated fixture '+$Version+' '+$file)}
    [void][IO.Directory]::CreateDirectory((Join-Path $directory 'scripts'))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1'),(Join-Path $PSScriptRoot 'Uninstall-RelaxDesk.ps1') -Destination (Join-Path $directory 'scripts')
    [pscustomobject]@{format=1;version=$Version;rid='win-x64';self_contained=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'package.json') -Encoding UTF8
    Manifest $directory;return $directory
}
function State([string]$Root){Get-Content -LiteralPath (Join-Path $Root 'setup-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json}
$source=Package 'source one' '0.5.0-fixture.1'
$second=Package 'source two' '0.5.0-fixture.2'
$root=Join-Path $fixture '安装 空格 & (用户)'
try{
    $plan=Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -DesktopShortcuts -Context $context -WhatIf
    Check (!$plan.Installed -and !(Test-Path -LiteralPath $root) -and !(Test-Path -LiteralPath $context.Registry) -and !(Test-Path -LiteralPath $context.Menu)) 'WhatIf writes nothing'
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $source -Context $context} 'source equals destination'
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot (Join-Path $source 'nested') -Context $context} 'destination inside source'
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $fixture -Context $context} 'source inside destination'
    $bad=Package 'bad' '0.5.0-bad'
    Put (Join-Path $bad 'Viewer/RelaxDesk.Viewer.exe') 'tampered'
    Reject {Install-RelaxDeskPackage -PackageRoot $bad -InstallRoot $root -Context $context} 'corrupted content'
    Manifest $bad;Put (Join-Path $bad 'unlisted.txt') 'extra'
    Reject {Install-RelaxDeskPackage -PackageRoot $bad -InstallRoot $root -Context $context} 'unlisted source file'
    $manifest=Join-Path $bad 'SHA256SUMS.json'
    $original=Get-Content -LiteralPath (Join-Path $source 'SHA256SUMS.json') -Raw -Encoding UTF8
    foreach($path in @('../outside.txt','/absolute','C:/absolute','a\b','a:stream','a/../b','CON.txt','dir/end.','dir/end ','dir//file')){
        ConvertTo-Json -InputObject @([pscustomobject]@{path=$path;sha256=('A'*64);bytes=0}) | Set-Content -LiteralPath $manifest -Encoding UTF8
        Reject {Install-RelaxDeskPackage -PackageRoot $bad -InstallRoot $root -Context $context} ('unsafe path '+$path)
    }
    $duplicate=Get-Content -LiteralPath (Join-Path $source 'SHA256SUMS.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    ConvertTo-Json -InputObject @($duplicate[0],$duplicate[0]) | Set-Content -LiteralPath $manifest -Encoding UTF8
    Reject {Install-RelaxDeskPackage -PackageRoot $bad -InstallRoot $root -Context $context} 'duplicate manifest path'
    Put (Join-Path $root 'do-not-remove.txt') 'unowned'
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -Context $context} 'existing unowned directory'
    Check ((Get-Content -LiteralPath (Join-Path $root 'do-not-remove.txt')) -eq 'unowned') 'unowned file preserved'
    Remove-Item -LiteralPath (Join-Path $root 'do-not-remove.txt');[IO.Directory]::Delete($root)
    foreach($firstFault in @('mid-copy','after-registration')){
        $context.Fault=$firstFault
        Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -DesktopShortcuts -Context $context} ('fresh install rollback '+$firstFault)
        Check (!(Test-Path -LiteralPath $root) -and !(Test-Path -LiteralPath $context.Registry)) 'failed fresh install leaves no owned app/registration'
    }
    $context.Fault=''
    $install=Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -DesktopShortcuts -Context $context
    Check ($install.Installed -and $install.Version -eq '0.5.0-fixture.1') 'first install'
    $initial=State $root
    Check ($initial.owner -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) 'installation bound to current SID'
    Check ((Get-Item -LiteralPath $context.Registry).GetValue('DisplayVersion') -eq '0.5.0-fixture.1') 'HKCU version registered'
    Check ((Get-Item -LiteralPath $context.Registry).GetValue('UninstallString').Contains('-InstallRoot "'+$root+'"')) 'uninstall command quotes install root'
    $shell=New-Object -ComObject WScript.Shell
    try{
        $link=$shell.CreateShortcut((Join-Path $context.Desktop 'RelaxDesk Host.lnk'))
        try{Check ($link.TargetPath -eq (Join-Path $install.ApplicationRoot 'HostUi/RelaxDesk.HostShell.exe') -and !$link.Arguments) 'actual desktop shortcut target'}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)}
    }finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
    Check ((Get-Content -LiteralPath (Join-Path $install.ApplicationRoot 'ThirdParty/NOTICE.txt')) -like '*fixture*') 'third-party notices retained'
    & (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -File (Join-Path $PSScriptRoot 'Install-RelaxDesk.ps1') -PackageRoot $source -InstallRoot $root -WhatIf
    Check ($LASTEXITCODE -eq 0) 'public install CLI WhatIf handles Unicode/space/metacharacter path'
    & (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -File (Join-Path $PSScriptRoot 'Uninstall-RelaxDesk.ps1') -InstallRoot $root -WhatIf
    Check ($LASTEXITCODE -eq 0) 'public uninstall CLI WhatIf'
    $initialShortcutHash=(Get-FileHash -LiteralPath (Join-Path $context.Desktop 'RelaxDesk Host.lnk') -Algorithm SHA256).Hash
    Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context -WhatIf
    Check (Test-Path -LiteralPath (Join-Path $root 'setup-state.json')) 'uninstall WhatIf retains installation'
    foreach($fault in @('mid-copy','after-copy','after-registration')){
        $context.Fault=$fault
        Reject {Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context} ('upgrade rollback '+$fault)
        Check ((State $root).current -eq $initial.current) 'rollback preserves active version'
        Check ((Get-Item -LiteralPath $context.Registry).GetValue('DisplayVersion') -eq '0.5.0-fixture.1') 'rollback restores registry'
        Check (@(Get-ChildItem -LiteralPath $root -Directory).Count -eq 1) 'rollback removes staged files'
        Check ((Get-FileHash -LiteralPath (Join-Path $context.Desktop 'RelaxDesk Host.lnk') -Algorithm SHA256).Hash -eq $initialShortcutHash) 'rollback restores exact shortcut bytes'
    }
    $context.Fault=''
    # Another process holds the exact per-root mutex; setup must reject before mutation.
    $holderScript=Join-Path $fixture 'hold-setup-lock.ps1'
    Put $holderScript @'
param([string]$ModulePath,[string]$Root)
$ErrorActionPreference='Stop'
Import-Module $ModulePath -Force
& (Get-Module RelaxDesk.Setup) {param($Root) $held=Enter-SetupLock $Root;try{[Console]::WriteLine('LOCKED');[void][Console]::ReadLine()}finally{$held.ReleaseMutex();$held.Dispose()}} $Root
'@
    $holderInfo=New-Object Diagnostics.ProcessStartInfo
    $holderInfo.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $holderInfo.Arguments='-NoProfile -File "'+$holderScript+'" "'+(Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1')+'" "'+$root+'"'
    $holderInfo.UseShellExecute=$false;$holderInfo.CreateNoWindow=$true;$holderInfo.RedirectStandardInput=$true;$holderInfo.RedirectStandardOutput=$true
    $holder=[Diagnostics.Process]::Start($holderInfo)
    try{
        $ready=$holder.StandardOutput.ReadLineAsync();if(!$ready.Wait(10000)){throw 'Setup mutex fixture timed out'}
        Check ($ready.Result -eq 'LOCKED') 'cross-process setup lock held'
        Reject {Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context} 'concurrent install refused'
        Reject {Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context} 'concurrent uninstall refused'
        Check ((State $root).current -eq $initial.current) 'concurrent refusal preserves state'
    }finally{$holder.StandardInput.WriteLine('release');$holder.StandardInput.Flush();if(!$holder.WaitForExit(10000)){$holder.Kill();$holder.WaitForExit()};$holder.Dispose()}
    # A real owned test process exercises the refusal to kill/replace running applications.
    $fixtureExe=Join-Path $install.ApplicationRoot 'Viewer/RelaxDesk.Viewer.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/ping.exe') -Destination $fixtureExe -Force
    $processInfo=New-Object Diagnostics.ProcessStartInfo
    $processInfo.FileName=$fixtureExe;$processInfo.Arguments='-t 127.0.0.1';$processInfo.UseShellExecute=$false;$processInfo.CreateNoWindow=$true
    $running=[Diagnostics.Process]::Start($processInfo)
    try{
        Start-Sleep -Milliseconds 100
        Check (!$running.HasExited) 'owned running-process fixture started'
        Reject {Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context} 'upgrade refuses running owned process'
        Reject {Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context} 'uninstall refuses running owned process'
        Check (!$running.HasExited) 'setup never kills running process'
    }finally{if(!$running.HasExited){$running.Kill();$running.WaitForExit()};$running.Dispose();Copy-Item -LiteralPath (Join-Path $source 'Viewer/RelaxDesk.Viewer.exe') -Destination $fixtureExe -Force}
    # A reparse point added after installation must fail preflight before unregistering.
    $releaseJunction=Join-Path $install.ApplicationRoot 'replaced-directory'
    New-Item -ItemType Junction -Path $releaseJunction -Target $second | Out-Null
    try{
        Reject {Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context} 'installed reparse point rejected before teardown'
        Check (Test-Path -LiteralPath $context.Registry) 'reparse refusal preserves registration'
    }finally{[IO.Directory]::Delete($releaseJunction)}
    $updated=Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context
    Check ($updated.Version -eq '0.5.0-fixture.2' -and !(Test-Path -LiteralPath $install.ApplicationRoot)) 'upgrade activates new and removes unchanged old release'
    Check ((State $root).desktop -and (Test-Path -LiteralPath (Join-Path $context.Desktop 'RelaxDesk Host.lnk'))) 'desktop option retained on upgrade'
    $unknown=Join-Path $root 'my-notes.txt';Put $unknown 'keep'
    $modified=Join-Path $updated.ApplicationRoot '说明/中文 空格.txt';Put $modified 'user edited'
    $unknownEmptyDirectory=Join-Path $updated.ApplicationRoot 'my-empty-folder';[void][IO.Directory]::CreateDirectory($unknownEmptyDirectory)
    Import-Module (Join-Path $updated.ApplicationRoot 'scripts/RelaxDesk.Setup.psm1') -Force
    $removed=Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context
    Check ($removed.Uninstalled -and $removed.RemainingFiles -and (Test-Path -LiteralPath $unknown) -and (Test-Path -LiteralPath $modified)) 'uninstall preserves unknown and modified files'
    Check (Test-Path -LiteralPath $unknownEmptyDirectory -PathType Container) 'uninstall preserves unknown empty directory'
    Check (!(Test-Path -LiteralPath $context.Registry) -and !(Test-Path -LiteralPath (Join-Path $context.Desktop 'RelaxDesk Host.lnk'))) 'uninstall removes owned registration/shortcuts'
    # Restore the generated file to demonstrate a subsequent cleanup without force deletion.
    Copy-Item -LiteralPath (Join-Path $second '说明/中文 空格.txt') -Destination $modified -Force
    Remove-Item -LiteralPath $unknown
    [IO.Directory]::Delete($unknownEmptyDirectory)
    $removed=Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context
    Check (!$removed.RemainingFiles -and !(Test-Path -LiteralPath $root)) 'installed module can remove its own files and completely uninstall'
    Reject {Uninstall-RelaxDeskPackage -InstallRoot $source -Context $context} 'uninstall refuses unowned source directory'
    # Junctions are created only within this fresh fixture; nothing outside is touched.
    $outside=Join-Path $fixture 'junction-target';Put (Join-Path $outside 'sentinel.txt') 'untouched'
    $junction=Join-Path $fixture 'junction-install'
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot (Join-Path $junction 'app') -Context $context} 'reparse ancestor rejected'
    Reject {Uninstall-RelaxDeskPackage -InstallRoot $junction -Context $context} 'reparse uninstall root rejected'
    Check ((Get-Content -LiteralPath (Join-Path $outside 'sentinel.txt')) -eq 'untouched') 'junction target unchanged'
    [IO.Directory]::Delete($junction)
    $startup=Join-Path $context.Startup 'RelaxDesk Host.lnk'
    $fresh=Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -Context $context
    Check ($fresh.Startup -eq 'Disabled' -and !(Test-Path -LiteralPath $startup)) 'fresh installation has no startup entry'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Window -Context $context -WhatIf | Out-Null
    Check (!(Test-Path -LiteralPath $startup)) 'startup WhatIf writes nothing'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Window -Context $context | Out-Null
    $shell=New-Object -ComObject WScript.Shell
    $link=$shell.CreateShortcut($startup)
    Check ($link.Arguments -eq '--logon' -and (State $root).logon -eq 'Window') 'explicit visible window startup'
    $hash=(Get-FileHash -LiteralPath $startup).Hash
    $context.Fault='after-registration'
    Reject {Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -LogonStartup ServiceWindow -Context $context} 'startup rollback'
    $context.Fault=''
    Check ((Get-FileHash -LiteralPath $startup).Hash -eq $hash -and (State $root).logon -eq 'Window') 'startup rollback restores exact shortcut and choice'
    $fresh=Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context
    $link=$shell.CreateShortcut($startup)
    Check ($link.Arguments -eq '--logon' -and $link.TargetPath -eq (Join-Path $fresh.ApplicationRoot 'HostUi/RelaxDesk.HostShell.exe')) 'upgrade retains choice and retargets startup'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup ServiceWindow -Context $context | Out-Null
    Check ($shell.CreateShortcut($startup).Arguments -eq '--logon --service-agent') 'service startup is explicit'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Disabled -Context $context | Out-Null
    Check (!(Test-Path -LiteralPath $startup) -and (State $root).logon -eq 'Disabled') 'disable removes owned startup'
    Put $startup 'foreign entry'
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Window -Context $context} 'foreign startup collision refused'
    Check ((Get-Content -LiteralPath $startup) -eq 'foreign entry') 'foreign startup preserved'
    Remove-Item -LiteralPath $startup
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Window -Context $context | Out-Null
    Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context | Out-Null
    Check (!(Test-Path -LiteralPath $startup)) 'uninstall removes owned startup'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -Context $context | Out-Null
    $legacy=State $root;$legacy.PSObject.Properties.Remove('logon')
    $legacy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'setup-state.json') -Encoding UTF8
    Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context | Out-Null
    Check ((State $root).logon -eq 'Disabled' -and !(Test-Path -LiteralPath $startup)) 'legacy state upgrade defaults startup off'
    Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -LogonStartup Window -Context $context | Out-Null
    $link=$shell.CreateShortcut($startup);$link.Arguments='--foreign';$link.Save()
    Reject {Install-RelaxDeskPackage -PackageRoot $second -InstallRoot $root -Context $context} 'changed startup arguments block upgrade'
    Uninstall-RelaxDeskPackage -InstallRoot $root -Context $context | Out-Null
    Check ((Test-Path -LiteralPath $startup) -and $shell.CreateShortcut($startup).Arguments -eq '--foreign') 'uninstall preserves altered startup'
    Remove-Item -LiteralPath $startup
    $other=New-Item -Path $context.Registry -Force;$other.SetValue('InstallLocation','C:\unrelated')
    Reject {Install-RelaxDeskPackage -PackageRoot $source -InstallRoot $root -Context $context} 'foreign uninstall registration collision'
    Remove-Item -LiteralPath $context.Registry
    Write-Output "PASS $script:checks user setup contracts: real files/HKCU isolated test key/COM shortcuts, Unicode paths, manifests, WhatIf, rollback, upgrade, process/mutex refusal, preserved edits, clean uninstall and junction rejection. Generated EXEs are text; running-process fixture uses Windows ping, no RelaxDesk capture/input. fixture=$fixture"
}finally{
    $context.Fault=''
    if(Test-Path -LiteralPath $context.Registry){Remove-Item -LiteralPath $context.Registry -Force}
}
