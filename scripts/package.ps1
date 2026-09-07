[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
$cargoDirectory = Join-Path $env:USERPROFILE ".cargo\\bin"

# Rustup does not update PATH in PowerShell windows that were already open.
if (Test-Path (Join-Path $cargoDirectory "cargo.exe")) {
    $env:Path = "$cargoDirectory;$env:Path"
}

& (Join-Path $PSScriptRoot "build-windows-installer.ps1") -Configuration $Configuration -Runtime "win-x64"
if ($LASTEXITCODE -ne 0) { throw "AgentLink packaging failed." }

$installer = Join-Path $projectRoot "artifacts\\AgentLink-Setup\\AgentLink.Setup.exe"
if (-not (Test-Path $installer)) { throw "Installer not found: $installer" }
Write-Host "Installer: $installer"
