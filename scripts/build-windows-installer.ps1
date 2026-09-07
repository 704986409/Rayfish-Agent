[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$appProject = "src\\RayLink.App\\RayLink.App.csproj"
$transportProject = "native\\iroh-transport\\Cargo.toml"
$setupProject = "installer\\RayLink.Setup\\RayLink.Setup.csproj"
$appOutput = "artifacts\\win-x64-single"
$setupOutput = "artifacts\\AgentLink-Setup"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust Cargo was not found. Install Rust 1.91 or later, then run this script again."
}

Write-Host "[1/3] Building native Iroh transport..."
cargo build --release --manifest-path $transportProject --target x86_64-pc-windows-msvc
if ($LASTEXITCODE -ne 0) { throw "Iroh transport build failed." }
$transportBinary = "native\\iroh-transport\\target\\x86_64-pc-windows-msvc\\release\\raylink-iroh-transport.exe"
if (-not (Test-Path $transportBinary)) { throw "Iroh transport output missing: $transportBinary" }

Write-Host "[2/3] Publishing AgentLink..."
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "AgentLink publish failed." }
Copy-Item $transportBinary (Join-Path $appOutput "AgentLink.Transport.exe") -Force

Write-Host "[3/3] Publishing AgentLink.Setup..."
dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:IncludeAllContentForSelfExtract=false -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }
$installer = Join-Path $setupOutput "AgentLink.Setup.exe"
if (-not (Test-Path $installer)) { throw "Installer output missing: $installer" }
Get-Item $installer | Format-List FullName,Length,LastWriteTime
Get-FileHash $installer -Algorithm SHA256
