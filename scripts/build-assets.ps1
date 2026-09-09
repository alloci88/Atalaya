<#
.SYNOPSIS
  Regenera los assets de identidad de Atalaya (F6.4) desde sus fuentes.

.DESCRIPTION
  Lee de assets/:
    · atalaya-icon.svg        el icono, para 32/48/64/256 px
    · atalaya-icon-small.svg  la variante de silueta, para 16/24 px

  Y escribe en assets/:
    · atalaya.ico             multi-tamaño (16, 24, 32, 48, 64, 256)

  Es determinista: sin cambios en las fuentes, los ficheros salen idénticos y git no ve
  nada. Ejecútalo cuando toques un SVG; el .ico va versionado para que compilar no
  dependa de tener esta herramienta a mano.

  Ya no prepara ningún logotipo corporativo: la marca de la organización donde nació
  Atalaya se fue con ella (F38), y lo que identifica hoy a un despliegue es el NOMBRE de
  su organización, que sale del hub y no de assets/.

.EXAMPLE
  pwsh scripts/build-assets.ps1
#>
param(
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $PSScriptRoot "IconGen/IconGen.csproj"

Write-Host "Regenerando assets de identidad..." -ForegroundColor Cyan
dotnet run --project $tool --configuration Release -- $root
if ($LASTEXITCODE -ne 0) { throw "IconGen falló con código $LASTEXITCODE" }

if ($Verify) {
    # Para CI: comprueba que lo versionado coincide con lo que producen las fuentes.
    $dirty = git -C $root status --porcelain -- assets
    if ($dirty) {
        Write-Host "Los assets versionados NO coinciden con sus fuentes:" -ForegroundColor Red
        Write-Host $dirty
        exit 1
    }
    Write-Host "Los assets versionados coinciden con sus fuentes." -ForegroundColor Green
}

Write-Host "Hecho. Assets en: $(Join-Path $root 'assets')" -ForegroundColor Green
