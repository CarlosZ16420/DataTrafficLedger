[CmdletBinding(SupportsShouldProcess=$true)]
param([switch]$KeepApplicationFiles,[switch]$DeleteData,[switch]$Quiet)
# Compatibility entry point; both layouts share the verified cleanup implementation.
& (Join-Path $PSScriptRoot 'Uninstall.ps1') @PSBoundParameters
exit $LASTEXITCODE
