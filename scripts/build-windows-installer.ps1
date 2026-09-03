[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$appProject = "src\RayLink.App\RayLink.App.csproj"
$setupProject = "installer\RayLink.Setup\RayLink.Setup.csproj"
$appOutput = "artifacts\win-x64-single"
$setupOutput = "artifacts\RayLink-Setup"

Write-Host "[1/2] Publishing RayLink..."
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "RayLink publish failed." }

$rayfishMsi = "third-party\rayfish\ray-windows-x86_64.msi"
if (-not (Test-Path $rayfishMsi)) { throw "Missing embedded Rayfish MSI: $rayfishMsi" }
$hash = (Get-FileHash $rayfishMsi -Algorithm SHA256).Hash
Write-Host "Rayfish MSI SHA-256: $hash"

Write-Host "[2/2] Publishing RayLink.Setup..."
dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:IncludeAllContentForSelfExtract=false `
    -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }

$installer = Join-Path $setupOutput "RayLink.Setup.exe"
if (-not (Test-Path $installer)) { throw "Installer output missing: $installer" }
Get-Item $installer | Format-List FullName,Length,LastWriteTime
Get-FileHash $installer -Algorithm SHA256
