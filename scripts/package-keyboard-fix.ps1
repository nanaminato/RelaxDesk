param([string]$OutputDirectory='', [string]$BaseRevision='10bd9ee')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME=Join-Path $root '.dotnet'
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    if (!$OutputDirectory) { $OutputDirectory='build/keyboard-delivery-'+$stamp }
    $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory' }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    # Isolate this explicitly requested keyboard fix from the parent's in-flight
    # HDR/native/UI edits. No checkout/reset/worktree or git mutation occurs.
    $revision=(& git rev-parse $BaseRevision).Trim()
    $archive=Join-Path $output 'source-base.zip'; & git archive --format=zip "--output=$archive" $revision
    if ($LASTEXITCODE) { throw 'Source snapshot failed' }
    $source=Join-Path $output 'source'; Expand-Archive -LiteralPath $archive -DestinationPath $source
    $files=@(
        'src/RelaxDesk.Viewer/NativeVideoHost.cs','src/RelaxDesk.Viewer/RemoteKeyboardKeys.cs',
        'src/RelaxDesk.Viewer/BitmapKeyboardInput.cs','src/RelaxDesk.Viewer/KeyboardFocusVerification.cs',
        'src/RelaxDesk.Windows/Desktop.cs','src/RelaxDesk.Windows/KeyboardInjectionFlags.cs',
        'src/RelaxDesk.Windows/WindowsContracts.cs'
    )
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $source $file) }
    $app=Join-Path $source 'src/RelaxDesk.Viewer/App.cs'
    $text=[IO.File]::ReadAllText($app)
    if (!$text.Contains('--keyboard-contracts')) {
        $text=$text.Replace('if (args is ["--hwnd-contracts"]) HwndLifecycleVerification.Run(app);','if (args is ["--hwnd-contracts"]) HwndLifecycleVerification.Run(app);'+[Environment]::NewLine+'            else if (args is ["--keyboard-contracts"]) KeyboardFocusVerification.Run(app);')
        [IO.File]::WriteAllText($app,$text)
    }
    $native=Join-Path $root 'build/native-capturefix/Release/RelaxDesk.Native.dll'
    $probe=Join-Path $root 'build/native-capturefix/Release/RelaxDesk.NativeProbe.exe'
    if (!(Test-Path $native)) { throw 'Stable baseline native build/native-capturefix is required on the packaging machine' }
    $package=Join-Path $output 'RelaxDesk-keyboard-win-x64'
    $version='0.3.2-keyboard.'+$stamp+'.g'+$revision.Substring(0,7)
    $projects=[ordered]@{'RelaxDesk.Host'='Host';'RelaxDesk.Viewer'='Viewer';'RelaxDesk.Smoke'='Tools/Smoke';'RelaxDesk.Tests'='Tools/Tests'}
    foreach ($project in $projects.Keys) {
        dotnet publish (Join-Path $source "src/$project/$project.csproj") --artifacts-path (Join-Path $output 'artifacts') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false "-p:RelaxDeskNativePath=$native" -o (Join-Path $package $projects[$project])
        if ($LASTEXITCODE) { throw "Publish failed: $project" }
        $assets=Get-Content -Raw (Join-Path $output "artifacts/obj/$project/project.assets.json") | ConvertFrom-Json
        foreach($pack in @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object {$_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'})) {
            $v=$pack.version.Trim('[',']').Split(',')[0].Trim();$dest=Join-Path $package ('ThirdParty/'+$pack.name+'-'+$v);[IO.Directory]::CreateDirectory($dest)|Out-Null
            foreach($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $path=Join-Path $folder ($pack.name.ToLowerInvariant()+'/'+$v)
                if(Test-Path $path){Get-ChildItem $path -File | Where-Object {$_.Name -match 'LICENSE|NOTICE|THIRD.PARTY'} | Copy-Item -Destination $dest -Force}
            }
        }
    }
    $tools=Join-Path $package 'Tools/Native';[IO.Directory]::CreateDirectory($tools)|Out-Null;Copy-Item $native,$probe -Destination $tools
    Copy-Item (Join-Path $source 'packaging/Collect-Logs.cmd'),(Join-Path $source 'packaging/Collect-Logs.ps1') -Destination $package
    Copy-Item (Join-Path $source 'scripts') -Destination $package -Recurse
    Copy-Item (Join-Path $root 'docs/KEYBOARD_FIX.md') -Destination (Join-Path $package 'RUN-ME.zh-CN.md')
    $manifest=[pscustomobject]@{version=$version;revision=$revision;keyboard_patch_files=$files;native_baseline_sha256=(Get-FileHash $native -Algorithm SHA256).Hash;scope='Keyboard fix only; excludes concurrent parent HDR/UI changes';self_contained=$true;rid='win-x64';physical_chrome_ime='NOT_TESTED'}
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'package.json') -Encoding utf8
    $version | Set-Content (Join-Path $package 'BUILD-INFO.txt') -Encoding utf8
    $hashes=@(Get-ChildItem $package -File -Recurse | Sort-Object FullName | ForEach-Object {[pscustomobject]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}})
    $hashes | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $package 'SHA256SUMS.json') -Encoding utf8
    Compress-Archive -LiteralPath $package -DestinationPath ($package+'.zip') -CompressionLevel Optimal
    $hash=Get-FileHash ($package+'.zip') -Algorithm SHA256
    ($hash.Hash+' *'+[IO.Path]::GetFileName($package+'.zip')) | Set-Content ($package+'.zip.sha256') -Encoding ascii
    Write-Output "PACKAGE=$package.zip"
} finally { Pop-Location }
