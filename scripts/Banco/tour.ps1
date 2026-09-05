<#
  UI-AUDIT-1 — recorrido COMPLETO de las vistas sobre el dist real.

  Deriva de tourc.ps1 de F26 §C (D-977, D-983 §1): mismo arranque, misma copia de seguridad del
  settings.json real antes de tocarlo y misma restauracion al terminar. Lo que cambia es que aqui
  se recorren TODAS las vistas de una pasada, no las de una parte.

  Lo que este recorrido NO alcanza y por eso lo cubre el banco del agente falso (Atalaya.Shots):
  Sesion en vivo, Arreglo asistido, cierre del arreglo, Ultima sesion y los cinco dialogos.
#>
param(
    [ValidateSet("dark","light")][string]$Theme = "dark",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [switch]$Maximized,
    [Parameter(Mandatory=$true)][string]$Out,
    # Por defecto, el dist DE ESTE repositorio: el banco vive dentro y no tiene por que
    # saber donde esta clonado (P-28).
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\dist\Atalaya.exe")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @'
using System;using System.Drawing;using System.Runtime.InteropServices;
public class T {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(250);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static void Place(IntPtr h, int w, int hh) {
        ShowWindow(h, 9); SetWindowPos(h, IntPtr.Zero, 40, 40, w, hh, 0x0040 | 0x0010); SetForegroundWindow(h);
    }
    public static void Focus(IntPtr h) { SetForegroundWindow(h); }
    // La foto se toma de la PANTALLA y no de la ventana: los popups (el «…» del portafolio, un
    // desplegable) viven en su propia ventana por encima, y una captura de la ventana sola los
    // deja fuera — que es justo lo que se quiere mirar.
    public static void Shot(IntPtr h, string path, int pad) {
        RECT r; GetWindowRect(h, out r);
        int w = r.Right - r.Left + pad, hh = r.Bottom - r.Top + pad;
        using (var b = new Bitmap(w, hh)) using (var g = Graphics.FromImage(b)) {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, hh));
            b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@ -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Windows.Forms

$UIA = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$CT  = [System.Windows.Automation.ControlType]
$INV = [System.Windows.Automation.InvokePattern]

$script:cfgPath   = "$env:LOCALAPPDATA\Atalaya\settings.json"
$script:cfgBackup = Join-Path $env:TEMP "atalaya-settings-antes-del-tour.json"

function Backup-Config {
    if (-not (Test-Path $script:cfgBackup)) { Copy-Item $script:cfgPath $script:cfgBackup -Force }
}

function Restore-Config {
    if (Test-Path $script:cfgBackup) {
        Copy-Item $script:cfgBackup $script:cfgPath -Force
        Remove-Item $script:cfgBackup -Force
    }
}

function Set-Config {
    Backup-Config
    $p = $script:cfgPath
    $j = Get-Content $p -Raw | ConvertFrom-Json
    $j.theme = $Theme
    $j.window.saved = $true
    $j.window.maximized = [bool]$Maximized
    $j.window.left = 40; $j.window.top = 40
    $j.window.width = $Width; $j.window.height = $Height
    $j.window.railCollapsed = $false; $j.window.railPinned = $false
    $j | ConvertTo-Json -Depth 20 | Set-Content $p -Encoding utf8
}

# Con REINTENTOS. Al final del recorrido el arbol de la ventana es enorme --el lector de informes
# sigue vivo aunque se oculte-- y una sola busqueda vuelve vacia aunque el boton este ahi: las
# cinco secciones de Ajustes se «perdian» justo despues de abrir dos informes, y aparecian solas
# al preguntar otra vez.
function Find-Btn($root, [string]$name) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::Button)))
    for ($k = 0; $k -lt 4; $k++) {
        $el = $root.FindFirst($TS::Descendants, $c)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 900
    }
    return $null
}

function Click([string]$name, [int]$wait = 1500) {
    $el = Find-Btn $script:root $name
    if ($null -eq $el) { Write-Host "   (no encontrado: $name)" -ForegroundColor DarkYellow; return $false }
    $el.GetCurrentPattern($INV::Pattern).Invoke()
    Start-Sleep -Milliseconds $wait
    return $true
}

