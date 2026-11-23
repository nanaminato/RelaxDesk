param([switch]$DiagnosticOnly, [ValidateRange(60,1800)][int]$Frames=300, [switch]$VerifyOnly)
$ErrorActionPreference='Stop'
try {
    $root=$PSScriptRoot
    & "$root/Tools/Smoke/RelaxDesk.Smoke.exe" --collect-package $root --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Package integrity verification failed; preserve the verifier error above.' }
    if ($VerifyOnly) { exit 0 }
    & "$root/scripts/accept-home.ps1" -PackageRoot $root -DiagnosticOnly:$DiagnosticOnly -Frames $Frames
    exit $LASTEXITCODE
} catch {
    Write-Host $_ -ForegroundColor Red
    # Failure remains visible when launched by the .cmd double-click entry.
    exit 1
}
