# El banco de capturas

Con qué se mira Atalaya antes de dar una vista por buena. Son dos piezas y hacen dos mitades
distintas del mismo trabajo:

| Pieza | Qué fotografía | Contra qué |
|---|---|---|
| `tour.ps1` | Las 18 vistas del recorrido, en las cuatro combinaciones | El `dist` publicado, con los datos reales de quien lo corre |
| `Atalaya.Shots/` | Las cuatro vistas densas y los nueve diálogos | La carcasa montada en un proceso propio, contra el agente falso y un hub temporal |

## Por qué está en el repositorio

**Contradice D-977**, que decidió que el banco «vive en el scratchpad y no en el repositorio: es un
instrumento de esta fase, no producto». El argumento nuevo (P-28, UI-AUDIT-1) es que **ya no es de
una fase**: se ha necesitado en F26 §B, en F26 §C y otra vez en la auditoría de la interfaz, y esa
tercera vez hubo que ir a rescatarlo del scratchpad de otra sesión y volver a arreglarle tres
cosas —el orden del recorrido, el clic que plegaba el raíl, los reintentos de la búsqueda—.

Lo que D-977 dijo con razón es que el banco no es **producto**. `scripts/` no es producto, y ahí ya
viven `PromptBench` y `IconGen` por exactamente el mismo motivo.

Los PNG **no** entran: los ignora el `.gitignore` de esta carpeta. Lo que se versiona es el
instrumento; las capturas de una fase concreta van a `docs/design/<fase>/` cuando son evidencia de
algo que se decidió, y a la basura cuando no.

## El recorrido sobre el `dist` — `tour.ps1`

```powershell
# Una combinación
scripts\Banco\tour.ps1 -Theme dark -Width 1920 -Height 1080 -Maximized -Out C:\tmp\dark-completa

# Las cuatro de una vez
scripts\Banco\tour-todo.ps1 -Dest C:\tmp\banco
```

Necesita el `dist` construido (`scripts\publish.ps1`). Por defecto usa el `dist` de este mismo
repositorio; `-Exe` apunta a otro.

**Toca el `settings.json` real y lo devuelve.** Para fotografiar los dos temas hay que cambiar el
tema, y el tema es un ajuste del usuario. El script copia
`%LOCALAPPDATA%\Atalaya\settings.json` antes de tocarlo y lo restaura al terminar, también si algo
falla por el camino. Si una ejecución se corta a lo bruto, la copia se queda en
`%TEMP%\atalaya-settings-antes-del-tour.json`.

**Las capturas son de la PANTALLA, no de la ventana.** Los desplegables —el «···» de una tarjeta,
un combo abierto— viven en su propia ventana por encima, y una captura de la ventana sola los deja
fuera, que es justo lo que se quiere mirar.

## Las vistas densas y los diálogos — `Atalaya.Shots`

```powershell
dotnet run --project scripts\Banco\Atalaya.Shots -- C:\tmp\banco\densas
```

**Por qué existe.** Sesión en vivo, Arreglo asistido, el cierre del arreglo y Última sesión piden
una sesión de varias unidades y una conversación viva; lanzarlas de verdad gasta los créditos del
usuario y escribe en el hub del equipo. Y los diálogos no se pueden abrir desde el `dist` sin
mentirle al usuario: «Vincular clon local» solo aparece cuando el clon está roto, y dos de los
otros borran de verdad.

Lo que monta es la **misma** carcasa —`MainWindow` con su `MainViewModel`, los mismos diccionarios
de tema— sobre un hub temporal y el agente falso que ya usa la suite. La vista es la de verdad; los
datos son de mentira. **Lo que el agente falso no produce se dice en el parte y no se dibuja a
mano.**

**No está en `Atalaya.sln`**, igual que `PromptBench`: no es producto ni suite, y meterlo en la
solución haría que cada build de la aplicación arrastrara una ventana que solo se abre a mano.

## Los diálogos se fotografían una vez por tema, y no cuatro

Los nueve llevan ancho fijo declarado (520–860 px) y `ResizeMode="NoResize"` o alto fijo, así que
no cambian con el tamaño de la ventana. Cuatro capturas por diálogo serían cuatro copias del mismo
píxel.

## Y la escala al 150 % no tiene carpeta propia

WPF dispone en DIP: una pantalla de 1920×1080 al 150 % da exactamente 1280×720 DIP de lienzo. La
disposición, los recortes y los saltos de columna al 150 % son los de las capturas `1280`, con más
densidad de píxel. Donde un hallazgo dice «1280×720 (= 150 %)» quiere decir las dos cosas.