# Igual que Click, pero con el RATON sobre el centro del control. Hace falta para lo que abre una
# ventana modal —Invoke() se queda esperando a que la modal se cierre— y para los ToggleButton,
# que no tienen patron Invoke.
function ClickMouse([string]$name, [int]$wait = 1500, [string]$type = "Button") {
    $ct = if ($type -eq "Toggle") { $CT::Button } else { $CT::Button }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $ct)))
    $el = $script:root.FindFirst($TS::Descendants, $c)
    if ($null -eq $el) { Write-Host "   (no encontrado: $name)" -ForegroundColor DarkYellow; return $false }
    $rc = $el.Current.BoundingRectangle
    [T]::ClickAt([int]($rc.Left + $rc.Width / 2), [int]($rc.Top + $rc.Height / 2))
    Start-Sleep -Milliseconds $wait
    return $true
}

# El «…» de una tarjeta del portafolio: es un ToggleButton sin rotulo, asi que se busca por su
# tooltip a traves del HelpText, y si no, por geometria (el boton mas pequeno de la tarjeta).
function ClickMore([int]$wait = 1200) {
    $c = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::Button)
    $all = @($script:root.FindAll($TS::Descendants, $c))
    $r = New-Object T+RECT
    [void][T]::GetWindowRect($script:hwnd, [ref]$r)
    foreach ($b in $all) {
        if ($b.Current.HelpText -eq "Más acciones" -or $b.Current.Name -eq "Más acciones") {
            $rc = $b.Current.BoundingRectangle
            [T]::ClickAt([int]($rc.Left + $rc.Width / 2), [int]($rc.Top + $rc.Height / 2))
            Start-Sleep -Milliseconds $wait
            return $true
        }
    }
    Write-Host "   (no encontrado: el «…» de la tarjeta)" -ForegroundColor DarkYellow
    return $false
}

# Pulsa la enesima fila ANCHA de la lista de contenido. La primera lista del arbol es el rail —sus
# entradas tambien son botones—, asi que buscar «la primera» abria Portafolio en vez de un informe.
function ClickNth([int]$n, [int]$wait = 1500) {
    $listCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::List)
    $lists = $script:root.FindAll($TS::Descendants, $listCond)
    $r = New-Object T+RECT
    [void][T]::GetWindowRect($script:hwnd, [ref]$r)
    $list = $null; $best = 0
    foreach ($l in $lists) {
        $rc = $l.Current.BoundingRectangle
        if ([double]::IsInfinity($rc.Width) -or [double]::IsInfinity($rc.Height)) { continue }
        if ($rc.Left -le ($r.Left + 260)) { continue }
        $area = $rc.Width * $rc.Height
        if ($area -gt $best) { $best = $area; $list = $l }
    }
    if ($null -eq $list) { Write-Host "   (no hay lista de contenido)" -ForegroundColor DarkYellow; return $false }
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::Button)
    $all = @($list.FindAll($TS::Descendants, $btnCond) | Where-Object { $_.Current.BoundingRectangle.Width -gt 600 })
    if ($all.Count -le $n) { Write-Host "   (solo $($all.Count) filas anchas)" -ForegroundColor DarkYellow; return $false }
    $all[$n].GetCurrentPattern($INV::Pattern).Invoke()
    Start-Sleep -Milliseconds $wait
    return $true
}

function ClickPoint([int]$x, [int]$y, [int]$wait = 2500) {
    $r = New-Object T+RECT
    [void][T]::GetWindowRect($script:hwnd, [ref]$r)
    [T]::ClickAt($r.Left + $x, $r.Top + $y)
    Start-Sleep -Milliseconds $wait
}

function Shot([string]$name, [int]$pad = 0) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(2, 2)
    Start-Sleep -Milliseconds 600
    [T]::Shot($script:hwnd, (Join-Path $Out "$name.png"), $pad)
    Write-Host "   -> $name"
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null
Set-Config
Get-Process Atalaya -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$proc = Start-Process $Exe -PassThru
$script:hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 700
    $q = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -ne $q -and $q.MainWindowHandle -ne [IntPtr]::Zero) { $script:hwnd = $q.MainWindowHandle; break }
}
if ($script:hwnd -eq [IntPtr]::Zero) { throw "La ventana de Atalaya no aparecio." }
Start-Sleep -Seconds 4
if (-not $Maximized) { [T]::Place($script:hwnd, $Width, $Height) } else { [T]::Focus($script:hwnd) }
Start-Sleep -Milliseconds 1800
$script:root = $UIA::FromHandle($script:hwnd)

