[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\RayLink.App\RayLink.App.csproj'
dotnet restore $project
dotnet run --project $project
