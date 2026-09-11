<#
.SYNOPSIS
Deploys the locally built Media Controls extension into PowerToys Command Palette for testing.
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $SkipPublish) {
    Write-Host "Publishing MediaControlsExtension (Release win-x64)..." -ForegroundColor Cyan
    & dotnet publish "$repoRoot\src\MediaControlsExtension\JPSoftworks.MediaControlsExtension.csproj" /p:PublishProfile=win-x64 -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE."
    }
}

$devAppX = "$repoRoot\artifacts\DevAppX"
Write-Host "Staging unpacked development package to '$devAppX'..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$devAppX\Assets" | Out-Null
New-Item -ItemType Directory -Force -Path "$devAppX\JPSoftworks.MediaControlsExtension" | Out-Null
Copy-Item -Path "$repoRoot\src\MediaControlsExtension.Package\Assets\*" -Destination "$devAppX\Assets" -Recurse -Force
Copy-Item -Path "$repoRoot\src\MediaControlsExtension\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\*" -Destination "$devAppX\JPSoftworks.MediaControlsExtension" -Recurse -Force

# Check existing package
$existingPkg = Get-AppxPackage -Name JiriPolasek.MediaControlsForCmdPal | Select-Object -First 1
if ($existingPkg -and (Test-Path "$($existingPkg.InstallLocation)\resources.pri")) {
    Copy-Item -Path "$($existingPkg.InstallLocation)\resources.pri" -Destination "$devAppX\resources.pri" -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$($existingPkg.InstallLocation)\AppxManifest.xml" -Destination "$devAppX\AppxManifest.xml" -Force -ErrorAction SilentlyContinue
}

# Ensure AppxManifest.xml exists
if (-not (Test-Path "$devAppX\AppxManifest.xml")) {
    Copy-Item -Path "$repoRoot\src\MediaControlsExtension.Package\Package.appxmanifest" -Destination "$devAppX\AppxManifest.xml" -Force
    (Get-Content "$devAppX\AppxManifest.xml") -replace '\$targetnametoken\$', 'JPSoftworks.MediaControlsExtension\JPSoftworks.MediaControlsExtension' | Set-Content "$devAppX\AppxManifest.xml"
}

# Stop any running instances of MediaControlsExtension
Get-Process -Name "JPSoftworks.MediaControlsExtension" -ErrorAction SilentlyContinue | Stop-Process -Force

if ($existingPkg) {
    if ($existingPkg.IsDevelopmentMode) {
        Write-Host "Unregistering existing development package '$($existingPkg.PackageFullName)' (preserving app data)..." -ForegroundColor Yellow
        Remove-AppxPackage -Package $existingPkg.PackageFullName -PreserveApplicationData
    } else {
        Write-Host "Removing existing Store package '$($existingPkg.PackageFullName)'..." -ForegroundColor Yellow
        Remove-AppxPackage -Package $existingPkg.PackageFullName
    }
}

Write-Host "Registering unpacked development package..." -ForegroundColor Green
$manifestPath = (Resolve-Path "$devAppX\AppxManifest.xml").Path
Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown

Write-Host "`nSuccessfully installed development extension into Command Palette!" -ForegroundColor Green
Write-Host "Open PowerToys Command Palette to test the new iTunes integration." -ForegroundColor Cyan
