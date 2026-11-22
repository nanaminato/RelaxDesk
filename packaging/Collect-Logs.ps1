param([switch]$DiagnosticOnly, [ValidateRange(60,1800)][int]$Frames=300, [switch]$VerifyOnly)
$ErrorActionPreference='Stop'
try {
    $root=$PSScriptRoot
    $manifest=Get-Content -Raw (Join-Path $root 'SHA256SUMS.json') | ConvertFrom-Json
    foreach ($entry in $manifest) {
        $file=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if (!$file.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package hash mismatch: $($entry.path)" }
    }
    Write-Host "PASS package SHA256 verification: $($manifest.Count) files"
    if ($VerifyOnly) { exit 0 }
    & "$root/scripts/accept-home.ps1" -PackageRoot $root -DiagnosticOnly:$DiagnosticOnly -Frames $Frames
    exit $LASTEXITCODE
} catch {
    Write-Host $_ -ForegroundColor Red
    # Failure remains visible when launched by the .cmd double-click entry.
    exit 1
}
