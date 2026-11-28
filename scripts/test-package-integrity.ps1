param([Parameter(Mandatory)][string]$PackageRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$package=[IO.Path]::GetFullPath($PackageRoot)
$smoke=Join-Path $package 'Tools/Smoke/RelaxDesk.Smoke.exe'
$fixture=Join-Path $root ('build/integrity-fixture-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$file=Join-Path $fixture 'sample.txt'; $manifest=Join-Path $fixture 'SHA256SUMS.json'
function Expect-Code([int]$Expected,[string[]]$Extra=@('--verify-only')) {
    & $smoke --collect-package $fixture @Extra | Out-Host
    if ($LASTEXITCODE -ne $Expected) { throw "Expected $Expected, got $LASTEXITCODE" }
}
'original' | Set-Content -LiteralPath $file
$entry=@([pscustomobject]@{path='sample.txt';sha256=(Get-FileHash $file -Algorithm SHA256).Hash;bytes=(Get-Item $file).Length})
ConvertTo-Json -InputObject $entry | Set-Content $manifest
Expect-Code 0
'corrupted' | Set-Content -LiteralPath $file
Expect-Code 1
'[]' | Set-Content $manifest
Expect-Code 1
ConvertTo-Json -InputObject @([pscustomobject]@{path='../outside.txt';sha256='00';bytes=0}) | Set-Content $manifest
Expect-Code 1
Expect-Code 1 @('--unsupported-option')
Write-Output 'PASS 5 packaged collector integrity/CLI contracts: valid, corruption, empty manifest, path escape, unknown option'
exit 0