# ============================================================================ PORTAFOLIO
Write-Host "Portafolio"
Click "Portafolio" 2500 | Out-Null
Shot "01-portafolio"

Write-Host "Nueva aplicacion"
if (Click "+ Nueva aplicación" 3000) { Shot "02-nueva-aplicacion" }

# Las vistas de SISTEMA van primero, y no es capricho. El lector de informes sigue vivo en el
# arbol aunque se oculte (ReportsView §83), y despues de abrir dos informes la busqueda por
# automatizacion deja de encontrar las cinco secciones de Ajustes --con reintentos y todo-- aunque
# esten pintadas en la captura. Es una limitacion del banco, no de la aplicacion: se recorren
# antes de que el arbol crezca.
# ============================================================================ SISTEMA
Write-Host "Metricas"
Click "Métricas" 4000 | Out-Null
Shot "08-metricas"

Write-Host "Cuenta"
Click "Cuenta" 2500 | Out-Null
Shot "09-cuenta"

Write-Host "Acerca de"
if (Click "Acerca de" 2200) { Shot "10-acerca-de" }

Write-Host "Ajustes"
Click "Ajustes" 2500 | Out-Null
Shot "11-ajustes"
foreach ($s in @(@("Auditoría","11b-ajustes-auditoria"),
                 @("Tarifas","11c-ajustes-tarifas"),
                 @("Apariencia","11d-ajustes-apariencia"),
                 @("Avanzado","11e-ajustes-avanzado"))) {
    if (Click $s[0] 1600) { Shot $s[1] }
}

# ============================================================================ INVENTARIO
Write-Host "Inventario"
Click "Portafolio" 2200 | Out-Null
if (Click "Abrir inventario" 4000) {
    Shot "03-inventario"
    if (Click "Resumen del ciclo" 1600) {
        Shot "03b-inventario-cajon"
        Click "Cerrar" 1200 | Out-Null
    }
}

# ============================================================================ HALLAZGOS
Write-Host "Hallazgos"
Click "Hallazgos" 3000 | Out-Null
Shot "04-hallazgos"

Write-Host "Ficha de un hallazgo"
Click "Expandir todo" 2500 | Out-Null
ClickPoint 700 460 3500
Shot "05-hallazgo-ficha"

# ============================================================================ INFORMES
Write-Host "Informes"
Click "Informes" 3000 | Out-Null
Shot "06-informes"

Write-Host "Informe abierto"
if (ClickNth 0 3000) { Shot "07-informe-abierto" }

# Se vuelve por el RAIL y no por un «← Volver» dentro del informe: ese boton ya no existe. Lo
# retiro F27 con la raiz 4 (UI-0058) porque la miga hace ya ese trabajo y habia dos vueltas atras
# distintas en la misma pantalla. Mientras el banco lo siguio buscando, esta captura no se tomo.
Write-Host "Informe con hallazgos"
Click "Informes" 1800 | Out-Null
if (ClickNth 2 3000) { Shot "07b-informe-con-hallazgos" }

# ============================================================================ EL «…» Y EL RAIL
# El menu «…» se deja PARA EL FINAL a proposito: abrirlo deja las cinco secciones de Ajustes fuera
# del arbol de accesibilidad --es un hallazgo de esta auditoria, no una rareza del banco-- y con
# ellas fuera no hay forma de pedirlas por su nombre. Ponerlo al final captura lo mismo sin que se
# lleve por delante el resto del recorrido.
Write-Host "El «…» de la tarjeta"
Click "Portafolio" 2200 | Out-Null
if (ClickMore 1200) {
    # El popup se pinta FUERA de la ventana por abajo cuando la tarjeta esta al pie: se pide un
    # margen extra para no recortar el menu que se acaba de abrir.
    Shot "01b-portafolio-mas" 80
    # Se cierra volviendo a pulsar el propio «…». La primera version cerraba el popup con un clic
    # «en cualquier sitio» a 40,90 --que es exactamente donde vive el plegado del rail-- y el rail
    # se quedaba plegado el resto del recorrido.
    ClickMore 800 | Out-Null
}

Write-Host "Rail plegado"
Click "Portafolio" 2000 | Out-Null
if (Click "Plegar o desplegar el menú" 1500) {
    Shot "12-rail-plegado"
    Click "Plegar o desplegar el menú" 1200 | Out-Null
}

Get-Process Atalaya -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Restore-Config
Write-Host "Listo: $Out" -ForegroundColor Green
