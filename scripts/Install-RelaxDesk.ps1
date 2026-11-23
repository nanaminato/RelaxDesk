[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$PackageRoot=(Split-Path $PSScriptRoot -Parent),[string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs\RelaxDesk'),[switch]$DesktopShortcuts,[ValidateSet('Keep','Disabled','Window','ServiceWindow')][string]$LogonStartup='Keep')
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1') -Force
Install-RelaxDeskPackage -PackageRoot $PackageRoot -InstallRoot $InstallRoot -DesktopShortcuts:$DesktopShortcuts -LogonStartup $LogonStartup -WhatIf:$WhatIfPreference
