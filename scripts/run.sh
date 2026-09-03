#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore src/RayLink.App/RayLink.App.csproj
dotnet run --project src/RayLink.App/RayLink.App.csproj
