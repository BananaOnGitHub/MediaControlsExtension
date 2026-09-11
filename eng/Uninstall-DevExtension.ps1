<#
.SYNOPSIS
Unregisters the development Media Controls package so you can reinstall from the Microsoft Store.
#>
[CmdletBinding()]
param()

$existingPkg = Get-AppxPackage -Name JiriPolasek.MediaControlsForCmdPal | Select-Object -First 1
if ($existingPkg) {
    Write-Host "Unregistering development package '$($existingPkg.PackageFullName)'..." -ForegroundColor Yellow
    Remove-AppxPackage -Package $existingPkg.PackageFullName -PreserveApplicationData
    Write-Host "Development package removed. You can now reinstall Media Controls from the Microsoft Store or winget." -ForegroundColor Green
} else {
    Write-Host "No Media Controls package is currently registered." -ForegroundColor Gray
}
