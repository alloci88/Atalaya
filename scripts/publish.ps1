<#
.SYNOPSIS
  Publishes Atalaya (the WPF app) to a distributable folder.
.PARAMETER SelfContained
  Bundle the .NET runtime (no runtime install required on the target machine).
.EXAMPLE
  pwsh scripts/publish.ps1
  pwsh scripts/publish.ps1 -SelfContained
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release",
    [string]$Output = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/Atalaya.App/Atalaya.App.csproj"

$args = @(
    "publish", $proj,
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", (Join-Path $root $Output),
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=false"
)

Write-Host "Publishing Atalaya ($Configuration, self-contained=$SelfContained)..." -ForegroundColor Cyan
dotnet @args
Write-Host "Done. Output in: $(Join-Path $root $Output)" -ForegroundColor Green
