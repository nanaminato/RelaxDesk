[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory)][string]$InstallRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'RelaxDesk.Setup.psm1') -Force
Uninstall-RelaxDeskPackage -InstallRoot $InstallRoot -WhatIf:$WhatIfPreference
