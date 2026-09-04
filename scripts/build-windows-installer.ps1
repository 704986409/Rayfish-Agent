[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$appProject = "src\RayLink.App\RayLink.App.csproj"
$transportProject = "native\iroh-transport\Cargo.toml"
$setupProject = "installer\RayLink.Setup\RayLink.Setup.csproj"
$appOutput = "artifacts\win-x64-single"
$setupOutput = "artifacts\RayLink-Setup"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "未找到 Rust Cargo。请安装 Rust 1.91+，然后重新执行此脚本。"
}
Write-Host "[1/3] Building native Iroh transport..."
cargo build --release --manifest-path $transportProject --target x86_64-pc-windows-msvc
if ($LASTEXITCODE -ne 0) { throw "Iroh transport build failed." }
$transportBinary = "native\iroh-transport\target\x86_64-pc-windows-msvc\release\raylink-iroh-transport.exe"
if (-not (Test-Path $transportBinary)) { throw "Iroh transport output missing: $transportBinary" }

Write-Host "[2/3] Publishing RayLink..."
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "RayLink publish failed." }
Copy-Item $transportBinary (Join-Path $appOutput "RayLink.Transport.exe") -Force

Write-Host "[3/3] Publishing RayLink.Setup..."
dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:IncludeAllContentForSelfExtract=false -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }
$installer = Join-Path $setupOutput "RayLink.Setup.exe"
if (-not (Test-Path $installer)) { throw "Installer output missing: $installer" }
Get-Item $installer | Format-List FullName,Length,LastWriteTime
Get-FileHash $installer -Algorithm SHA256
