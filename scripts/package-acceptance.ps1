param([string]$OutputDirectory = '', [string]$NativeBuildDirectory = 'build/native-package', [switch]$SkipNativeBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $revision = & git rev-parse HEAD
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $productVersion = '0.4.0-hdrui.' + $stamp + '.g' + $revision.Substring(0,7)
    $fileVersion = '0.4.' + (Get-Date -Format 'MMdd') + '.' + (Get-Date -Format 'HHmm')
    if (!$OutputDirectory) { $OutputDirectory = 'build/RelaxDesk-acceptance-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.zip'))) { throw 'Output already exists; evidence/packages are never overwritten.' }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    if (!$SkipNativeBuild) { & "$PSScriptRoot/build-native.ps1" -BuildDirectory $NativeBuildDirectory }
    $native = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.Native.dll"
    $probe = Join-Path $root "$NativeBuildDirectory/Release/RelaxDesk.NativeProbe.exe"
    if (!(Test-Path $native) -or !(Test-Path $probe)) { throw 'Native build missing' }
    $projects = [ordered]@{'RelaxDesk.Host'='Host';'RelaxDesk.Viewer'='Viewer';'RelaxDesk.HostShell'='HostUi';'RelaxDesk.Smoke'='Tools/Smoke';'RelaxDesk.Tests'='Tools/Tests'}
    foreach ($project in $projects.Keys) {
        dotnet publish "src/$project/$project.csproj" --artifacts-path "$root/build/package-artifacts" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:InformationalVersion=$productVersion" "-p:FileVersion=$fileVersion" -p:IncludeSourceRevisionInInformationalVersion=false "-p:RelaxDeskNativePath=$native" -o (Join-Path $output $projects[$project])
        if ($LASTEXITCODE) { throw "Publish failed: $project" }
        # Preserve runtime-pack redistribution notices, in addition to anything
        # copied by publish. Do not assign a license to RelaxDesk itself.
        $assets = Get-Content -Raw "$root/build/package-artifacts/obj/$project/project.assets.json" | ConvertFrom-Json
        $runtimePacks = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$' })
        foreach ($library in $runtimePacks) {
            $version = ($library.version.Trim('[',']').Split(',')[0]).Trim()
            $relative = $library.name.ToLowerInvariant() + '/' + $version
            $foundNotices = $false
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $directory = Join-Path $folder $relative
                if (Test-Path -LiteralPath $directory) {
                    $notices = Join-Path $output ('ThirdParty/' + $library.name + '-' + $version)
                    [IO.Directory]::CreateDirectory($notices) | Out-Null
                    $files = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match 'LICENSE|NOTICE|THIRD.PARTY' })
                    if ($files.Count) { $files | Copy-Item -Destination $notices -Force; $foundNotices = $true }
                }
            }
            if (!$foundNotices) { throw "Missing redistribution notices for $($library.name)" }
        }
    }
    $nativeFolder = Join-Path $output 'Tools/Native'; [IO.Directory]::CreateDirectory($nativeFolder) | Out-Null
    Copy-Item -LiteralPath $native,$probe -Destination $nativeFolder
    $scripts = Join-Path $output 'scripts'; [IO.Directory]::CreateDirectory($scripts) | Out-Null
    foreach ($name in @('accept-home.ps1','verify.ps1','benchmark-renderers.ps1','measure-resources.ps1','summarize-renderers.py')) { Copy-Item -LiteralPath "$PSScriptRoot/$name" -Destination $scripts }
    Copy-Item -LiteralPath "$root/packaging/Collect-Logs.cmd","$root/packaging/Collect-Logs.ps1","$root/packaging/RUN-ME.zh-CN.md","$root/packaging/Start-Host.cmd","$root/packaging/Start-Viewer.cmd" -Destination $output
    $revision = & git rev-parse HEAD
    $dirty = [bool](& git status --porcelain)
    [pscustomobject]@{format=1;version=$productVersion;file_version=$fileVersion;revision=$revision;working_tree_dirty=$dirty;built_at=(Get-Date -Format o);rid='win-x64';self_contained=$true;trimmed=$false;native_abi=2;minimum_os='Windows 10 x64 (supported .NET 11 build) / Windows 11 x64';home_physical_console='NOT_TESTED';signature='UNSIGNED_EXPERIMENTAL'} | ConvertTo-Json | Set-Content (Join-Path $output 'package.json') -Encoding utf8
    "RelaxDesk $productVersion`r`nRevision: $revision`r`nBuild: $stamp`r`nConnect: IP + port, then click Allow in Host window" | Set-Content (Join-Path $output 'BUILD-INFO.txt') -Encoding utf8
    $hashes = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($output.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}
    })
    $hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'SHA256SUMS.json') -Encoding utf8
    Compress-Archive -LiteralPath $output -DestinationPath ($output+'.zip') -CompressionLevel Optimal
    $zipHash = Get-FileHash -LiteralPath ($output+'.zip') -Algorithm SHA256
    ($zipHash.Hash + ' *' + [IO.Path]::GetFileName($output+'.zip')) | Set-Content -LiteralPath ($output+'.zip.sha256') -Encoding ascii
    $zipHash | Format-List
    Write-Output "PACKAGE=$output.zip"
} finally { Pop-Location }
