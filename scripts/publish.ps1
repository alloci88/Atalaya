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
if ($LASTEXITCODE -ne 0) { throw "El publish de Atalaya falló." }

# F11 — El relevo de actualización, junto al ejecutable.
#
# Va SIEMPRE, también en un publish local, para que la carpeta que sale de aquí tenga la misma
# forma que la que sale de una Release. Sin él, probar la actualización obligaría a recordar un
# segundo comando — y lo que hay que recordar, un día se olvida.
#
# Siempre self-contained y en un solo fichero, aunque Atalaya no lo sea: este exe se copia a
# %LOCALAPPDATA% y corre desde allí, fuera de la carpeta que va a sustituir, así que no puede
# depender de nada que viva en ella.
$updater = Join-Path $root "src/Atalaya.Updater/Atalaya.Updater.csproj"
Write-Host "Publishing AtalayaUpdater (self-contained, single file)..." -ForegroundColor Cyan
dotnet publish $updater `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -o (Join-Path $root $Output)
if ($LASTEXITCODE -ne 0) { throw "El publish del relevo falló." }

Write-Host "Done. Output in: $(Join-Path $root $Output)" -ForegroundColor Green
