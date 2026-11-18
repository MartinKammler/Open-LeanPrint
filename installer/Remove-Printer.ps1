<#
.SYNOPSIS
    Removes the Open-LeanPrint virtual printer. Run by the uninstaller.

.DESCRIPTION
    Leaving the queue behind after uninstalling would mean print jobs vanishing
    into a printer with nothing behind it, so uninstalling takes it away again.
    Needs administrator rights, which the uninstaller has.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "SilentlyContinue"

# This product's queue, under its current name or the one from before the
# rename - not every printer that happens to contain the word.
Get-Printer | Where-Object { $_.Name -like "Open-LeanPrint*" -or $_.Name -like "OpenLeanPrint*" } | ForEach-Object {
    Write-Host "Removing printer: $($_.Name)"
    Remove-Printer -Name $_.Name
}
exit 0
