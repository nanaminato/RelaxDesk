param([Parameter(Mandatory)][string]$RoleDirectory,[Parameter(Mandatory)][string]$CompilerDirectory,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $revision=& git rev-parse HEAD
    if($LASTEXITCODE -or !$revision){throw 'Installer recipe revision unavailable'}
    $changes=@(& git status --porcelain)
    if($LASTEXITCODE -or $changes.Count){throw 'Installer recipe must be committed and clean'}
    if(!$OutputDirectory){$OutputDirectory='build/RelaxDesk-installers-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss')}
    $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    if(!$output.StartsWith((Join-Path $root 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh build output directory'}
    [void][IO.Directory]::CreateDirectory($output)
    $compiler=[IO.Path]::GetFullPath($CompilerDirectory);$iscc=Join-Path $compiler 'ISCC.exe';$license=Join-Path $compiler 'license.txt'
    if(!(Test-Path -LiteralPath $iscc) -or !(Test-Path -LiteralPath $license)){throw 'Verified stable Inno Setup compiler/license missing'}
    $signature=Get-AuthenticodeSignature -LiteralPath $iscc
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B.V.'){throw 'Compiler publisher/signature invalid'}
    $compilerVersion=(& $iscc --version | Out-String).Trim()
    if($LASTEXITCODE -or $compilerVersion -ne '7.1.1'){throw 'Use the reviewed stable Inno Setup 7.1.1 compiler'}
    $results=@();$sharedVersion=$null;$sharedFileVersion=$null
    foreach($role in @('Host','Viewer')){
        $archive=Join-Path ([IO.Path]::GetFullPath($RoleDirectory)) ('RelaxDesk-'+$role+'-win-x64.zip')
        $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if($archiveHash -cne (Get-Content -LiteralPath ($archive+'.sha256') -Raw).Split(' ')[0].Trim()){throw 'Role ZIP hash mismatch'}
        $work=Join-Path $output ('source-'+$role);Expand-Archive -LiteralPath $archive -DestinationPath $work
        $payload=Join-Path $work ('RelaxDesk-'+$role+'-win-x64')
        $metadata=Get-Content -LiteralPath (Join-Path $payload 'package.json') -Raw | ConvertFrom-Json
        if($metadata.role -cne $role.ToLowerInvariant() -or !$metadata.self_contained -or $metadata.rid -ne 'win-x64' -or $metadata.working_tree_dirty){throw 'Invalid role package metadata'}
        if($metadata.revision -cne $revision){throw "Role package revision does not match the installer recipe: $role"}
        if(!$metadata.version -or !$metadata.file_version){throw "Role package version is missing: $role"}
        if($sharedVersion -and ($metadata.version -cne $sharedVersion -or $metadata.file_version -cne $sharedFileVersion)){throw 'Host/Viewer versions differ'}
        $sharedVersion=$metadata.version;$sharedFileVersion=$metadata.file_version
        $manifestPath=Join-Path $payload 'SHA256SUMS.json';$manifest=@(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
        if(@(Get-ChildItem -LiteralPath $payload -File -Recurse).Count -ne $manifest.Count+1){throw 'Unexpected role payload files'}
        $release='app-'+(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.Substring(0,32).ToLowerInvariant()
        $lines=[Collections.Generic.List[string]]::new()
        foreach($entry in $manifest){
            $relative=[string]$entry.path
            if($relative.Contains(':') -or $relative.Contains('\') -or $relative.Split('/') -contains '..' -or $relative.StartsWith('/')){throw 'Invalid role member'}
            $file=Join-Path $payload $relative
            if((Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256){throw 'Role payload integrity failure'}
            $lines.Add($entry.sha256.ToLowerInvariant()+"`t"+$release+'\'+$relative.Replace('/','\'))
        }
        $lines.Add((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()+"`t"+$release+'\SHA256SUMS.json')
        $lines.Add((Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash.ToLowerInvariant()+"`t"+'receipts\'+$release+'\InnoSetup-LICENSE.txt')
        $receipt=Join-Path $work ($release+'.hashes');[IO.File]::WriteAllLines($receipt,$lines,[Text.UTF8Encoding]::new($true))
        $title=if($role -eq 'Host'){'RelaxDesk 被控端'}else{'RelaxDesk 连接端'}
        $entryExe=if($role -eq 'Host'){'HostUi\RelaxDesk.HostShell.exe'}else{'Viewer\RelaxDesk.Viewer.exe'}
        $query=if($role -eq 'Host'){'SELECT ExecutablePath FROM Win32_Process WHERE Name=''RelaxDesk.HostShell.exe'' OR Name=''RelaxDesk.Host.exe'''}else{'SELECT ExecutablePath FROM Win32_Process WHERE Name=''RelaxDesk.Viewer.exe'''}
        $query=$query.Replace("'","''")
        & $iscc '--quiet' ("--define=Role=$role") ("--define=Title=$title") ("--define=AppVersion="+$metadata.version) ("--define=FileVersion="+$metadata.file_version) ("--define=Release=$release") ("--define=Entry=$entryExe") ("--define=Payload=$payload") ("--define=Receipt=$receipt") ("--define=ReceiptHash="+(Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash.ToLowerInvariant()) ("--define=CompilerLicense=$license") ("--define=OutputDirectory=$output") ("--define=ProcessQuery=$query") (Join-Path $root 'packaging/installer/RelaxDesk.iss')
        if($LASTEXITCODE){throw "Installer compile failed: $role"}
        $exe=Join-Path $output ("RelaxDesk-$role-Setup-x64.exe");$hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        ($hash+' *'+[IO.Path]::GetFileName($exe)) | Set-Content -LiteralPath ($exe+'.sha256') -Encoding ascii
        $results+= [pscustomobject]@{role=$role.ToLowerInvariant();file=$exe;sha256=$hash;bytes=(Get-Item -LiteralPath $exe).Length;package_version=$metadata.version;application_revision=$metadata.revision;release=$release;source_archive_sha256=$archiveHash;recipe_revision=$revision;compiler=$compilerVersion;compiler_pe_product_version=(Get-Item -LiteralPath $iscc).VersionInfo.ProductVersion;compiler_sha256=(Get-FileHash -LiteralPath $iscc -Algorithm SHA256).Hash;signature='UNSIGNED_EXPERIMENTAL'}
        Write-Output ($role.ToUpperInvariant()+'_INSTALLER='+$exe)
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'installers.json') -Encoding utf8
} finally {Pop-Location}
