param([Parameter(Mandatory=$true)][string]$Dest)
$ErrorActionPreference = "Stop"
$s = $PSScriptRoot

Get-Process Atalaya -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

foreach ($t in @("dark","light")) {
    Write-Host "=== $t pantalla completa ===" -ForegroundColor Cyan
    & "$s\tour.ps1" -Theme $t -Width 1920 -Height 1080 -Maximized -Out "$Dest\$t-completa"
    Write-Host "=== $t 1280x720 ===" -ForegroundColor Cyan
    & "$s\tour.ps1" -Theme $t -Width 1280 -Height 720 -Out "$Dest\$t-1280"
}
Write-Host "TODO LISTO" -ForegroundColor Green
