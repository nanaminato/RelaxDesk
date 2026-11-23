[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$PackageRoot=(Split-Path $PSScriptRoot -Parent),[string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs\RelaxDesk'),[switch]$DesktopShortcuts)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1') -Force
Install-RelaxDeskPackage -PackageRoot $PackageRoot -InstallRoot $InstallRoot -DesktopShortcuts:$DesktopShortcuts -WhatIf:$WhatIfPreference
