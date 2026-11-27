param([string]$OutputDirectory='', [string]$NativeBuildDirectory='build/native-roles', [switch]$SkipNativeBuild,
    [string]$MsQuicOpenSslPackage='', [string]$MsQuicSchannelPackage='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME=Join-Path $root '.dotnet'
    $revision=& git rev-parse HEAD
    if($LASTEXITCODE){throw 'Source revision unavailable'}
    $changes=@(& git status --porcelain)
    if($LASTEXITCODE -or $changes.Count){throw 'Role packages require committed, clean source'}
    function Assert-PackageRevision {
        $head = & git rev-parse HEAD
        if ($LASTEXITCODE -or $head -cne $revision) { throw 'Source revision changed during role packaging' }
        $pending = @(& git status --porcelain)
        if ($LASTEXITCODE -or $pending.Count) { throw 'Source files changed during role packaging' }
    }
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $version='0.4.2-roles.'+$stamp+'.g'+$revision.Substring(0,7)
    $fileVersion='0.4.'+(Get-Date -Format 'MMdd')+'.'+(Get-Date -Format 'HHmm')
    if(!$OutputDirectory){$OutputDirectory='build/RelaxDesk-separated-win-x64-'+$stamp}
    $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    if(!$output.StartsWith((Join-Path $root 'build')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Role output must be a fresh directory inside repository/build'}
    if(Test-Path -LiteralPath $output){throw 'Output exists; packages and evidence are never overwritten'}
    [void][IO.Directory]::CreateDirectory($output)
    $quicProvider='schannel'
    $quicRuntimeSource='dotnet-runtime'
    $quicAssets=$null
    if($MsQuicOpenSslPackage -and $MsQuicSchannelPackage){throw 'Choose one MsQuic package'}
    if($MsQuicOpenSslPackage -or $MsQuicSchannelPackage){
        $quicRuntimeSource='microsoft-nuget'
        $quicSource=$MsQuicSchannelPackage
        $expectedHash='98C7B6AC85CC1A1DB4C40EACFA2D153ED4434595C2D3A8B182FCF8D9764BB6CA'
        $quicNotices=@('LICENSE')
        if($MsQuicOpenSslPackage){
            $quicProvider='openssl'
            $quicSource=$MsQuicOpenSslPackage
            $expectedHash='2DCCC0030B7B85408612F1E7B1442B494B8CAAADE445C2DC6478A6D62CE4579E'
            $quicNotices+='THIRD-PARTY-NOTICES'
        }
        $quicPackage=(Resolve-Path -LiteralPath $quicSource).Path
        if((Get-FileHash -LiteralPath $quicPackage -Algorithm SHA256).Hash -cne $expectedHash){
            throw "Use the reviewed MsQuic 2.5.13 package for $quicProvider"
        }
        dotnet nuget verify --all $quicPackage
        if($LASTEXITCODE){throw 'MsQuic package signature verification failed'}
        $quicAssets=Join-Path $output 'msquic-input'
        [void][IO.Directory]::CreateDirectory($quicAssets)
        $quicArchive=[IO.Compression.ZipFile]::OpenRead($quicPackage)
        try {
            foreach($asset in @('build/native/bin/x64/msquic.dll')+$quicNotices){
                $entry=$quicArchive.GetEntry($asset)
                if(!$entry){throw "Missing MsQuic package entry: $asset"}
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $quicAssets ([IO.Path]::GetFileName($asset))),$false)
            }
        } finally {$quicArchive.Dispose()}
    }
    if(!$SkipNativeBuild){& "$PSScriptRoot/build-native.ps1" -BuildDirectory $NativeBuildDirectory}
    $native=Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.Native.dll"
    if(!(Test-Path -LiteralPath $native -PathType Leaf)){throw 'Native DLL missing'}
    $artifacts=Join-Path $root ('build/package-roles-artifacts-'+$stamp)
    $roles=[ordered]@{
        Host=[ordered]@{Name='RelaxDesk-Host-win-x64';Projects=[ordered]@{'RelaxDesk.Host'='Host';'RelaxDesk.HostShell'='HostUi';'RelaxDesk.Service'='Service';'RelaxDesk.ElevatedInput'='ElevatedInput'};Launcher='Start-Host.cmd';Entry='启动被控端.cmd';Guide='HOST.zh-CN.md'}
        Viewer=[ordered]@{Name='RelaxDesk-Viewer-win-x64';Projects=[ordered]@{'RelaxDesk.Viewer'='Viewer'};Launcher='Start-Viewer.cmd';Entry='启动连接端.cmd';Guide='VIEWER.zh-CN.md'}
    }
    $roleOutputs = @()
    foreach($role in $roles.Keys){
        $definition=$roles[$role];$package=Join-Path $output $definition.Name
        [void][IO.Directory]::CreateDirectory($package)
        foreach($project in $definition.Projects.Keys){
            Assert-PackageRevision
            dotnet publish "src/$project/$project.csproj" --artifacts-path $artifacts -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:InformationalVersion=$version" "-p:FileVersion=$fileVersion" -p:IncludeSourceRevisionInInformationalVersion=false "-p:RelaxDeskNativePath=$native" -o (Join-Path $package $definition.Projects[$project])
            if($LASTEXITCODE){throw "Publish failed: $project"}
            if ($project -in @('RelaxDesk.Host', 'RelaxDesk.Viewer')) {
                $runtime = Join-Path (Join-Path $package $definition.Projects[$project]) 'msquic.dll'
                if (!(Test-Path -LiteralPath $runtime -PathType Leaf)) { throw "Bundled MsQuic runtime missing: $project" }
                if($quicAssets){Copy-Item -LiteralPath (Join-Path $quicAssets 'msquic.dll') -Destination $runtime -Force}
                $quicVersion=(Get-Item -LiteralPath $runtime).VersionInfo.FileVersion
            }
            $assets=Get-Content -LiteralPath "$artifacts/obj/$project/project.assets.json" -Raw | ConvertFrom-Json
            $packs=@($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object {$_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'})
            if(!$packs.Count){throw "Runtime redistribution inventory missing: $project"}
            foreach($library in $packs){
                $packVersion=($library.version.Trim('[',']').Split(',')[0]).Trim()
                $relative=$library.name.ToLowerInvariant()+'/'+$packVersion
                $found=$false
                foreach($folder in $assets.packageFolders.PSObject.Properties.Name){
                    $directory=Join-Path $folder $relative
                    if(Test-Path -LiteralPath $directory){
                        $notices=Join-Path $package ('ThirdParty/'+$library.name+'-'+$packVersion)
                        [void][IO.Directory]::CreateDirectory($notices)
                        $files=@(Get-ChildItem -LiteralPath $directory -File | Where-Object {$_.Name -match 'LICENSE|NOTICE|THIRD.PARTY'})
                        if($files.Count){$files | Copy-Item -Destination $notices -Force;$found=$true}
                    }
                }
                if(!$found){throw "Runtime notices missing: $($library.name)"}
            }
        }
        # App Paths is registered under the executable's own name. The .NET
        # apphost embeds the managed DLL name, so this copy opens the same Viewer.
        if($role -eq 'Viewer'){
            Copy-Item -LiteralPath (Join-Path $package 'Viewer/RelaxDesk.Viewer.exe') -Destination (Join-Path $package 'Viewer/rdesk.exe')
        }
        $msquicNotices = Join-Path $package 'ThirdParty/MsQuic-2.5.12'
        [void][IO.Directory]::CreateDirectory($msquicNotices)
        Copy-Item -LiteralPath (Join-Path $root 'src/RelaxDesk.Native/third_party/msquic/LICENSE') -Destination $msquicNotices
        if($quicAssets){
            $runtimeNotices=Join-Path $package "ThirdParty/MsQuic-$quicProvider-2.5.13"
            [void][IO.Directory]::CreateDirectory($runtimeNotices)
            foreach($notice in $quicNotices){
                Copy-Item -LiteralPath (Join-Path $quicAssets $notice) -Destination $runtimeNotices
            }
        }
        Copy-Item -LiteralPath (Join-Path $root ('packaging/'+$definition.Launcher)) -Destination (Join-Path $package $definition.Entry)
        Copy-Item -LiteralPath (Join-Path $root ('packaging/roles/'+$definition.Guide)) -Destination (Join-Path $package '使用说明.txt')
        [pscustomobject]@{format=1;role=$role.ToLowerInvariant();version=$version;file_version=$fileVersion;revision=$revision;working_tree_dirty=$false;built_at=(Get-Date -Format o);rid='win-x64';self_contained=$true;trimmed=$false;native_abi=2;quic_tls_provider=$quicProvider;quic_runtime_version=$quicVersion;quic_runtime_source=$quicRuntimeSource;entry=$definition.Entry;components=@($definition.Projects.Keys);home_physical_console='NOT_TESTED';signature='UNSIGNED_EXPERIMENTAL'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'package.json') -Encoding utf8
        $hashes=@(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}
        })
        $hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.json') -Encoding utf8
        $archive=$package+'.zip'
        Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
        $hash=Get-FileHash -LiteralPath $archive -Algorithm SHA256
        ($hash.Hash+' *'+[IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive+'.sha256') -Encoding ascii
        $roleOutputs += ($role.ToUpperInvariant()+'_PACKAGE='+$archive)
        $roleOutputs += ($role.ToUpperInvariant()+'_SHA256='+$hash.Hash)
    }
    Assert-PackageRevision
    $roleOutputs | Write-Output
    Write-Output ('VERSION='+$version)
    Write-Output ('ARTIFACTS='+$artifacts)
} finally {Pop-Location}
