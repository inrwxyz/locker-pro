<#
 Builds Locker (Manager, Uninstaller, Guard) and packs everything into one installer.

   .\build_package.ps1
   .\build_package.ps1 -CopyTo "$env:USERPROFILE\Downloads"

 Output: release\Locker-Setup.exe   (requires the .NET 10 SDK)
#>
param(
    [string]$CopyTo = ""
)

$ErrorActionPreference = "Stop"

$root         = $PSScriptRoot
$dist         = Join-Path $root "dist"
$installerDir = Join-Path $root "src\AppLocker.Installer"
$payloadZip   = Join-Path $installerDir "payload.zip"
$installerOut = Join-Path $root "installer_out"
$releaseDir   = Join-Path $root "release"

Write-Host "=== 1. Cleaning dist and payload ==="
if (Test-Path $dist) { Remove-Item -Path $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
if (Test-Path $payloadZip) { Remove-Item -Path $payloadZip -Force }

Write-Host "=== 2. Publishing Manager (Locker.exe) ==="
dotnet publish (Join-Path $root "src\AppLocker.Manager") -c Release -o $dist --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Manager build failed" }

Write-Host "=== 3. Publishing Uninstaller (uninstall.exe) ==="
dotnet publish (Join-Path $root "src\AppLocker.Uninstaller") -c Release -o $dist --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Uninstaller build failed" }

Write-Host "=== 4. Publishing Watchdog (LockerGuard.exe) ==="
dotnet publish (Join-Path $root "src\AppLocker.Guard") -c Release -o $dist --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Guard build failed" }

Write-Host "=== 5. Copying icon and cleaning extra files ==="
Copy-Item (Join-Path $root "src\lock.ico") -Destination (Join-Path $dist "lock.ico") -Force
Get-ChildItem -Path $dist -Filter "*.bat" | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $dist -Filter "*.reg" | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $dist -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "=== 6. Creating payload.zip ==="
Compress-Archive -Path "$dist\*" -DestinationPath $payloadZip -Force

Write-Host "=== 7. Publishing standalone Locker-Setup.exe ==="
if (Test-Path $installerOut) { Remove-Item -Path $installerOut -Recurse -Force }
dotnet publish $installerDir -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $installerOut
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$setupExe = Join-Path $installerOut "Locker-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "Locker-Setup.exe was not found in $installerOut" }

if (-not (Test-Path $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir | Out-Null }
Copy-Item $setupExe -Destination (Join-Path $releaseDir "Locker-Setup.exe") -Force
Write-Host "SUCCESS: $releaseDir\Locker-Setup.exe"

if ($CopyTo -ne "") {
    if (-not (Test-Path $CopyTo)) { New-Item -ItemType Directory -Path $CopyTo | Out-Null }
    Copy-Item $setupExe -Destination (Join-Path $CopyTo "Locker-Setup.exe") -Force
    Write-Host "Also copied to $CopyTo\Locker-Setup.exe"
}
