param([string]$BuildDirectory = 'build/native')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Install stable Visual Studio C++ Build Tools and Windows SDK.' }
$cmake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$build = Join-Path $root $BuildDirectory
& $cmake -S (Join-Path $root 'src/RelaxDesk.Native') -B $build -A x64
if ($LASTEXITCODE) { throw 'Native configure failed' }
& $cmake --build $build --config Release --parallel
if ($LASTEXITCODE) { throw 'Native build failed' }
