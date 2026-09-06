# UI-AUDIT-1 — Atalaya se audita a sí misma, esta vez la interfaz

Auditoría de la interfaz tal como quedó tras **F26 partes A, B y C**, más las revisiones R2 y R3
(D-983, D-997, D-999, D-1000). **No se ha corregido nada**: ni un margen, ni un test, ni una línea
de código. El árbol solo lleva esta carpeta.

Build y suite en verde al empezar y al terminar: **2.399 tests, 0 fallos**.

---

## Cómo se auditó

**Tres auditores con tres lupas**, cada uno recorriendo todas las vistas por su cuenta y sin ver
las listas de los otros; después, una consolidación que funde las tres, marca los duplicados y
verifica lo verificable.

| Lupa | Qué mira |
|---|---|
| **1 — Legibilidad y densidad** | Tipografía, contraste, espacio muerto, textos recortados, alineación, escala al 150 % |
| **2 — Navegación y estado** | Pasos, qué se conserva al volver, miga, raíl, comportamiento en estrecho, dónde estoy, foco y teclado |
| **3 — Color y semántica** | Un primario por vista, deshabilitados con su razón, avisos con su color, gravedad de lejos, coherencia de un estado entre vistas |

### El banco: 100 capturas

- **74 del recorrido** sobre el `dist` publicado con los datos reales del usuario, en las cuatro
  combinaciones (1920×1080 maximizada y 1280×720, claro y oscuro): Portafolio, el menú «…» de una
  tarjeta, Nueva aplicación, Inventario, el cajón del ciclo, Hallazgos, ficha de hallazgo,
  Informes, un informe abierto, un informe con hallazgos, Métricas, Cuenta, Acerca de, las cinco
  secciones de Ajustes y el raíl plegado.
- **16 de las vistas densas** (Sesión en vivo a mitad de una sesión de seis unidades, Arreglo
  asistido con la conversación viva y el diff lleno, el cierre del arreglo y Última sesión), sobre
  la misma carcasa montada contra el agente falso y un hub temporal (D-977). Lanzarlas de verdad
  gasta los créditos del usuario y escribe en el hub del equipo.
- **10 de los diálogos** (vincular clon, patrones silenciados, eliminar aplicación,
  restablecimiento de fábrica, lanzar auditoría), construidos con su view-model real sobre datos de
  mentira, porque desde el `dist` no se pueden abrir sin mentirle al usuario: «Vincular clon local»
  solo aparece cuando el clon está roto y los otros dos borran de verdad.

**Los diálogos se fotografían una vez por tema y no cuatro**, y es correcto: los cinco llevan ancho
fijo declarado (520–760 px) y `ResizeMode="NoResize"` o alto fijo, así que no cambian con el tamaño
de la ventana.

**La escala al 150 % no tiene carpeta propia, y también es correcto.** WPF dispone en DIP: una
pantalla de 1920×1080 al 150 % da exactamente 1280×720 DIP de lienzo. La disposición, los recortes
y los saltos de columna al 150 % son los de las carpetas `1280`, con más densidad de píxel. Donde
un hallazgo dice «1280×720 (= 150 %)» quiere decir las dos cosas.

### Lo que la consolidación verificó por su cuenta

Un hallazgo de otro agente no se da por bueno porque venga escrito. Estos se han comprobado con
medida propia sobre el píxel o sobre el código, y **cinco de ellos han llegado hasta su raíz**,
que es información que no traía ninguna de las tres listas:

- **UI-0007** — `Brush.Ink.OnVivid` **no existe**. Las paletas declaran `Color.Ink.OnVivid`
  (`#10151B` en oscuro, `#FFFFFF` en claro) y es la **única de las 61 claves de color a la que le
  falta su `SolidColorBrush`**. Las seis referencias `{DynamicResource Brush.Ink.OnVivid}` de
  `SessionView.xaml` y `FindingDetailView.xaml` no resuelven y WPF cae al negro por defecto:
  medido, `#000000` en los dos temas (129 y 127 píxeles negros en el mismo recorte). El arreglo
  está escrito en el XAML y no funciona.
- **UI-0013/UI-0014** — ninguno de los **nueve** diálogos pinta su rejilla raíz. `MainWindow.xaml`
  sí lo hace (`<Grid Background="{DynamicResource Brush.Bg}">`), y su comentario explica por qué:
  `FluentWindow` aplica su propio telón por debajo. Medido: ventana `#F2EBDD`/`#171B22`, diálogo
  `#FAFAFA`/`#202020`. Y `PaletteContrastTests` solo mide contra `Bg`, `Surface` y `Surface2`, así
  que pasa en verde sobre una superficie que la aplicación nunca declara.
- **UI-0031** — el informe **sí va justificado**, y no porque nadie lo escribiera: no hay un solo
  `TextAlignment="Justify"` en el árbol. `MarkdownFlowDocument.Build` fija familia, tamaño,
  interlineado, relleno, columna y fondo, y **no fija la alineación**; el valor por defecto de
  `FlowDocument` en WPF es `Justify`.
- **UI-0046** — el marcador de «estás aquí» del raíl no cabe en su carril: `Rail.MarkerLane` es 8,
  y el `Border` pide `Rail.MarkerWidth` 3 más `Pad.XXS` 4 a cada lado = **11**. El `DataTrigger` de
  `IsActive` le pone `Brush.Primary.Fill` y no se ve: **cero píxeles del primario** en el raíl de
  las cuatro combinaciones, desplegado y plegado.
- **UI-0027** — `SessionView.xaml:306` pinta `Text="{Binding Severity}"`, el enum en crudo, en vez
  de pasar por `SeverityToLabel`. El comentario de `SeverityNames` avisa literalmente de este fallo
  («volcarlo con `ToString()` en una etiqueta escribía "Critica" en una interfaz en castellano»).

Y **una corrección a un auditor**: la lupa 2 apoyó UI-0017 en que `08-metricas.png` no tiene
«Inventario» en el raíl y `07b` sí. Esa comparación no vale: el recorrido fotografió Métricas
**antes** de abrir el inventario por primera vez, así que el orden de los ficheros no es el orden
de los hechos. **El hallazgo se sostiene igualmente**, medido en vivo sobre el `dist`: abierto el
inventario, «Inventario» está en el raíl en Métricas y en Ajustes; se pasa por Portafolio y
desaparece de las dos. La evidencia del hallazgo se ha reescrito con las capturas que sí lo prueban.

---

## Recuento

### Por gravedad

| Gravedad | Hallazgos |
|---|---:|
| **Alta** — impide o confunde una tarea | **18** |
| **Media** — cuesta más de lo que debería, o rompe la coherencia | **31** |
| **Baja** — pulido | **12** |
| **Total** | **61** |

### Por vista

Cada hallazgo cuenta una vez, en la vista donde vive el problema; los que se repiten en varias
listan las demás en su texto.

| Vista | Alta | Media | Baja | Total |
|---|---:|---:|---:|---:|
| Portafolio | 1 | 3 | 2 | 6 |
| Inventario | 2 | 2 | 0 | 4 |
| Hallazgos | 1 | 2 | 0 | 3 |
| Ficha de hallazgo | 2 | 1 | 1 | 4 |
| Sesión en vivo | 4 | 1 | 1 | 6 |
| Última sesión | 0 | 0 | 0 | **0** |
| Arreglo asistido | 2 | 2 | 1 | 5 |
| Informes | 0 | 1 | 3 | 4 |
| Informe abierto | 0 | 2 | 2 | 4 |
| Métricas | 0 | 2 | 0 | 2 |
| Cuenta | 0 | 2 | 0 | 2 |
| Acerca de | 0 | 0 | 0 | **0** |
| Ajustes | 0 | 4 | 0 | 4 |
| Nueva aplicación | 0 | 1 | 1 | 2 |
| Diálogos | 2 | 0 | 0 | 2 |
| Carcasa (raíl, miga, foco) | 4 | 8 | 1 | 13 |

### Las vistas sin ningún hallazgo propio, que también es información

- **Acerca de.** Ninguna de las tres lupas le encontró nada. Es la vista más simple de la
  aplicación —una tarjeta de 640 px centrada, D-999 §6— y es la única que sale limpia de las tres
  lupas a la vez. Le afecta un hallazgo transversal (UI-0017, la desaparición de «Inventario» del
  raíl), pero nada suyo.
- **Última sesión.** Tampoco tiene hallazgo propio, pero **no está igual de limpia**: aparece dentro
  de UI-0012 (el aviso flotante que tapa la casilla del pie) y de UI-0020 (el contenido acaba en
  y=470 de 1.032). Comparte los dos con Arreglo asistido y con Portafolio, y por eso se listan allí.

La lectura honesta del reparto: **la carcasa concentra 13 de los 61**, y cuatro de ellos son altos.
Lo que F26 arregló fueron las vistas; lo que queda peor es lo que las envuelve —el raíl, la miga,
el foco— y lo que se quedó fuera del barrido —los diálogos y el color que no sale de la paleta—.

### Duplicados

Se marcan y **no se fusionan**: la persona decide si son uno o dos.

| Hallazgo | Posible duplicado de | Por qué se dejan separados |
|---|---|---|
| **UI-0014** | UI-0013 | El fondo del diálogo y su botón primario son el mismo descuido —la ventana no lee la paleta— pero se arreglan en dos sitios distintos |
| **UI-0006** | UI-0005 | La lupa 3 los vio como un solo defecto del panel derecho de la ficha; la lupa 1 los separó en «los botones» y «la pastilla». Son dos colores fuera de paleta en la misma tarjeta |
| **UI-0010** | UI-0007, UI-0027 | Los tres tocan el panel de gravedad de Sesión en vivo y los tres son distintos: dos juegos de color, una tinta que no resuelve y un rótulo sin tilde |
| **UI-0019** | UI-0050 | La misma pastilla de temática, vista por contraste (lupas 1 y 3) y por coherencia de forma (lupa 1) |
| **UI-0050** | UI-0019 | El par recíproco del anterior |
| **UI-0022** | UI-0038 | El mismo primario encendido sin poder hacer nada, visto desde Inventario y desde las cinco secciones de Ajustes. Las lupas 2 y 3 llegaron por caminos distintos |
| **UI-0034** | UI-0010 | «Crítica» y «Alta» con el mismo relleno, y los dos juegos de color de gravedad: se cruzan en la misma escala pero fallan en vistas distintas |
| **UI-0059** | UI-0031 | Los dos salen de `MarkdownFlowDocument.Build`, pero uno es la alineación y el otro la escala tipográfica |

---

# Bloque 1 — Hallazgos

Ordenados por gravedad y, dentro de cada gravedad, por vista.

**Cómo leer la evidencia.** Cada hallazgo cita dos cosas: su **recorte**, `UI-xxxx.png`, en esta
misma carpeta, y entre paréntesis la **captura completa** de la que sale, que está en `banco/` con
la ruta que se nombra —`banco/vistas/dark-completa/01-portafolio.png`,
`banco/densas/dark-1280-06-arreglo-asistido.png`, `banco/dialogos/light-d1-vincular-clon.png`—. Las
100 capturas van enteras para que cualquiera pueda comprobar el hallazgo sin volver a montar el
banco, igual que F26 dejó las suyas en `docs/design/f26-parte-*`. Cuando una comparación nombra un
fichero **sin carpeta** —`06-informes.png`— quiere decir ese fichero en las cuatro combinaciones de
`banco/vistas/`, porque lo que se compara se ve en las cuatro.


## Gravedad alta

Impiden o confunden una tarea: un control inalcanzable, un texto que miente al recortarse, un
estado ilegible.

### UI-0001 — El menú «…» de una tarjeta no recibe el foco: borrar una aplicación no se puede sin ratón

- **Vista y lugar:** Portafolio › el botón «···» de la tarjeta de una aplicación y su desplegable.
- **Combinación:** todas.
- **Qué pasa:** abierto con el teclado (Espacio sobre «···»), el foco se queda en el botón; el
  siguiente Tab salta a «Abrir inventario», que está **detrás** del menú abierto, y sigue por el
  raíl. La única entrada del menú, «Eliminar la aplicación…», nunca recibe el foco. Además, al
  abrirse por esa vía se pinta encima el tooltip «Más acciones» y de su única entrada solo se lee
  «…aplicación…».
- **Qué debería pasar:** el desplegable toma el foco al abrirse, las flechas recorren sus entradas,
  Escape lo cierra y devuelve el foco al «···»; y el tooltip del botón no se pinta mientras su
  menú está abierto.
- **Evidencia:** `UI-0001.png` (de `banco/vistas/dark-completa/01b-portafolio-mas.png`) — el menú colgando del
  «···» de la esquina superior derecha de la tarjeta. El comportamiento de teclado, medido sobre
  `dist\Atalaya.exe`.
- **Principio:** D-944.7 (el menú es un lugar al que se llega), D-944.4 (una acción destructiva
  tiene que poder pulsarse a conciencia).
- **Gravedad:** alta — es la única vía para borrar una aplicación y no existe sin ratón.
- **Origen:** lupa 2 (A2-02).

### UI-0002 — La barra de desplazamiento del cajón del ciclo se pinta encima de los enlaces «Gestionar»

- **Vista y lugar:** Inventario › cajón «Resumen del ciclo» › bloque «Gobernanza», los tres enlaces
  «Gestionar».
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** el pulgar de la barra vertical se superpone al texto del enlace y lo cruza entre la
  `a` y la `r` de «Gestionar». El enlace se lee tachado y su zona de pulsación queda debajo de la
  barra.
- **Qué debería pasar:** el desplazamiento reserva su carril —o el contenido lleva el relleno
  derecho que lo evita—; nunca se dibuja sobre texto pulsable.
- **Evidencia:** `UI-0002.png` (de `banco/vistas/dark-1280/03b-inventario-cajon.png`), la fila «Patrones
  silenciados: 0 · Gestionar». Idéntico en `light-1280`.
- **Principio:** D-944.1 (lo que no cabe se reorganiza, no se encoge).
- **Gravedad:** alta — el único camino a la gobernanza del ciclo queda tapado por una barra.
- **Origen:** lupa 1 (A1-07). *Comprobado en la consolidación sobre el recorte.*

### UI-0003 — El cajón del ciclo no recibe el foco, no lo retiene y no se cierra con Escape

- **Vista y lugar:** Inventario › el cajón «Resumen del ciclo» (el panel flotante de la derecha).
- **Combinación:** 1280×720 y 150 %, los dos temas. A 1920 el panel está desplegado y no hay cajón.
- **Qué pasa:** al abrirlo, el foco se queda en el botón que lo abrió; Tab **no entra** en el cajón
  y sigue recorriendo la página que está tapada por debajo (buscador, filtro, «Auditar selección»,
  y luego casilla y fila de los 923 módulos); Escape **no lo cierra**. Sus seis controles —«Cerrar»,
  «Configurar ciclo», «Revisar» y los tres «Gestionar»— están marcados como enfocables y no hay
  ninguna secuencia de Tab que llegue a ellos. Desde que D-997 §2 retiró de Ajustes el aviso del
  umbral, este cajón es **el único sitio** desde el que se llega a la gobernanza del ciclo.
- **Qué debería pasar:** al abrirse el foco entra en el cajón, Tab circula dentro de él y no por la
  página que tapa, y Escape lo cierra devolviendo el foco al botón que lo abrió.
- **Evidencia:** `UI-0003.png` (de `banco/vistas/dark-1280/03b-inventario-cajon.png`) — el panel y sus seis
  controles. El recorrido de foco, medido sobre `dist\Atalaya.exe` a 1280×720.
- **Principio:** D-944.1, D-944.6.
- **Gravedad:** alta — seis controles inalcanzables sin ratón, y son los únicos de su función.
- **Origen:** lupa 2 (A2-01). *Posible duplicado de sitio, no de problema, con UI-0002.*

### UI-0004 — La miga dice «› XBLAST ›» mientras la lista enseña los hallazgos de todas las aplicaciones

- **Vista y lugar:** Hallazgos › la barra de la miga, contra el combo «Aplicación» de la barra de
  filtros.
- **Combinación:** todas.
- **Qué pasa:** la miga mete el eslabón de la aplicación cuando hay **aplicación activa de
  ventana**, y el filtro de la lista es otra cosa. Entrar en Hallazgos desde «Ver hallazgos» del
  Inventario y poner «Aplicación: Todas» deja la miga en `Portafolio › XBLAST › Hallazgos` con la
  lista enseñando el portafolio entero. Y el eslabón «XBLAST» no lleva a la lista que estás
  mirando: lleva al inventario. Al revés también: la misma página con el mismo filtro sale con dos
  migas distintas según por dónde entres.
- **Qué debería pasar:** el eslabón de la aplicación se pinta cuando la página está mostrando esa
  aplicación, no cuando la ventana la recuerda.
- **Evidencia:** `UI-0004.png` (de `banco/vistas/dark-completa/04-hallazgos.png`) — miga de dos eslabones con
  el filtro «Todas»; entrando por el inventario, la misma pantalla sale con tres.
- **Principio:** D-944.6, D-955.
- **Gravedad:** alta — la miga miente sobre dónde estás, que es lo único que la miga hace.
- **Origen:** lupa 2 (A2-06).

### UI-0005 — En la ficha, tres acciones se pintan con el botón de fábrica: iguales en los dos temas

- **Vista y lugar:** Hallazgos › ficha › bloque «Acciones»: «Generar prompt de arreglo»,
  «Verificar ahora» y «Abrir en el editor».
- **Combinación:** todas, los dos temas.
- **Qué pasa:** los tres se pintan con relleno `#DDDDDD` y texto `#000000`, **idénticos en los dos
  temas**. Medido en la consolidación: el mismo recorte da 10.943 píxeles `#DDDDDD` y 54 `#000000`
  en claro y en oscuro, byte a byte. En tema oscuro son tres barras casi blancas sobre `#1F242D`
  que pesan más que «Arreglar con agente», que es el primario verde y la acción de la vista. El
  tema no las toca.
- **Qué debería pasar:** las tres son `Button.Secondary` del sistema y quedan por debajo del
  primario en peso visual, en los dos temas.
- **Evidencia:** `UI-0005.png` (de `banco/vistas/dark-completa/05-hallazgo-ficha.png`) — el primario verde y las
  tres losas blancas debajo.
- **Principio:** D-944.4, D-945 (el tema cambia todos los recursos), D-949 (cuatro variantes de
  botón y ninguna más).
- **Gravedad:** alta — lo secundario pesa más que la acción principal, y en oscuro deslumbra.
- **Origen:** lupas 1 y 3 (A1-02, A3-05). *Medida verificada en la consolidación.*

### UI-0006 — La pastilla «Activo» usa la misma tinta en los dos temas: 2,38:1 sobre el crema

- **Vista y lugar:** Hallazgos › ficha, la fila de pastillas bajo el título (`Alta` · `Confianza
  media` · `Activo`).
- **Combinación:** tema claro, los dos tamaños.
- **Qué pasa:** la tinta de «Activo» es `#4A9EE0` **en los dos temas** sobre un fondo que sí cambia:
  `#E4EAEA` en claro da **2,38:1** y `#243343` en oscuro da 4,45:1. En claro, el dato que dice si
  el hallazgo sigue vivo se lee mucho peor que las dos pastillas de al lado (`Alta` 6,8:1,
  `Confianza media` 6,17:1). **La raíz, encontrada en la consolidación:**
  `FindingStatusToBrushConverter` (`Converters.cs:253`) escribe el color a mano y se salta la
  paleta, así que `PaletteContrastTests` no lo mira nunca.
- **Qué debería pasar:** la pastilla de estado tiene su par `.Soft`/`.Ink` por tema, como la
  gravedad, y pasa AA en los dos.
- **Evidencia:** `UI-0006.png` (de `banco/vistas/light-completa/05-hallazgo-ficha.png`) — las tres pastillas
  juntas: la tercera es la que no se lee.
- **Principio:** D-944.5, D-945, D-947.
- **Gravedad:** alta — un estado ilegible.
- **Origen:** lupas 1 y 3 (A1-03, A3-05). *Ratios recalculados y raíz localizada en la consolidación.*
  *Posible duplicado de UI-0005: la lupa 3 los dio como un solo defecto del panel.*

### UI-0007 — «Brush.Ink.OnVivid» no existe: la tinta sobre color vivo cae a negro en los dos temas

- **Vista y lugar:** Sesión en vivo › los cuatro contadores del panel «Hallazgos» y los chips de la
  lista; Hallazgos › ficha › la pastilla de gravedad bajo el título.
- **Combinación:** todas; el fallo se ve en el tema claro.
- **Qué pasa:** las paletas declaran `Color.Ink.OnVivid` —`#10151B` en oscuro, `#FFFFFF` en claro—
  y **es la única de las 61 claves de color a la que le falta su `SolidColorBrush`**. Las seis
  referencias `{DynamicResource Brush.Ink.OnVivid}` de `SessionView.xaml` (líneas 276, 281, 286,
  291, 306) y `FindingDetailView.xaml` (148) no resuelven, y WPF cae al negro por defecto. Medido:
  `#000000` en los dos temas, con el mismo recuento de píxeles. En claro el resultado es negro
  sobre los tonos oscuros del tema claro: **«Crítica 2» 3,59:1, «Alta 3» 3,56:1, «Media 2» 3,88:1,
  «Baja 1» ≈3,5:1**, los cuatro por debajo de AA. La inversión de tinta está pensada, escrita en la
  paleta con su comentario, y no llega a pintarse.
- **Qué debería pasar:** la clave tiene su pincel y la tinta se invierte con el tema, que es lo que
  el XAML ya pide.
- **Evidencia:** `UI-0007.png` (de `banco/densas/light-completa-05-sesion-en-vivo.png`) — las cuatro pastillas
  con tinta negra sobre relleno oscuro.
- **Principio:** D-944.5, D-945, D-947/D-961.
- **Gravedad:** alta — cuatro pastillas y una pastilla de gravedad por debajo de AA, y la causa es
  un recurso que no existe.
- **Origen:** lupa 3 (A3-03). *La raíz —el pincel que falta— la encontró la consolidación.*

### UI-0008 — El estado de una unidad usa el verde del tema oscuro también en el claro: 1,66:1

- **Vista y lugar:** Sesión en vivo › columna de unidades («completa», «pasada N», «pendiente» y el
  check); Portafolio › el punto de estado de la tarjeta y la insignia del avatar del raíl.
- **Combinación:** las cuatro; ilegible en las dos de tema claro.
- **Qué pasa:** el check y el punto son `#3FB950` —el verde del tema **oscuro**— en los dos temas:
  sobre el crema dan **2,34:1**. Las palabras de estado son ese mismo verde y azul rebajados con
  opacidad, así que en claro salen `#7DCA81` («completa», **1,66:1**), `#84B9DF` («pasada»,
  **1,77:1**) y `#ACAAA5` («pendiente», **1,96:1**). Y en oscuro, por el otro lado, «pendiente» se
  queda en `#606164` (**2,79:1**). El dato que dice si una unidad está hecha es lo menos legible de
  su columna.
- **Qué debería pasar:** los tres estados leen `Success.Ink`/`Primary.Ink`/`TextMuted` por
  `DynamicResource`, y lo apagado se consigue cambiando de color, no bajando la opacidad — que es
  exactamente lo que D-983 §4 resolvió para los botones.
- **Evidencia:** `UI-0008.png` (de `banco/densas/light-completa-05-sesion-en-vivo.png`) — la columna «Unidad 3
  de 6» entera: «completa» y «pendiente» casi desaparecen sobre el crema.
- **Principio:** D-944.5, D-945, D-947.
- **Gravedad:** alta — el progreso de la sesión no se puede leer en el tema claro.
- **Origen:** lupa 3 (A3-04).

### UI-0009 — A 1280 (= 150 %) las rutas de unidad se recortan sin elipsis contra el galón, y mienten

- **Vista y lugar:** Sesión en vivo › columna central, las cabeceras de unidad.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** las rutas se cortan por el final **sin puntos suspensivos** y chocan con el galón de
  desplegar: se lee `…/Servicios/FormateadorInfc⌄`, `…/Servicios/RepositorioVolac⌄`,
  `…/Utilidades/Conversiones.c⌄`. Nada dice que falte texto: `FormateadorInfc` parece un nombre de
  fichero y `Conversiones.c` parece un fichero de C.
- **Qué debería pasar:** la ruta se acorta por el medio conservando los extremos —como ya hace la
  cabecera del bloque de código desde D-983 §5— o al menos lleva elipsis y aire antes del galón.
- **Evidencia:** `UI-0009.png` (de `banco/densas/dark-1280-05-sesion-en-vivo.png`) — las tres filas plegadas.
- **Principio:** D-944.1, D-944.8.
- **Gravedad:** alta — un texto que miente al recortarse.
- **Origen:** lupa 1 (A1-04).

### UI-0010 — Dos juegos de color de gravedad, y conviven en el mismo panel

- **Vista y lugar:** Sesión en vivo › panel «Hallazgos» (contadores arriba, chips de la lista
  debajo); Hallazgos › ficha › pastilla bajo el título.
- **Combinación:** todas.
- **Qué pasa:** además de las pastillas de la paleta (`Sev.*` sobre `*.Soft`) existe un segundo
  juego escrito a mano en `Services/SeriesPalette.cs` —crítica `#D13A3A`, alta `#E07A2B`, media
  `#D2B036`, baja `#6C93C0`—, **idéntico en los dos temas**, que llega a las vistas por
  `SeverityToBrushConverter`. En el panel «Hallazgos» los dos aparecen a 40 px de distancia: los
  contadores usan el juego de la paleta y los chips de debajo el otro. La misma «Alta» sale en dos
  naranjas distintos en la misma columna.
- **Qué debería pasar:** una sola forma de pintar una gravedad, la de `Pill.Sev`/`Pill.Sev.Text`
  que ya usan Portafolio, Hallazgos, Métricas y el informe. D-990 lo dejó escrito: «cuatro sitios
  pintando una gravedad y una sola forma de hacerlo».
- **Evidencia:** `UI-0010.png` (de `banco/densas/dark-completa-05-sesion-en-vivo.png`) — la fila de contadores y
  los chips de la lista, en la misma columna.
- **Principio:** D-944.4, D-945, D-971.
- **Gravedad:** alta — la escala que gobierna la aplicación tiene dos códigos de color a la vez.
- **Origen:** lupa 3 (A3-02). *Confirmado en el código durante la consolidación.*
  *Posible duplicado de UI-0007 y UI-0027: los tres están en el mismo panel y son distintos.*

### UI-0011 — A 1280 (= 150 %) la cabecera del arreglo se queda en «Age» y «atala…»

- **Vista y lugar:** Arreglo asistido › la tira de identidad de la cabecera (nombre de aplicación y
  pastilla de proveedor·modelo).
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la pastilla «Agente falso · claude-opus-4.7» se recorta **a mitad de palabra y sin
  elipsis** hasta dejar solo `Age`, y el nombre de la aplicación queda en `atala…`. En la pantalla
  de cierre la misma pastilla enseña `Agente falso · clau`. El modelo con el que se está
  arreglando —el dato que decide cuánto cuesta y quién juzga— desaparece, y lo que queda no se
  puede interpretar.
- **Qué debería pasar:** por debajo de un ancho la tira baja a una segunda línea o pliega los datos
  secundarios; una pastilla que no cabe entera se retira, no se corta en tres letras.
- **Evidencia:** `UI-0011.png` (de `banco/densas/dark-1280-06-arreglo-asistido.png`) — la cabecera entre «Volver
  al hallazgo» y «Pausar».
- **Principio:** D-944.1, D-944.8.
- **Gravedad:** alta — con quién y con qué modelo se está tocando el código deja de estar escrito.
- **Origen:** lupa 1 (A1-05). *Verificado en la consolidación contra la misma vista a 1920.*

### UI-0012 — El aviso flotante se planta encima de la barra de cierre y tapa una casilla

- **Vista y lugar:** Arreglo asistido y Última sesión › la barra de resumen del pie; el aviso
  emergente de abajo a la derecha.
- **Combinación:** todas.
- **Qué pasa:** el aviso («Sesión completada: 13 nuevos…») se dibuja encima del pie de la vista y
  **tapa una casilla de verificación con su rótulo**: solo asoma el borde superior del cuadrito y
  la mitad de arriba de las letras. Mientras el aviso está en pantalla la casilla no se ve ni se
  sabe qué dice; a 1280 se lleva por delante también «build/tests».
- **Qué debería pasar:** el aviso ocupa su propio carril —empuja el contenido o vive por encima de
  la barra— y deja libre la franja de acciones. Un aviso informativo no puede tapar un control.
- **Evidencia:** `UI-0012.png` (de `banco/densas/dark-completa-06-arreglo-asistido.png`), esquina inferior
  derecha. Igual en `light-completa`, `dark-1280` y en `banco/densas/light-1280-08-ultima-sesion.png`.
- **Principio:** D-944.8, D-944.4.
- **Gravedad:** alta — un control tapado por algo que el usuario no ha pedido.
- **Origen:** lupa 1 (A1-06).

### UI-0013 — Los nueve diálogos no llevan la paleta: blanco de fábrica sobre una aplicación crema

- **Vista y lugar:** los cinco diálogos fotografiados (y los nueve del árbol): el fondo de la
  ventana, las cajas de aviso y los campos de texto.
- **Combinación:** los dos temas.
- **Qué pasa:** medido en el píxel: la ventana de la aplicación es `#F2EBDD` en claro y `#171B22`
  en oscuro; el diálogo es **`#FAFAFA`** y **`#202020`**, que son los colores de fábrica de WPF-UI.
  Las cajas de texto de la confirmación de borrado y del restablecimiento son `#FFFFFF` puro. En
  «Patrones silenciados» conviven las dos paletas: la página es `#FAFAFA` y las tarjetas de patrón
  son crema, así que **la tarjeta sale más oscura que la página que la sostiene**, al revés que en
  toda la aplicación. **La raíz, encontrada en la consolidación:** `MainWindow.xaml` pinta su
  rejilla raíz con `Brush.Bg` justamente por esto —su comentario lo explica: `FluentWindow` aplica
  su propio telón por debajo— y **ninguno de los nueve `*Dialog.xaml` lo hace**. Y
  `PaletteContrastTests` solo mide contra `Bg`, `Surface` y `Surface2`: pasa en verde sobre una
  superficie que la aplicación nunca declara.
- **Qué debería pasar:** el diálogo es una superficie más y lee las mismas claves.
- **Evidencia:** `UI-0013.png` (de `banco/dialogos/light-d3-eliminar-aplicacion.png`) — el fondo blanco y el campo
  `#FFFFFF`. Y `banco/dialogos/dark-d1-vincular-clon.png` frente a `banco/vistas/dark-completa/01-portafolio.png`.
- **Principio:** D-944.5, D-945, D-948 («claro sobre crema, no sobre blanco»).
- **Gravedad:** alta — el modo claro que D-948 decidió no existe en los diálogos, y dos de ellos son
  los que confirman un borrado.
- **Origen:** lupas 1 y 3 (A1-01, A3-01). *Medida y raíz verificadas en la consolidación.*

### UI-0014 — El primario del diálogo es el azul de Windows, no el de Atalaya

- **Vista y lugar:** los cinco diálogos › el botón principal, los radios y el foco de campo.
- **Combinación:** los dos temas.
- **Qué pasa:** «Vincular», «Confirmar y auditar» y compañía se pintan `#1E9BFA` en oscuro y
  `#0071C7` en claro —el acento de Windows— en vez de `Primary.Fill` (`#3A72DD`/`#2F62C9`). Y la
  caja de peligro es `#382424`/`#F5E1E1` en vez de `Danger.Soft` (`#42211F`/`#F7DCD9`). Abrir un
  diálogo desde una pantalla de Atalaya enseña otro azul.
- **Qué debería pasar:** `Primary.Fill` para el primario y `Danger.Soft` para la caja de peligro,
  como en el resto.
- **Evidencia:** `UI-0014.png` (de `banco/dialogos/light-d1-vincular-clon.png`) — «Vincular» en azul Windows.
- **Principio:** D-944.4, D-945.
- **Gravedad:** alta — el botón que confirma una acción irreversible no es del color de la casa.
- **Origen:** lupa 3 (A3-01). *Posible duplicado de UI-0013: mismo descuido, dos sitios de arreglo.*

### UI-0015 — El foco se marca con el rectángulo de puntos de fábrica: 1,21:1 en tema oscuro

- **Vista y lugar:** toda la aplicación; se ve en cualquier control (raíl, botones de tarjeta,
  filtros).
- **Combinación:** todas; el problema de contraste es del tema oscuro.
- **Qué pasa:** ningún control declara su anillo de foco, así que WPF pinta el suyo: un rectángulo
  de puntos de 1 px en negro. Medido sobre el «···» del Portafolio en oscuro: `#0F1216` sobre
  `#1F242D`, **1,21:1**. Es **el único elemento visual de la aplicación que no cambia con el tema**,
  porque no sale de la paleta: en claro se ve porque el negro sobre crema se ve, no porque nadie lo
  haya elegido. Y su forma —rectángulo a hueso— no coincide con la pastilla redondeada del raíl, así
  que el foco y el «estás aquí» hablan dos idiomas.
- **Qué debería pasar:** un anillo de foco propio, con su token y sus dos claves de paleta, por
  encima de 3:1 en los dos temas y con el radio del control que rodea.
- **Evidencia:** `UI-0015.png` (de `banco/vistas/dark-completa/01-portafolio.png`, la vista donde se midió). La
  medida se tomó sobre `dist\Atalaya.exe` con el foco puesto: el anillo no aparece en una captura
  del recorrido porque el recorrido navega con `Invoke`, no con el teclado.
- **Principio:** D-944.5, D-945, D-947/D-961.
- **Gravedad:** alta — quien navega con el teclado no ve dónde está.
- **Origen:** lupa 2 (A2-03).

### UI-0016 — Media aplicación llega sin nombre al árbol de accesibilidad

- **Vista y lugar:** la carcasa y las cuatro vistas medidas (Portafolio, Hallazgos, Inventario,
  Ajustes).
- **Combinación:** todas.
- **Qué pasa:** volcado el árbol del `dist`, llegan **sin nombre**: la flecha de volver, la fila de
  cuenta del pie del raíl, los cuatro combos de filtro de Hallazgos, el buscador, las 111 filas de
  hallazgo, las casillas y filas de módulo del Inventario y los dos combos de Ajustes. Los bloques
  del raíl llegan como `DataItem 'Atalaya.App.ViewModels.NavGroup'` —el nombre de la clase—, así
  que **la promesa de D-1000 §2 no se cumple en el binario**: el nombre está declarado sobre un
  `StackPanel`, que no llega a la vista de control. Y las cinco secciones de Ajustes, que sí
  declaran `AutomationProperties.Name`, se enfocan como **`[Window] Atalaya`** (paradas 18–22 de
  las 28 del ciclo). Con el foco ahí, Espacio activa pero **Enter no hace nada y las flechas no
  mueven**: en una lista de cinco hay que tabular una a una.
- **Qué debería pasar:** todo control que se enfoca dice su nombre, y una lista de cinco secciones
  se recorre con flechas y se activa con Enter y con Espacio.
- **Evidencia:** `UI-0016.png` (de `banco/vistas/dark-completa/11-ajustes.png`) — las cinco entradas de sección
  que no tienen nombre en el árbol. Volcado completo medido sobre `dist\Atalaya.exe`.
  *Comprobado además en la consolidación:* tras abrir y cerrar el menú «…» de una tarjeta, esas
  cinco entradas **desaparecen del todo** del árbol (4/4 antes, 0/4 después, reproducido cuatro
  veces); siguen pintadas y siguen funcionando con el ratón.
- **Principio:** D-944.7, y D-1000 §2, que declara lo contrario.
- **Gravedad:** alta — un lector de pantalla no puede nombrar media aplicación.
- **Origen:** lupa 2 (A2-04). *La desaparición tras el menú «…» la encontró el banco de capturas.*

### UI-0017 — Pasar por Portafolio borra «Inventario» del raíl: deja de estar a un paso

- **Vista y lugar:** el raíl, en Métricas, Cuenta, Ajustes, Acerca de y Nueva aplicación.
- **Combinación:** todas.
- **Qué pasa:** el bloque de la aplicación —donde vive «Inventario»— solo se pinta si hay
  aplicación activa, y **Portafolio la borra a propósito** (D-953). Consecuencia: en cuanto se pasa
  por Portafolio, el raíl de esas cinco vistas se queda sin la entrada y volver al inventario
  cuesta **dos pasos**. Medido en vivo sobre el `dist`: abierto el inventario, «Inventario» sigue
  en el raíl en Métricas y en Ajustes; se pulsa «Portafolio» y desaparece de las dos.
- **Qué debería pasar:** la entrada no desaparece. O el raíl recuerda la última aplicación mirada
  aunque se pase por Portafolio, o «Inventario» se queda siempre y, sin aplicación elegida, abre el
  selector.
- **Evidencia:** `UI-0017.png` (de `banco/vistas/dark-completa/08-metricas.png`) — el raíl sin «Inventario».
  El par que lo prueba en el banco es `03-inventario.png` … `07b-informe-con-hallazgos.png` (con la
  entrada) frente a `12-rail-plegado.png`, tomada después de volver a Portafolio (sin ella).
  **Nota de la consolidación:** la lupa 2 citó `08-metricas.png` contra `07b`, y esa comparación no
  vale —el recorrido fotografió Métricas antes de abrir el inventario—; el hallazgo se sostiene por
  la medida en vivo, no por ese par.
- **Principio:** D-944.6, literalmente: «el inventario es alcanzable en un paso desde cualquier sitio».
- **Gravedad:** alta — incumple un principio por su enunciado exacto.
- **Origen:** lupa 2 (A2-05). *Reproducido y corregido en la evidencia por la consolidación.*

### UI-0018 — El azul de fábrica de la librería convive con el primario de Atalaya

- **Vista y lugar:** Ajustes › Auditoría y Apariencia (interruptores); Cuenta (los dos anillos de
  «Comprobando»); Portafolio (barra de progreso del ciclo); ficha › Gobernanza (radios); los cinco
  diálogos; Arreglo asistido («Responder» y «Enviar»); la barra de estado de la sesión.
- **Combinación:** todas.
- **Qué pasa:** hay **tres azules distintos diciendo cosas cercanas**: `Primary.Fill`
  (`#3A72DD`/`#2F62C9`) en los botones propios, `Primary.Ink` (`#759DE8`/`#2E5FC3`) en los enlaces,
  y `#1E9BFA`/`#0071C7` —el acento de Windows, que la paleta nunca declara— en todo lo que sigue
  siendo un control de la librería. En Cuenta se ven los tres a la vez; en Ajustes › Apariencia el
  interruptor encendido es azul Windows a 40 px de un aviso azul de la paleta.
- **Qué debería pasar:** la paleta ya redirige `AccentFillColorDefaultBrush` y compañía; faltan las
  claves que leen el interruptor, el anillo, la `ProgressBar`, el radio y el foco, para que
  «encendido / en curso / esto es lo principal» sea un solo azul.
- **Evidencia:** `UI-0018.png` (de `banco/vistas/dark-completa/11b-ajustes-auditoria.png`) — el interruptor
  `#1E9BFA` con maneta negra. También en `banco/vistas/light-completa/11d-ajustes-apariencia.png`,
  `banco/vistas/dark-completa/09-cuenta.png` y `banco/vistas/dark-completa/01-portafolio.png`.
- **Principio:** D-944.4, D-945.
- **Gravedad:** alta — «lo principal» se dice con tres colores según quién pinte el control.
- **Origen:** lupa 3 (A3-06).


## Gravedad media

Cuestan más de lo que deberían, o rompen la coherencia entre vistas.

### UI-0019 — La pastilla neutra de temática no llega a AA en ninguno de los dos temas

- **Vista y lugar:** Portafolio › tarjeta de aplicación (junto a «1,8 % auditado») e Inventario ›
  «Resumen del ciclo» (junto a «Configurar ciclo»); también en el cajón a 1280.
- **Combinación:** todas.
- **Qué pasa:** la pastilla usa grises que no están en ninguna paleta: `#DDDBD7` con tinta `#6B7280`
  en claro (**3,50:1**) y `#383E47` con `#9CA3AF` en oscuro (**4,25:1**), con texto de 12 px. Los
  dos por debajo de 4,5. Es el único par medido que falla en los **dos** temas, así que no es un
  descuido del crema: es un par que nunca entró en la comprobación. La pastilla neutra del sistema,
  la de la columna «Tipo» de Informes, usa `Surface2` + `TextMuted` y da 6,45:1.
- **Qué debería pasar:** una sola pastilla neutra, la del sistema, en los tres sitios.
- **Evidencia:** `UI-0019.png` (de `banco/vistas/light-completa/01-portafolio.png`) — la línea «Ciclo 1 · 1,8 %
  auditado · General».
- **Principio:** D-944.5, D-944.4, D-947/D-961.
- **Gravedad:** media.
- **Origen:** lupas 1 y 3 (A1-08, A3-12). *Ratios recalculados en la consolidación: coinciden.*
  *Posible duplicado de UI-0050.*

### UI-0020 — Azulejos de 393 px con 55 px de contenido, y media pantalla vacía

- **Vista y lugar:** Portafolio (la tira de gravedades y la rejilla); también Última sesión, el
  informe abierto y Ajustes.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** los cuatro azulejos de Portafolio ocupan 266→659, 680→1073, 1094→1487 y
  1508→1901 —**393 px cada uno**— y dentro de cada uno el contenido («0 / Críticas») mide ~55 px:
  el 86 % del azulejo está vacío. Debajo, la rejilla de aplicaciones usa 531 de 1.635 px y deja el
  resto en blanco, con **las mismas cuatro cifras repetidas** 200 px más abajo dentro de la tarjeta.
  En Última sesión el contenido acaba en y=470 de 1.032; en Ajustes › Proveedor y modelo acaba en
  y=545 y en x=1.290.
- **Qué debería pasar:** o los azulejos se ciñen a su contenido y la rejilla usa el ancho, o la tira
  de gravedades se funde con la tarjeta que ya las enseña. Una vista con tres renglones no se estira
  hasta 1.920: se reparte o se ciñe.
- **Evidencia:** `UI-0020.png` (de `banco/vistas/dark-completa/01-portafolio.png`, la vista entera: es el hueco
  lo que hay que ver). También `banco/densas/dark-completa-08-ultima-sesion.png` y
  `banco/vistas/dark-completa/11-ajustes.png`.
- **Principio:** D-944.2 («el espacio se reparte, no se deja»; «no hay vistas donde el 60 % de la
  pantalla esté vacío»).
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-15).

### UI-0021 — Más de un botón primario por vista

- **Vista y lugar:** Portafolio (cabecera + tarjeta); Arreglo asistido (tarjeta de decisión +
  compositor).
- **Combinación:** todas.
- **Qué pasa:** Portafolio tiene «+ Nueva aplicación» y «Abrir inventario», los dos `Primary.Fill`
  macizo del mismo peso; con N tarjetas serían N+1 azules. En Arreglo asistido hay dos primarios a
  la vez —«Responder» en la tarjeta ámbar y «Enviar» en el compositor— además de un aviso macizo
  («Detener») y un peligro macizo («Descartar todo»).
- **Qué debería pasar:** un primario por vista. La acción de una tarjeta puede ser secundaria —la
  tarjeta ya es su contexto— y de los dos campos de respuesta solo uno lleva el primario.
- **Evidencia:** `UI-0021.png` (de `banco/vistas/dark-completa/01-portafolio.png`) — los dos `#3A72DD`, arriba a
  la derecha y al pie de la tarjeta. También `banco/densas/dark-completa-06-arreglo-asistido.png`.
- **Principio:** D-944.4 («un solo botón primario por vista»).
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-08).

### UI-0022 — «Auditar selección» está encendido con cero unidades y sin decir cuántas

- **Vista y lugar:** Inventario › la barra de la lista, el botón primario.
- **Combinación:** todas.
- **Qué pasa:** con ninguna casilla marcada el primario se pinta a plena intensidad y está
  habilitado (`IsEnabled=True`, medido). D-999 §1 bajó ese botón a la barra de la lista precisamente
  **con su recuento** —«Auditar 3 seleccionadas»— «porque es lo que se va a gastar»; con cero
  seleccionadas no hay recuento y tampoco hay freno: el botón que gasta créditos del usuario está
  encendido sin nada que auditar y sin la razón al lado.
- **Qué debería pasar:** deshabilitado mientras no haya selección, con la razón al lado, y el
  recuento en el rótulo en cuanto la haya.
- **Evidencia:** `UI-0022.png` (de `banco/vistas/dark-completa/03-inventario.png`) — «Auditar selección» azul con
  las 923 casillas de debajo sin marcar.
- **Principio:** D-944.4 («un botón deshabilitado por una razón lleva la razón al lado»), D-999 §1.
- **Gravedad:** media.
- **Origen:** lupas 2 y 3 (A2-17, A3-09). *Posible duplicado de UI-0038.*

### UI-0023 — «Expandir todo» es enlace azul en Inventario y botón neutro en Hallazgos

- **Vista y lugar:** Inventario › barra de la lista (extremo derecho) y Hallazgos › cabecera
  (arriba a la derecha).
- **Combinación:** todas.
- **Qué pasa:** el mismo rótulo y la misma acción —desplegar todos los grupos de una lista— se
  pintan como enlace `Primary.Ink` en Inventario y como botón secundario con borde y tinta neutra
  en Hallazgos, y además en sitios distintos de la pantalla. El azul, que en el resto de la
  aplicación anuncia navegación, aquí anuncia un plegado.
- **Qué debería pasar:** un gesto, una forma, y en el mismo sitio de la vista.
- **Evidencia:** `UI-0023.png` (de `banco/vistas/dark-completa/03-inventario.png`, el enlace azul) frente a
  `banco/vistas/dark-completa/04-hallazgos.png` (el botón, arriba a la derecha).
- **Principio:** D-944.4.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-15).

### UI-0024 — Las pastillas de gravedad se empaquetan a la derecha: la columna no significa nada

- **Vista y lugar:** Hallazgos › la lista, las pastillas de recuento al final de cada fila.
- **Combinación:** todas.
- **Qué pasa:** medidas las x, las filas con tres pastillas las ponen en 1670/1747/1819 y las que
  solo tienen una la ponen en **1819**, que es la ranura de «Baja». Recorriendo la columna derecha
  se lee «4 Baja, 1 Baja, 6 Baja, **3 Alta**, 1 Baja, 1 Baja, **3 Media**…»: la misma gravedad
  cambia de columna según cuántas tenga la fila, y una pastilla roja aparece donde el ojo ya se ha
  acostumbrado a ver azul.
- **Qué debería pasar:** tres ranuras fijas —Alta, Media, Baja— con hueco vacío cuando esa gravedad
  no tiene casos, para que la columna se pueda recorrer.
- **Evidencia:** `UI-0024.png` (de `banco/vistas/dark-completa/04-hallazgos.png`) — la columna derecha de seis
  filas seguidas: se ve «3 Alta» y «3 Media» solos en la ranura de la derecha.
- **Principio:** D-944.8, D-973 («la gravedad se ve sin leer»).
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-14). *Verificado en la consolidación sobre el recorte.*

### UI-0025 — «Ver hallazgos» borra en silencio el filtro que el raíl sí conserva

- **Vista y lugar:** Inventario › cabecera, botón «Ver hallazgos» → Hallazgos › barra de filtros.
- **Combinación:** todas.
- **Qué pasa:** D-952 funciona por el raíl: puesto «Gravedad: Alta», salir a Métricas o a Portafolio
  y volver a Hallazgos por el raíl devuelve «Alta». Pero llegar a la misma página por «Ver
  hallazgos» **restablece toda la barra**: la gravedad vuelve a «Todas» y solo se conserva la
  aplicación. La misma vista vuelve de dos maneras según la puerta, y nada lo dice.
- **Qué debería pasar:** un enlace que añade un filtro añade ese filtro y respeta los demás; si
  limpia, lo dice, con su «Limpiar filtros» al lado, que ya existe.
- **Evidencia:** `UI-0025.png` (de `banco/vistas/dark-completa/03-inventario.png`) — el botón «Ver hallazgos» de
  la cabecera. El comportamiento, medido sobre `dist\Atalaya.exe`.
- **Principio:** D-944.6, D-952, D-973.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-15).

### UI-0026 — En la ficha, la monoespaciada marca tres filas al azar y se lee como énfasis

- **Vista y lugar:** Hallazgos › ficha › tarjeta «Metadatos».
- **Combinación:** todas.
- **Qué pasa:** de las doce filas, tres valores van en monoespaciada y más brillantes (`Regla`,
  `Unidad`, `Commit anclado`) y nueve en la proporcional del sistema. El criterio no se sostiene:
  `Identificador: BUG-0008` es un identificador y va proporcional, mientras `Commit anclado:
  e34fc69` va monoespaciada. Como las tres también pesan más, la tipografía dice que esas tres
  filas son las importantes, y no lo son.
- **Qué debería pasar:** la monoespaciada marca una sola clase de dato y la marca siempre —rutas y
  hashes, por ejemplo— y no cambia además el peso: el énfasis de una tabla de metadatos lo pone el
  sitio, no la familia.
- **Evidencia:** `UI-0026.png` (de `banco/vistas/dark-completa/05-hallazgo-ficha.png`) — la tabla de metadatos
  entera, con las filas «Identificador» y «Commit anclado» a la vista.
- **Principio:** D-944.3, D-976.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-17).

### UI-0027 — Los cuatro niveles se rotulan de cinco maneras, y una lleva falta de ortografía

- **Vista y lugar:** Portafolio, Hallazgos, Métricas, informe abierto y Sesión en vivo.
- **Combinación:** todas.
- **Qué pasa:** Portafolio dice «Críticas · Altas · Medias · Bajas»; Hallazgos, «15 Alta · 10 Media
  · 4 Baja»; Métricas, «Crít 0 · Alta 27»; el informe, «3 Altas · 6 Medias»; y Sesión en vivo,
  «Crítica 2» en los contadores y **«Critica» sin tilde** en los chips de la lista, a 40 px de
  distancia. Singular/plural, abreviado/entero, cifra delante o detrás: cinco combinaciones para el
  mismo dato. **La raíz, encontrada en la consolidación:** `SessionView.xaml:306` pinta
  `Text="{Binding Severity}"`, el enum en crudo, en vez de pasar por `SeverityToLabel`. El
  comentario de `SeverityNames` avisa literalmente de este fallo.
- **Qué debería pasar:** un rótulo por nivel, escrito una vez, y la cifra siempre en el mismo lado.
- **Evidencia:** `UI-0027.png` (de `banco/densas/dark-completa-05-sesion-en-vivo.png`) — «Crítica 2» arriba y
  «Critica» en los chips de debajo.
- **Principio:** D-944.4 (el significado llega por la palabra, no solo por el color).
- **Gravedad:** media — la falta de ortografía sola sería baja; los cinco rótulos, no.
- **Origen:** lupa 3 (A3-11). *Raíz localizada en la consolidación.*
  *Posible duplicado de sitio con UI-0007 y UI-0010.*

### UI-0028 — El borde superior de la conversación siega la primera línea por la mitad de la letra

- **Vista y lugar:** Arreglo asistido › columna de la conversación, el primer mensaje visible.
- **Combinación:** todas.
- **Qué pasa:** el panel de desplazamiento empieza pegado a la cabecera, sin relleno ni degradado,
  así que el mensaje de arriba aparece cortado a media altura de glifo. No parece un texto que
  continúe hacia arriba: parece un texto roto.
- **Qué debería pasar:** relleno superior en el desplazamiento y, si acaso, un degradado de dos o
  tres píxeles que diga que hay más arriba.
- **Evidencia:** `UI-0028.png` (de `banco/densas/dark-completa-06-arreglo-asistido.png`) — la primera fila de la
  columna central.
- **Principio:** D-944.3.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-18).

### UI-0029 — Al terminar el arreglo, el raíl dice «Último arreglo» y el título sigue en «Arreglo asistido»

- **Vista y lugar:** Arreglo asistido › pantalla de cierre: entrada activa del raíl, título de la
  vista y último eslabón de la miga.
- **Combinación:** todas.
- **Qué pasa:** al acabar, la entrada del raíl cambia de rótulo y de icono («Arreglo asistido» +
  punto verde → «Último arreglo» + llave) pero la página no: el título y la miga siguen diciendo
  «Arreglo asistido». En la misma pantalla, los tres sitios que dicen dónde estás dicen dos cosas.
  La sesión, en el mismo caso, sí cuadra: «Última sesión» en los tres.
- **Qué debería pasar:** el rótulo del raíl, el título y el último eslabón de la miga son la misma
  cadena.
- **Evidencia:** `UI-0029.png` (de `banco/densas/dark-1280-07-arreglo-cierre.png`) — el raíl con «Último
  arreglo» resaltado y el título «Arreglo asistido» a su derecha. Compárese con
  `banco/densas/dark-completa-08-ultima-sesion.png`.
- **Principio:** D-944.6, D-955.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-11).

### UI-0030 — Los números de Informes no se alinean, y son los únicos de la casa que no lo hacen

- **Vista y lugar:** Informes › la tabla, columnas «Unidades», «Hallazgos» y «Coste».
- **Combinación:** todas.
- **Qué pasa:** las tres columnas numéricas van alineadas a la izquierda y sin dígitos tabulares,
  así que «1 unidad» y «12 unidades» empiezan en la misma x y sus cifras no comparten columna; lo
  mismo con «20,2 AI credits» y «64,6 AI credits» y con el «—» del coste desconocido. En Tarifas
  (D-997 §4) y en el desglose por fase de Métricas los números ya van a la derecha y tabulares. La
  tabla que existe para comparar gasto es la única donde no se puede comparar de un vistazo.
- **Qué debería pasar:** cifras a la derecha, con `NumeralAlignment="Tabular"`, como en Tarifas.
- **Evidencia:** `UI-0030.png` (de `banco/vistas/dark-completa/06-informes.png`) — las tres columnas numéricas,
  con la fila de «12 unidades» entre las de «1 unidad».
- **Principio:** D-944.3, D-944.8, D-992.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-16).

### UI-0031 — El cuerpo del informe va justificado y abre ríos de espacio en una medida de 700 px

- **Vista y lugar:** Informes › informe abierto › «Cobertura» y «Hallazgos nuevos».
- **Combinación:** todas.
- **Qué pasa:** los párrafos se justifican a los dos lados en una medida de ~700 px y sin partición
  de palabras —WPF no la tiene—, así que las líneas largas quedan con huecos de tres espacios entre
  palabras. Es el único texto de la aplicación que se justifica; todo lo demás va en bandera. **La
  raíz, encontrada en la consolidación:** no hay ni un `TextAlignment="Justify"` en el árbol;
  `MarkdownFlowDocument.Build` fija familia, tamaño, interlineado, relleno, columna y fondo y **no
  fija la alineación**, y el valor por defecto de `FlowDocument` en WPF es `Justify`.
- **Qué debería pasar:** el informe se lee en bandera a la izquierda, como el resto.
- **Evidencia:** `UI-0031.png` (de `banco/vistas/dark-completa/07b-informe-con-hallazgos.png`) — el último
  párrafo, con los huecos entre palabras a la vista.
- **Principio:** D-944.3, D-993.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-10). *Raíz localizada en la consolidación.*

### UI-0032 — Dentro de la tarjeta del informe conviven dos anchos y un hueco de 310 px

- **Vista y lugar:** Informes › informe abierto, la tarjeta del documento.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** la regla que va bajo «Informe de sesión — XBLAST» ocupa 290→990 (700 px) y la barra
  «Anexo técnico — diagnóstico», dentro de la misma tarjeta, ocupa 290→1881 (1.591 px). Y como la
  barra va anclada al pie, en una sesión corta el último renglón acaba en y≈650 y la barra aparece
  en y≈960: **310 px de nada** entre las dos. Es el mismo defecto de `DockPanel` que D-983 quitó de
  «Última sesión».
- **Qué debería pasar:** el anexo mide lo que mide la medida del texto y va justo debajo del último
  renglón; la tarjeta acaba donde acaba su contenido.
- **Evidencia:** `UI-0032.png` (de `banco/vistas/dark-completa/07-informe-abierto.png`, la vista entera: el hueco
  es lo que hay que ver).
- **Principio:** D-944.2, D-944.8, D-993.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-11).

### UI-0033 — «Descubrimiento» se recorta a «Descubrimient» en la tarjeta de coste de Métricas

- **Vista y lugar:** Métricas › azulejo «Coste del periodo», el desglose por fase.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la etiqueta de la primera fila se corta a mitad de la `o` final, sin elipsis, y
  queda pegada sin un píxel de aire a la columna de valores. La segunda fila, «Arreglo», deja 60 px
  de hueco: las dos filas que existen para compararse no comparten ni el ancho de su columna.
- **Qué debería pasar:** la columna de etiquetas mide lo que mide la etiqueta más larga, con su
  aire, y las dos filas alinean.
- **Evidencia:** `UI-0033.png` (de `banco/vistas/dark-1280/08-metricas.png`) — «Descubrimient» pegado a «5 ses.
  · 97 llam.».
- **Principio:** D-944.3, D-990.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-13). *Verificado en la consolidación sobre el recorte.*

### UI-0034 — «Crítica» y «Alta» comparten fondo: la escala de cuatro niveles se lee como tres

- **Vista y lugar:** Métricas › tarjeta «Hallazgos activos»; Hallazgos › pastillas de la fila;
  informe abierto › «Resumen».
- **Combinación:** todas.
- **Qué pasa:** las pastillas toman el fondo de las familias semánticas y no de la escala de
  gravedad, y como no existe `Sev.Crit.Soft` la crítica reutiliza `Danger.Soft`: «Crít 0» y
  «Alta 27» tienen **el mismo relleno** (`#42211F` en oscuro, `#F7DCD9` en claro) y solo se
  distinguen por la tinta. A la distancia a la que se recorre una lista —que es para lo que D-973
  puso el color— hay tres manchas, no cuatro.
- **Qué debería pasar:** cuatro rellenos para cuatro niveles, como ya hay cuatro tintas.
- **Evidencia:** `UI-0034.png` (de `banco/vistas/dark-completa/08-metricas.png`) — las cuatro pastillas en fila,
  las dos primeras con el mismo fondo.
- **Principio:** D-944.4, D-973.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-10). *Posible duplicado de UI-0010.*

### UI-0035 — Cuenta y Nueva aplicación empiezan en otra x que el resto de la aplicación

- **Vista y lugar:** Cuenta y Nueva aplicación, el bloque de página entero.
- **Combinación:** todas.
- **Qué pasa:** medido sobre la fila del título, todas las vistas arrancan en **x = 264–266**
  (Portafolio, Hallazgos, Informes, Métricas, Inventario, ficha, Ajustes) menos dos: **Nueva
  aplicación en x = 633** y **Cuenta en x = 800**. Al pasar de Portafolio a Cuenta el contenido
  salta 536 px a la derecha, y la miga —que sigue en x = 311— se queda 489 px a la izquierda de su
  propio título. Además la tarjeta de Cuenta mide 570 px fijos dentro de un área de 1.690: el 34 %
  del lienzo.
- **Qué debería pasar:** un margen de página por aplicación. Si una vista se centra, se centran
  todas las de su clase; «Acerca de» (tarjeta de 640 centrada, D-999 §6) es la excepción declarada,
  no el tercer patrón.
- **Evidencia:** `UI-0035.png` (de `banco/vistas/dark-completa/09-cuenta.png`, la vista entera) frente a
  `banco/vistas/dark-completa/06-informes.png`.
- **Principio:** D-944.2, D-944.8.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-09). *Las x recalculadas en la consolidación: coinciden.*

### UI-0036 — El estado del hub se dice con icono + color + palabra arriba y en texto neutro abajo

- **Vista y lugar:** Cuenta › «Estado de la conexión» (fila «Acceso al hub») y › «Hub local».
- **Combinación:** todas.
- **Qué pasa:** la misma verdad —el hub está sincronizado— se cuenta arriba con check verde,
  «Disponible» y el sello de hora, y abajo como «Estado: sincronizado · última sincronización: …»
  en tinta neutra, sin icono y sin color. D-989 fijó el patrón de icono + COLOR + PALABRA para las
  filas de conexión y la tarjeta de al lado no lo sigue: si el hub estuviera desincronizado, esa
  línea se leería exactamente igual.
- **Qué debería pasar:** un estado se dice siempre con las tres cosas, en las dos tarjetas.
- **Evidencia:** `UI-0036.png` (de `banco/vistas/dark-completa/09-cuenta.png`) — el bloque «Estado de la
  conexión» y la tarjeta «Hub local» debajo.
- **Principio:** D-944.4, D-989.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-14).

### UI-0037 — La barra de acciones fija corta el texto por la mitad de la letra

- **Vista y lugar:** Ajustes › Avanzado (última ayuda); Arreglo asistido › cierre («Resultado del
  último build/tests»); Informes › informe abierto («Cobertura»).
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la barra pegada al pie de la columna (D-1000 §1) recorta el contenido en seco: la
  línea de debajo se ve a media altura de glifo, sin elipsis, sin degradado y sin nada que diga que
  hay más. En el cierre del arreglo es peor: el titular «Resultado del último build/tests» queda a
  la vista y su única línea de contenido, tapada — un epígrafe sin cuerpo.
- **Qué debería pasar:** el corte cae entre líneas, no dentro de una, y lleva un degradado o una
  sombra que diga que el contenido sigue.
- **Evidencia:** `UI-0037.png` (de `banco/vistas/dark-1280/11e-ajustes-avanzado.png`) — la línea seccionada
  sobre la barra. También `banco/densas/light-1280-07-arreglo-cierre.png` y
  `banco/vistas/dark-1280/07-informe-abierto.png`.
- **Principio:** D-944.1, D-944.3.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-12).

### UI-0038 — «El primario apagado hasta que haya algo que hacer» no es una regla: cuatro sitios y cuatro criterios

- **Vista y lugar:** Ajustes (Proveedor, Auditoría, Apariencia, Avanzado) frente a Ajustes ›
  Tarifas; Inventario; Nueva aplicación.
- **Combinación:** todas.
- **Qué pasa:** en cuatro secciones de Ajustes «Guardar» está apagado en gris —bien contrastado,
  6,4:1— **pero sin la razón al lado**; en la quinta, Tarifas, «Guardar tarifas» está encendido sin
  que se haya tocado nada. Fuera de Ajustes, «Auditar selección» está encendido con cero unidades y
  «Crear e inventariar» con el repositorio sin elegir. Y la casilla «Importar el baseline al crear
  la aplicación» está deshabilitada y tampoco dice por qué.
- **Qué debería pasar:** o el primario se apaga cuando no puede hacer nada y lleva su razón al lado,
  o se queda encendido; pero lo mismo en las cinco secciones y en las tres vistas.
- **Evidencia:** `UI-0038.png` (de `banco/vistas/dark-completa/11-ajustes.png`) — «Guardar» gris, sin texto al
  lado. Contra `banco/vistas/dark-1280/11c-ajustes-tarifas.png` («Guardar tarifas» azul).
- **Principio:** D-944.4.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-09). *Posible duplicado de UI-0022.*

### UI-0039 — La acción destructiva es maciza en una vista y perfilada en otra

- **Vista y lugar:** Cuenta («Desconectar»), Ajustes › Avanzado («Restablecimiento de fábrica»),
  Arreglo asistido y su cierre («Descartar todo»).
- **Combinación:** todas.
- **Qué pasa:** D-999 §4 dejó la regla escrita —perfilado por defecto, macizo solo cuando la acción
  de ese color **es** la acción de la vista—. «Desconectar» la cumple; «Restablecimiento de fábrica»
  es macizo `#F2645E`/`#BA3630` y acaba siendo lo más saturado de una pantalla cuyo primario está
  apagado en gris. Y en el cierre del arreglo «Descartar todo» sigue macizo arriba a la derecha
  mientras el paso siguiente real, «Verificar ahora», está abajo y en el azul de la librería.
- **Qué debería pasar:** los dos perfilados; el macizo se reserva para cuando destruir es lo que se
  ha venido a hacer, que es el diálogo de confirmación.
- **Evidencia:** `UI-0039.png` (de `banco/vistas/dark-completa/11e-ajustes-avanzado.png`, la vista entera: hay
  que ver el rojo macizo y el «Guardar» gris a la vez). También `banco/vistas/dark-completa/09-cuenta.png` y
  `banco/densas/dark-completa-07-arreglo-cierre.png`.
- **Principio:** D-944.4, D-999 §4.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-13).

### UI-0040 — Los bloques del mismo nivel de Ajustes acaban en tres x distintas

- **Vista y lugar:** Ajustes › los bloques a ancho completo de Apariencia, Tarifas y Avanzado.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** medidos los bordes derechos dentro del mismo armazón: el aviso de Apariencia acaba
  en **x = 1441**, la tabla de Tarifas en **x = 1460** y la tarjeta «Zona peligrosa» en **x = 1400**.
  Tres topes distintos para bloques del mismo nivel de la misma vista, y ninguna de las dos
  diferencias (41 y 19) es múltiplo de 4. En esas capturas no hay barra de desplazamiento que lo
  explique.
- **Qué debería pasar:** el tope lo pone la columna, no cada bloque.
- **Evidencia:** `UI-0040.png` (de `banco/vistas/dark-completa/11e-ajustes-avanzado.png`, la vista entera) frente
  a `banco/vistas/dark-completa/11d-ajustes-apariencia.png` y `banco/vistas/dark-completa/11c-ajustes-tarifas.png`.
- **Principio:** D-944.3, D-946, D-951.
- **Gravedad:** media.
- **Origen:** lupa 1 (A1-19). *La lupa 1 dio dos topes (1441 y 1400); la consolidación midió los
  tres y encontró que Tarifas tiene el suyo (1460). El hallazgo es algo peor de lo reportado.*

### UI-0041 — En «Nueva aplicación» el botón que crea la aplicación se va con el scroll, y no hay por dónde cancelar

- **Vista y lugar:** Nueva aplicación › el pie del formulario.
- **Combinación:** todas; a 1280 el botón no se ve en ningún momento sin desplazar.
- **Qué pasa:** «Crear e inventariar» es la última fila de un formulario que hace scroll. A 1920
  queda a ras del borde inferior; a 1280 está fuera de la pantalla y nada anuncia que exista. Es el
  defecto que D-987 describió para Ajustes y que D-1000 §1 resolvió allí fijando la fila de acciones
  al pie de la columna: **la otra pantalla de formulario no recibió el mismo tratamiento**. Y no hay
  «Cancelar»: para salir hay que usar la flecha o la miga, mientras el raíl marca «Portafolio» como
  entrada activa — la entrada resaltada del menú es, literalmente, el botón que abandona el
  formulario sin avisar.
- **Qué debería pasar:** la misma fila de acciones que Ajustes, pegada al pie de la columna, con su
  «Cancelar» al lado.
- **Evidencia:** `UI-0041.png` (de `banco/vistas/dark-1280/02-nueva-aplicacion.png`) — el formulario acaba en
  «Stack / Detectar stack» y no hay ningún botón de acción a la vista.
- **Principio:** D-944.1, D-944.8, D-987, D-1000 §1.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-16).

### UI-0042 — Cada salto devuelve el foco a la raíz: de 13 a 22 paradas antes del primer control

- **Vista y lugar:** toda la aplicación; medido en Portafolio, Hallazgos y Ajustes.
- **Combinación:** todas.
- **Qué pasa:** al navegar con el teclado el foco **no va a la página nueva ni se queda en la
  entrada pulsada**: vuelve al elemento ventana. Desde ahí, llegar al primer control del contenido
  cuesta **14 paradas en Portafolio, 13 en Hallazgos y 22 en Ajustes**, de las cuales 6 o 7 por
  ciclo son contenedores que no hacen nada (las `[List]` de los grupos del raíl y de la miga, una
  `[List]` fuera de pantalla, el `Pane` del scroll). Y el recorrido no sigue el orden visual: la
  primera parada es el botón de plegar, la **segunda es la fila de cuenta del pie del raíl** —unos
  900 px más abajo— y solo después vienen las entradas del menú.
- **Qué debería pasar:** al cambiar de página el foco entra en la página; los contenedores no paran
  el foco; y en el raíl se tabula de arriba abajo.
- **Evidencia:** `UI-0042.png` (de `banco/vistas/dark-completa/11-ajustes.png`, la vista donde se contaron las 22
  paradas). El recorrido completo, medido sobre `dist\Atalaya.exe`.
- **Principio:** D-944.6 (la navegación conserva el estado, también el del foco), D-944.8.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-07). *Las 28 paradas del ciclo de Ajustes se midieron dos veces, por el
  banco y por la lupa: coinciden.*

### UI-0043 — El bloque de sistema del raíl se mueve hasta 137 px entre vistas

- **Vista y lugar:** el raíl, comparando vistas.
- **Combinación:** todas.
- **Qué pasa:** el bloque de la aplicación aparece, desaparece y crece (1, 2 o 3 entradas), y con él
  bajan «Cuenta», «Ajustes» y «Acerca de». Medido: «Cuenta» está en y=299 en Portafolio, en y=356
  en el Inventario, en y=396 con una sesión y en y=436 con sesión y arreglo. **137 px de recorrido**
  para tres entradas que se aprenden de memoria y se pulsan sin mirar. D-954 se preocupó de que la
  entrada activa no bailara tres píxeles; esto es lo mismo a escala de cuarenta veces.
- **Qué debería pasar:** el bloque de sistema queda anclado al pie del raíl —como ya lo está la fila
  de cuenta— y lo que crece es el hueco de en medio; o el bloque de la aplicación reserva su sitio.
- **Evidencia:** `UI-0043.png` (de `banco/vistas/dark-completa/03-inventario.png`, el raíl entero) contra
  `banco/vistas/dark-completa/01-portafolio.png`, `banco/densas/dark-completa-05-sesion-en-vivo.png` y
  `banco/densas/dark-completa-06-arreglo-asistido.png`.
- **Principio:** D-944.7, D-954.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-08).

### UI-0044 — La miga no tiene el mismo grano: en tres sitios no dice en qué página estás

- **Vista y lugar:** la barra de la miga, comparada entre las 18 vistas del recorrido.
- **Combinación:** todas.
- **Qué pasa:** tres desviaciones sobre la misma regla:
  1. **Inventario** acaba en el nombre de la aplicación y no dice «Inventario» (`Portafolio ›
     XBLAST`), cuando todas las demás acaban en su página. La documentación de `Crumbs` dice
     «Portafolio › XBLAST › Inventario» y el código salta el último eslabón.
  2. **Las cinco secciones de Ajustes tienen la misma miga.** Pero D-985 hizo de la sección un
     **destino** —Métricas enlaza a «Ajustes → Tarifas»—, así que se aterriza en un sitio que la
     miga no sabe nombrar.
  3. **El informe abierto tiene la miga de la lista**, idéntica a la de `06-informes.png`: la miga
     no distingue leer un informe de mirar la lista.
- **Qué debería pasar:** la miga acaba siempre en la página que estás mirando, con el mismo grano.
- **Evidencia:** `UI-0044.png` (de `banco/vistas/dark-completa/03-inventario.png`) — la miga de dos eslabones.
  También las cinco capturas `11-ajustes.png` … `11e-ajustes-avanzado.png` con la misma miga, y
  `06-informes.png` frente a `07-informe-abierto.png`.
- **Principio:** D-944.6, D-955, D-985.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-09).

### UI-0045 — El raíl nunca dice de qué aplicación es su bloque

- **Vista y lugar:** el raíl (bloque de la aplicación) y la cabecera del Arreglo asistido.
- **Combinación:** todas.
- **Qué pasa:** desde D-1000 §2 el raíl no pinta rótulos de grupo, y con ellos se fue el único sitio
  donde ponía el nombre de la aplicación. Hoy el bloque dice «Inventario», «Última sesión», «Último
  arreglo» **sin decir de quién son**: el raíl es el único mapa permanente de la ventana y no
  contesta «¿sobre qué aplicación estoy trabajando?». Y donde sí se dice, se dice de tres maneras a
  la vez: en Arreglo asistido la miga y la cabecera ponen `atalayabanco-app-for-tests` y la tira del
  pie pone `atalayabanco`.
- **Qué debería pasar:** el bloque lleva el nombre de la aplicación, y la aplicación se nombra igual
  en la miga, en la cabecera y en el pie.
- **Evidencia:** `UI-0045.png` (de `banco/densas/dark-completa-06-arreglo-asistido.png`, el raíl entero) — el
  bloque central sin nombre. Los tres nombres, en la misma captura completa.
- **Principio:** D-944.6, D-944.7, D-953.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-10).

### UI-0046 — El marcador de «estás aquí» del raíl no se pinta: no cabe en su carril

- **Vista y lugar:** el raíl y la lista de secciones de Ajustes, que comparten plantilla.
- **Combinación:** todas.
- **Qué pasa:** el `DataTrigger` de `IsActive` pinta el marcador de `Brush.Primary.Fill`, y **no
  aparece un solo píxel del primario en el raíl de ninguna de las cuatro combinaciones**, ni
  desplegado ni plegado. **La raíz, encontrada en la consolidación:** el carril es
  `Rail.MarkerLane` = 8 y el `Border` pide `Rail.MarkerWidth` 3 más `Pad.XXS` 4 a cada lado = **11**.
  Es la aritmética de carriles de D-966 fallando por tercera vez (D-963 y D-999 §3 fueron las dos
  primeras). Toda la señal de «estás aquí» recae en el relleno de la pastilla, que mide **1,27:1 en
  oscuro y 1,21:1 en claro** contra el fondo del raíl; desplegado lo salva el texto en seminegrita,
  y **plegado no hay texto**.
- **Qué debería pasar:** o el carril crece a 11 y la barra se pinta, o el marcador se retira del
  sistema y el «estás aquí» se resuelve con un relleno que se vea.
- **Evidencia:** `UI-0046.png` (de `banco/vistas/dark-completa/03-inventario.png`) — la entrada activa con los
  8 px vacíos a su izquierda. Barrido de píxel hecho sobre las cuatro capturas.
- **Principio:** D-944.7, D-954, D-966.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-12). *Barrido reproducido y aritmética confirmada en la consolidación.*

### UI-0047 — Plegado, «Inventario» lleva el icono del plegar, y la sesión y el arreglo no llevan icono

- **Vista y lugar:** el raíl plegado a iconos.
- **Combinación:** todas.
- **Qué pasa:** dos entradas no se distinguen cuando se pierde el texto. El icono de **Inventario**
  son tres rayas horizontales y el botón de **plegar o desplegar el menú** también; la única
  diferencia es que la tercera raya de Inventario es más corta, y plegados quedan los dos en la
  misma columna de 40 px separados por 40 px de alto. Y **«Sesión en vivo» y «Arreglo asistido» no
  tienen icono**: la plantilla dice, con su comentario, que «lo que late sustituye al icono en vez
  de sumarse», así que en el canal de 24 px llevan un punto verde de 9 px. Plegado, la entrada de lo
  que está corriendo queda como un punto suelto, indistinguible del piloto del avatar dos filas más
  abajo.
- **Qué debería pasar:** un icono por entrada, distinguible del botón de plegar; y el «está
  corriendo» se dice con el color o el latido **del icono**, no sustituyéndolo.
- **Evidencia:** `UI-0047.png` (de `banco/vistas/dark-completa/12-rail-plegado.png`) — la columna de iconos
  entera, con el plegar arriba. También `banco/vistas/dark-completa/03-inventario.png` y las densas.
- **Principio:** D-944.7, D-950, D-966.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-13). *La sustitución del icono por el punto está confirmada en
  `Styles.xaml`, con su comentario.*

### UI-0048 — La flecha de volver se pinta igual apagada que encendida

- **Vista y lugar:** la barra de la miga › la flecha «‹» de la izquierda.
- **Combinación:** todas.
- **Qué pasa:** recién arrancada la aplicación, en Portafolio, la flecha está **deshabilitada**
  (`IsEnabled=False`, y ni siquiera entra en la tabulación) y se pinta con **el mismo color** que
  cuando funciona: medido en el recorte, `#A4ABB8` en los dos casos, con el mismo antialias. Es la
  primera cosa arriba a la izquierda de la primera pantalla, tiene forma de control y no hace nada.
  Y en Portafolio la miga es un solo eslabón, así que la flecha es el único gesto de «atrás» que se
  ofrece ahí.
- **Qué debería pasar:** un control apagado se lee como apagado —D-983 §4 lo resolvió para los
  botones y esta flecha se quedó fuera— o, si no hay a dónde volver, no se pinta.
- **Evidencia:** `UI-0048.png` (de `banco/vistas/dark-completa/01-portafolio.png`) — la flecha y el único
  eslabón. Comparada píxel a píxel con la misma zona de `03-inventario.png` y `06-informes.png`.
- **Principio:** D-944.4, D-949.
- **Gravedad:** media.
- **Origen:** lupa 2 (A2-14). *Comparación de color reproducida en la consolidación: idénticas.*

### UI-0049 — «Primary.Soft» significa cuatro cosas distintas, y tres caben en una captura

- **Vista y lugar:** el raíl (entrada activa), Ajustes (sección activa), los avisos informativos y
  la pastilla de gravedad «baja».
- **Combinación:** todas.
- **Qué pasa:** el mismo relleno `#243554`/`#D6E1F5` se usa para «estás aquí», para «esto es
  información» y para «gravedad baja». En Ajustes › Apariencia los tres primeros aparecen juntos;
  en Hallazgos el mismo azul es a la vez la entrada del raíl y la pastilla «4 Baja» de la primera
  fila. En claro se suma que `Sev.Low` y `Primary.Ink` son literalmente el mismo valor (`#2E5FC3`),
  así que la cifra «20 Bajas» de Portafolio y el enlace «Expandir todo» de Inventario son el mismo
  color.
- **Qué debería pasar:** el azul suave se reserva para una cosa; la gravedad baja tiene su propio
  tono en la escala.
- **Evidencia:** `UI-0049.png` (de `banco/vistas/dark-completa/11d-ajustes-apariencia.png`, la vista entera) —
  raíl activo, sección activa y aviso azul, los tres `#243554`.
- **Principio:** D-944.4.
- **Gravedad:** media.
- **Origen:** lupa 3 (A3-07).


## Gravedad baja

Pulido. Nada de esto impide trabajar; todo esto se nota.

### UI-0050 — «Temática» es pastilla en dos vistas y texto corrido en otras dos

- **Vista y lugar:** Portafolio (tarjeta) e Inventario (resumen del ciclo) frente a Hallazgos ›
  ficha (metadatos) e informe abierto.
- **Combinación:** todas.
- **Qué pasa:** el mismo valor —«General»— se pinta como pastilla teñida en Portafolio y en el
  resumen del ciclo, y como texto plano en la fila «Temática» de la ficha y dentro de la línea
  «Ciclo: 1 · Temática: General · Modo: Lotes» del informe. Quien busca la temática la busca en dos
  formas distintas según por dónde entre.
- **Qué debería pasar:** un dato, un tratamiento. Si la temática es una etiqueta, es pastilla en
  todas partes; si es un metadato, no es pastilla en ninguna.
- **Evidencia:** `UI-0050.png` (de `banco/vistas/dark-completa/01-portafolio.png`, la pastilla) frente a
  `banco/vistas/dark-completa/05-hallazgo-ficha.png` (la fila «Temática · General») y
  `banco/vistas/dark-completa/07-informe-abierto.png`.
- **Principio:** D-944.8, D-989.
- **Gravedad:** baja.
- **Origen:** lupa 1 (A1-24). *Posible duplicado de UI-0019: es la misma pastilla.*

### UI-0051 — El cero se pinta de peligro

- **Vista y lugar:** Portafolio › azulejo «Críticas» y la cifra de la tarjeta; Métricas › pastilla
  «Crít 0».
- **Combinación:** todas.
- **Qué pasa:** con cero críticas, el azulejo conserva el borde rojo, la cifra «0» va en `Sev.Crit`
  y la pastilla «Crít 0» sigue teñida de rojo. El color dice «hay una crítica» donde el número dice
  lo contrario, y es el primer sitio donde cae la vista al abrir la aplicación.
- **Qué debería pasar:** un recuento a cero se pinta en neutro; el color de gravedad aparece cuando
  hay algo de esa gravedad.
- **Evidencia:** `UI-0051.png` (de `banco/vistas/light-completa/01-portafolio.png`) — el primer azulejo, con su
  borde rojo y su «0».
- **Principio:** D-944.4 («color con significado, y solo con significado»).
- **Gravedad:** baja.
- **Origen:** lupa 3 (A3-17).

### UI-0052 — El aviso ámbar unas veces lleva icono y otras no

- **Vista y lugar:** Métricas, Ajustes › Tarifas y Ajustes › Apariencia (con icono) frente a
  Hallazgos › ficha › tarjeta «Código» y el cierre del arreglo (sin icono).
- **Combinación:** todas.
- **Qué pasa:** el mismo control de aviso aparece en tres formas: relleno + icono + enlace; relleno
  sin icono (el «El código de la línea 145 ya no es el que se auditó» de la ficha); y relleno sin
  icono ni cabecera en el cierre del arreglo. En los dos últimos, «esto es un aviso» viaja solo en
  el color.
- **Qué debería pasar:** el aviso es un patrón: icono, texto y —si la hay— la acción, siempre.
- **Evidencia:** `UI-0052.png` (de `banco/vistas/dark-1280/05-hallazgo-ficha.png`) — la banda ámbar dentro de la
  tarjeta «Código», sin icono. Contra `banco/vistas/dark-completa/08-metricas.png`, que sí lo lleva.
- **Principio:** D-944.4, D-991.
- **Gravedad:** baja.
- **Origen:** lupa 3 (A3-16).

### UI-0053 — Dos barras de estado seguidas dicen lo mismo con dos notaciones

- **Vista y lugar:** Sesión en vivo › la barra de resumen y, justo debajo, la tira de estado de la
  carcasa.
- **Combinación:** todas.
- **Qué pasa:** a 44 px de distancia se lee «**Unidad 3 de 6** · 00:07 · 7 llamadas · coste no
  calculable…» y «Auditando atalayabanco · **unidad 3/6** · pasada 2». El mismo dato, dos veces y
  escrito de dos maneras. La repetición gasta el único renglón que queda para lo que sí cambia.
- **Qué debería pasar:** el progreso se dice una vez, en la barra de la vista, y la tira de la
  carcasa dice lo que la vista no puede decir (qué aplicación, si el hub responde).
- **Evidencia:** `UI-0053.png` (de `banco/densas/dark-completa-05-sesion-en-vivo.png`) — las dos barras, una
  encima de la otra.
- **Principio:** D-944.8.
- **Gravedad:** baja.
- **Origen:** lupa 1 (A1-23).

### UI-0054 — La tarjeta de permiso ya contestada sigue pintada de aviso

- **Vista y lugar:** Arreglo asistido › conversación, tarjeta «El agente pide permiso».
- **Combinación:** todas.
- **Qué pasa:** la tarjeta resuelta («Tu respuesta: Autorizar») y la que sigue esperando («El agente
  necesita que decidas») tienen el mismo relleno `Warning.Soft` y el mismo borde ámbar. Al bajar por
  el hilo, cada permiso ya contestado vuelve a reclamar atención, y lo único que las distingue es
  una línea de texto pequeña.
- **Qué debería pasar:** contestada la pregunta, la tarjeta baja a superficie neutra y deja el ámbar
  para la que sigue abierta.
- **Evidencia:** `UI-0054.png` (de `banco/densas/dark-completa-06-arreglo-asistido.png`) — las dos tarjetas
  ámbar, la de arriba ya contestada.
- **Principio:** D-944.4.
- **Gravedad:** baja.
- **Origen:** lupa 3 (A3-18).

### UI-0055 — A 1280, las dos filas de la barra de filtros de Informes no comparten margen izquierdo

- **Vista y lugar:** Informes › la barra de filtros, cuando se parte en dos filas.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la primera fila empieza en **x = 281** (el borde de «Buscar…») y la segunda en
  **x = 274** (la `P` de «Periodo:»). Siete píxeles, que además no son múltiplo de 4. En Hallazgos,
  con la misma barra partida, las dos filas empiezan las dos en x = 285.
- **Qué debería pasar:** el ritmo lo pone la barra, como quedó en D-984, y vale igual cuando envuelve.
- **Evidencia:** `UI-0055.png` (de `banco/vistas/dark-1280/06-informes.png`) — la tarjeta de filtros con sus dos
  filas. Contra `banco/vistas/dark-1280/04-hallazgos.png`.
- **Principio:** D-944.3, D-984.
- **Gravedad:** baja.
- **Origen:** lupa 1 (A1-21).

### UI-0056 — «8 informes» vive a 1.600 px del título que cuenta

- **Vista y lugar:** Informes › cabecera.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** el recuento se pinta en el extremo derecho (x≈1832) a la altura del subtítulo,
  mientras el título está en x=264. En Hallazgos el mismo dato («111 hallazgos») va bajo el título,
  y en Portafolio también («1 aplicación · 111 hallazgos abiertos»). Tres listas hermanas, dos
  sitios para el mismo dato; y el que está solo es el que hay que ir a buscar al otro extremo.
- **Qué debería pasar:** el recuento de una lista va donde va en las otras dos.
- **Evidencia:** `UI-0056.png` (de `banco/vistas/dark-completa/06-informes.png`) — la cabecera entera, con el
  título a la izquierda y «8 informes» al otro extremo.
- **Principio:** D-944.8.
- **Gravedad:** baja.
- **Origen:** lupa 1 (A1-22).

### UI-0057 — «+15 / −0» se pinta igual que «sin cambios»

- **Vista y lugar:** Informes › columna «Hallazgos».
- **Combinación:** todas.
- **Qué pasa:** la columna que dice si una sesión encontró algo escribe «+15 / −0» y «sin cambios»
  con el mismo color y el mismo peso, así que la tabla no se puede recorrer buscando las sesiones
  que trajeron trabajo. En Métricas, el delta equivalente («▲ 2 vs periodo anterior») sí va en verde.
- **Qué debería pasar:** el signo que ya está escrito lleva su color, o al menos «sin cambios» baja
  a tinta apagada.
- **Evidencia:** `UI-0057.png` (de `banco/vistas/dark-completa/06-informes.png`) — la columna entera, con las
  tres filas que sí tienen delta entre las cinco que no.
- **Principio:** D-944.4.
- **Gravedad:** baja.
- **Origen:** lupa 3 (A3-19).

### UI-0058 — Ni la ficha ni el informe ofrecen la vuelta a su lista, y el informe apila dos «volver»

- **Vista y lugar:** ficha de un hallazgo e informe abierto › barra de miga y cabecera de la vista.
- **Combinación:** todas.
- **Qué pasa:** en la ficha la miga es `Portafolio › XBLAST › BUG-0008` y no contiene «Hallazgos»,
  que es de donde vienes; su eslabón intermedio lleva al **inventario**, no a la lista, así que la
  miga no sirve para volver a los hallazgos filtrados y solo queda la flecha. En el informe abierto
  pasa lo mismo y además la vista añade **su propio «← Volver»** dentro del contenido, 95 px por
  debajo de la flecha «‹» de la carcasa: dos controles de volver, con dos formas y dos alcances,
  uno encima del otro. En el arreglo hay un tercer patrón: «Volver al hallazgo», en la cabecera.
- **Qué debería pasar:** un solo gesto de volver, el de la carcasa, y una miga que incluya la lista
  de la que cuelga la ficha.
- **Evidencia:** `UI-0058.png` (de `banco/vistas/dark-completa/07-informe-abierto.png`) — la flecha «‹» y el
  botón «← Volver» en la misma columna, uno debajo del otro.
- **Principio:** D-955.
- **Gravedad:** baja.
- **Origen:** lupa 2 (A2-18).

### UI-0059 — El informe escribe su propia tipografía: 13,5 px, interlineado 1,55 y familia a mano

- **Vista y lugar:** Informes › informe abierto, el cuerpo y los bloques de código.
- **Combinación:** todas.
- **Qué pasa:** `MarkdownFlowDocument` sí lee `FontSize.Body` de los recursos —el tamaño base es el
  del sistema—, pero además fija a mano tres cosas que el sistema ya tiene escritas: la familia
  (`new FontFamily("Segoe UI")`), el interlineado (`BaseFontSize * 1.55` = 23,25 px, cuando
  `LineHeight.Body` es 21, que es el 1,4 de D-944.3) y, en los bloques de código,
  `BaseFontSize - 1.5` = **13,5 px**, que no es ninguno de los tres tamaños de la escala
  (15 / 13 / 12) más su interlineado propio de 1,35. Es la vista de la que D-993 dijo que se leería
  «con la escala del sistema».
- **Qué debería pasar:** familia, tamaños e interlineado salen de `Tokens.xaml`, como en el resto.
- **Evidencia:** `UI-0059.png` (de `banco/vistas/dark-completa/07b-informe-con-hallazgos.png`) — el cuerpo y el
  fragmento monoespaciado. El origen exacto, en `Services/MarkdownFlowDocument.cs` líneas 115–117 y
  341–345.
- **Principio:** D-944.3 («nada se escribe con un tamaño ad hoc»; «interlineado 1,4»), D-993.
- **Gravedad:** baja.
- **Origen:** **la consolidación**. Ninguna de las tres lupas llegó a esto; salió al buscar la raíz
  de UI-0031. *Posible duplicado de UI-0031: mismo fichero, dos problemas.*

### UI-0060 — La fila «Stack» rompe el ritmo del formulario de alta

- **Vista y lugar:** Nueva aplicación › «El clon en esta máquina», fila «Stack».
- **Combinación:** todas.
- **Qué pasa:** en las tres filas de arriba el control arranca en x≈997 y el botón de apoyo se
  alinea a la derecha del bloque, terminando en x≈1513. En la fila «Stack» el valor («Unknown»)
  arranca en x≈997 pero cae **22 px por debajo** de su rótulo —las otras filas los alinean— y
  «Detectar stack» se queda en x≈1100, sin compartir x con nada. Tres reglas en cuatro filas.
- **Qué debería pasar:** una columna de rótulos, una de controles y una de botones de apoyo, y las
  cuatro filas dentro de ellas.
- **Evidencia:** `UI-0060.png` (de `banco/vistas/dark-completa/02-nueva-aplicacion.png`) — las filas «Carpeta» y
  «Stack», una debajo de la otra.
- **Principio:** D-944.3, D-944.8, D-994.
- **Gravedad:** baja.
- **Origen:** lupa 1 (A1-20).

### UI-0061 — Dos controles del raíl llevan a «Cuenta», y el de abajo no tiene nombre

- **Vista y lugar:** el raíl › entrada «Cuenta» y fila de usuario del pie.
- **Combinación:** todas.
- **Qué pasa:** la fila del avatar del pie abre Cuenta —su tooltip lo dice— y la entrada «Cuenta»
  del bloque de sistema también. Son dos entradas para el mismo sitio en el mismo menú, y **la de
  abajo no marca «estás aquí»** cuando estás en Cuenta (lo marca la de arriba), no tiene nombre de
  accesibilidad y es la **segunda parada de tabulación** de toda la ventana, antes que las entradas
  del menú que tiene encima.
- **Qué debería pasar:** o la fila de usuario deja de navegar y es solo identidad y piloto, o la
  entrada «Cuenta» se retira del bloque de sistema; y en cualquier caso el pie se tabula al final.
- **Evidencia:** `UI-0061.png` (de `banco/vistas/dark-completa/09-cuenta.png`) — el raíl entero: «Cuenta»
  resaltado arriba y la fila «alopezciller» sin resaltar abajo.
- **Principio:** D-944.7.
- **Gravedad:** baja.
- **Origen:** lupa 2 (A2-19).


---

# Bloque 2 — Lo que proponemos nosotros

**Esto no son hallazgos.** Nada de lo que sigue incumple un principio, ninguna lleva gravedad y
ninguna entra en el recuento. Es lo que cada auditor —y la consolidación— haría como diseñador
aunque el sistema no lo exija. Se agrupan por parentesco, no por quién las escribió; cada una lleva
su origen.

Dos de ellas **contradicen una decisión escrita**, y lo dicen: P-17 (el rótulo del raíl, contra
D-1000 §2) y P-28 (el banco de capturas, contra D-977).

---

## A · El sistema, donde le falta una pieza

### P-01 — Una regla de recorte para toda la aplicación, y que se pueda comprobar

- **Qué.** Tres primitivas y nada más: `Text.Path` (acorta por el MEDIO y conserva los extremos),
  `Text.Name` (elipsis al final) y `Text.Chip` (no se recorta: si no cabe, no se pinta). Todo lo que
  hoy se recorta a pelo pasa a una de las tres, y un test recorre los XAML buscando texto con
  `TextTrimming` o con ancho tope que no use ninguna.
- **Por qué.** UI-0009 y UI-0011 son el mismo defecto en dos sitios, y D-983 §5 ya lo arregló una
  vez en la cabecera del bloque de código: el arreglo no se generalizó y volvió a aparecer a 150 %.
  Un nombre recortado que miente es el fallo más caro de esta interfaz porque no se nota. Alcanza a
  las ocho vistas que enseñan rutas.
- **Coste.** Un control adjunto o tres estilos en `Tokens.xaml`, y un barrido por los XAML que hoy
  recortan (sesión, arreglo, ficha, inventario, informes). No arrastra lógica.
- *Origen: lupa 1.*

### P-02 — Un patrón de página, escrito y comprobado

- **Qué.** Un `PageShell` con tres huecos —título, acciones de vista, cuerpo— que fije el margen
  (264), el tope de medida y dónde va el recuento. Cuenta, Nueva aplicación y Acerca de declaran
  «cuerpo centrado» como una propiedad del shell, en vez de reinventar el margen.
- **Por qué.** UI-0035 y UI-0056 tienen la misma raíz: no hay un sitio donde esté escrito dónde
  empieza una página. Con el shell, la próxima vista nace alineada y la excepción de «Acerca de» se
  declara en vez de imitarse mal.
- **Coste.** Un control y las once vistas reencajadas. Es el cambio más caro de esta lista y el
  único que evita que esto vuelva.
- *Origen: lupa 1.*

### P-03 — Un solo patrón de superposición, con foco, trampa y Escape

- **Qué.** Un estilo `Overlay` que gobierne las tres cosas que hoy flotan con reglas distintas: el
  cajón del resumen del ciclo, los desplegables de tarjeta y los avisos emergentes. Contrato único:
  al abrirse toma el foco, lo retiene mientras está abierto, Escape lo cierra y al cerrarse lo
  devuelve a quien lo abrió. Los avisos, además, no se pintan encima de un control: se apilan en un
  carril propio.
- **Por qué.** Arregla UI-0001, UI-0003 y UI-0012 de una vez y evita el cuarto caso que vendrá. Hoy
  cada superposición se comporta distinta porque cada una la escribió su vista, que es el argumento
  de D-946 y D-951 aplicado al comportamiento en vez de a las medidas.
- **Coste.** Un `Behavior` o un `ContentControl` en `Styles.xaml` y tres sitios de uso. Sin lógica
  de negocio.
- *Origen: lupa 2 (P-A2-03), con el carril de avisos de la lupa 1 (P-A1-02) fundido dentro.*

### P-04 — El anillo de foco entra en la paleta

- **Qué.** Dos claves nuevas (`Focus.Ring`, `Focus.RingOffset`), un `FocusVisualStyle` propio de
  2 px con el radio del control que rodea, y prohibirlo escrito a mano con el test que ya vigila los
  colores (D-983 §8).
- **Por qué.** UI-0015. Es lo último de la interfaz que sigue pintando Windows y no Atalaya, y es lo
  único que sobrevivió a las tres revisiones sin que nadie lo viera: 1,21:1 no se ve en una captura,
  se ve midiendo. Es la lección de D-945.
- **Coste.** `Palette.*.xaml`, `Tokens.xaml`, `Styles.xaml`. Cero vistas.
- *Origen: lupa 2.*

### P-05 — Una escala de gravedad con cuatro rellenos propios

- **Qué.** Añadir `Sev.*.Soft` (cuatro rellenos) además de las cuatro tintas que ya hay, y que
  `Pill.Sev` los use en vez de tomar prestados `Danger.Soft`, `Warning.Soft` y `Primary.Soft`. Con
  una regla de prueba que compruebe que los cuatro rellenos y las cuatro tintas son distintos entre
  sí en los dos temas, y que ninguno coincide con un color de otra familia.
- **Por qué.** Cierra de raíz UI-0034 (crítica y alta con el mismo fondo) y UI-0049 (baja = azul de
  primario), y convierte «la gravedad se ve sin leer» —que es una decisión, D-973— en algo que falla
  solo cuando se rompe. Hoy la única forma de descubrirlo es mirar una captura y contar manchas.
- **Coste.** Ocho claves de paleta, un estilo, ninguna vista: todas piden la pastilla, no el color.
- *Origen: lupa 3.*

### P-06 — Un `StatusPill`, y una página de muestras donde se vean todos

- **Qué.** Un control con los estados que existen —activo, por revisar, arreglado sin verificar,
  silenciado, caducado, falso positivo, resuelto— con su color, su forma y su palabra fijados en un
  solo `Style`; y una página de sistema, oculta y solo para desarrollo, que los pinte todos en fila
  en los dos temas.
- **Por qué.** Hoy «activo» es una pastilla azul en la ficha, una opción de un desplegable en
  Hallazgos y una palabra en negrita al final de una línea de metadatos en el diálogo de patrones.
  Nadie puede contestar «¿cómo se ve un hallazgo caducado?» sin fabricar el dato; con la página de
  muestras se contesta mirando. Es el argumento que llevó a `EmptyState` (D-991): siete sitios que
  necesitan lo mismo divergen si no hay un control.
- **Coste.** Un control y un estilo; toca la ficha, la lista de Hallazgos, el diálogo de silenciados
  y el informe. La página de muestras es un XAML sin lógica y no entra en el instalador.
- *Origen: lupa 3.*

### P-07 — Un aviso sabe a qué alcanza, y se coloca por eso

- **Qué.** Dar al control de aviso dos tamaños declarados —**de pantalla** (ancho de columna, icono,
  cabecera opcional, acción) y **de bloque** (dentro de una tarjeta, icono e hilo de texto)— y
  prohibir el tercero que existe hoy de facto, el ámbar sin icono.
- **Por qué.** D-997 §8 ya sacó una conclusión que vale como regla general: «lo que abarca un aviso
  decide dónde va». Convertirla en dos variantes con nombre cierra UI-0052 y evita que el próximo
  aviso invente una cuarta forma. Y le da sitio natural al aviso del cierre del arreglo, que hoy es
  de pantalla y se pinta como de bloque.
- **Coste.** Una variante más en `Styles.xaml` y los cinco sitios que ya usan `Notice.*`.
- *Origen: lupa 3.*

### P-08 — Cifras alineadas en todas las tablas, por defecto y no por vista

- **Qué.** Un estilo `Cell.Number` (derecha + `NumeralAlignment="Tabular"`) usado por Informes,
  Tarifas, Métricas y los recuentos del inventario. Quien escriba una columna de números no tiene
  que acordarse.
- **Por qué.** UI-0030. La alineación de cifras es la diferencia entre poder recorrer una columna de
  costes y tener que leerla número a número; se resolvió en Tarifas y se quedó ahí.
- **Coste.** Un estilo y tres vistas.
- *Origen: lupa 1.*

### P-09 — Métricas y Portafolio comparten el mismo azulejo

- **Qué.** Un `StatTile` único —rótulo arriba a 13, cifra a 30, una línea secundaria opcional— usado
  por Portafolio, Métricas y el resumen del ciclo. Hoy hay tres dibujos parecidos y ninguno igual:
  Portafolio pone la cifra arriba y el rótulo debajo, Métricas al revés.
- **Por qué.** Es lo que hace que las tres pantallas de cifras se lean como la misma herramienta, y
  quita de en medio la discusión de si un azulejo se estira: la decide el panel, una vez.
- **Coste.** Un control en `Controls/`, tres vistas. Sin lógica.
- *Origen: lupa 1.*

---

## B · Lo que se puede medir en vez de mirar

### P-10 — Un barrido de «color que no sale de la paleta», sobre el píxel y no sobre el XAML

- **Qué.** El test de D-983 §8 mira el XAML convertido y prohíbe hexadecimales escritos a mano. El
  complementario: renderizar cada vista a PNG en los dos temas y comprobar que **el conjunto de
  colores distintos de cada captura está contenido en la paleta de ese tema**, con una lista blanca
  corta (capturas de código, logotipo, avatar).
- **Por qué.** Es la única prueba que habría visto UI-0005, UI-0008, UI-0010 y UI-0018 sin que un
  humano abriera la aplicación: ninguno de esos colores está escrito en un XAML convertido —vienen
  de controles de la librería, de estilos antiguos o de opacidades—, así que el test actual pasa en
  verde con la ficha llena de botones blancos. Los que se colarían en la primera ejecución:
  `#DDDDDD`, `#E07A2B`, `#D13A3A`, `#4A9EE0`, `#1E9BFA`, `#F44336`, `#383E47`.
- **Coste.** Un test sobre el arnés de capturas; comparación de histograma, sin dependencias. La
  lista blanca hay que mantenerla, y ése es el precio honesto: cada excepción obliga a justificarse
  por escrito.
- *Origen: lupa 3. Depende de P-28.*

### P-11 — Contraste medido sobre lo que se ve, no solo sobre los pares declarados

- **Qué.** Añadir a `PaletteContrastTests`, sobre las mismas capturas, una comprobación de las zonas
  que llevan **texto pequeño sobre color** (pastillas, botones macizos, avisos): recortar la caja,
  tomar el color dominante como fondo y el más alejado como tinta, y exigir 4,5:1.
- **Por qué.** Los seis fallos de AA de este informe —los cuatro contadores de Sesión en vivo,
  «Activo», «General», «completa», «Detener»— son todos de composición: el par declarado está bien y
  lo que se pinta encima no es el que se declaró. Un test de pares nunca los verá.
- **Coste.** Reutiliza el arnés de P-10; hay que anotar en cada vista qué cajas se miden, o
  derivarlo del árbol visual, que es más trabajo y más fiable.
- *Origen: lupa 3. Depende de P-28.*

### P-12 — Un test que le exija su pincel a cada color, y que no haya `DynamicResource` colgando

- **Qué.** Dos reglas de una línea cada una: **(a)** por cada `Color.X` de una paleta existe un
  `SolidColorBrush x:Key="Brush.X"` en las **dos**; **(b)** todo `{DynamicResource Brush.*}` que
  aparece en un XAML de `Views/`, `Controls/` o `Themes/` resuelve contra los diccionarios
  fusionados.
- **Por qué.** UI-0007 —seis referencias a un pincel que no existe, tinta negra en los dos temas y
  cuatro pastillas por debajo de AA— lo habría cazado la regla (b) el día que se escribió, y la (a)
  lo habría impedido antes. Es más barato que P-10 y P-11, no necesita capturas, corre en
  milisegundos y cubre una familia entera de fallos silenciosos: el recurso que no está. Es
  exactamente la clase de andamiaje que esta casa ya usa —el guardarraíl del prefijo estable
  (D-911), el banco de geometría (D-978)— aplicada al sitio donde todavía no hay ninguno.
- **Coste.** Un fichero de test. Cero producto. La (b) necesita un `Application` vivo o el mismo
  truco de fusión que ya usa `ViewLayout`.
- *Origen: **la consolidación**.*

---

## C · Navegación y ritmo de uso

### P-13 — Un selector de aplicación en la barra de la miga

- **Qué.** El eslabón de la aplicación se convierte en un desplegable: `Portafolio › XBLAST ▾ ›
  Hallazgos`. Al abrirlo, la lista de aplicaciones del portafolio, con búsqueda si pasan de diez.
  Elegir una **cambia la aplicación activa sin salir de la página**.

```
 ‹  Portafolio ›  XBLAST ▾ ›  Hallazgos
                  ┌──────────────────┐
                  │ ⌕ buscar…        │
                  │ • XBLAST      111│
                  │   AtalayaBanco  13│
                  │   Visor          0│
                  └──────────────────┘
```

- **Por qué.** Hoy cambiar de aplicación pasa **siempre** por Portafolio, que además borra la
  aplicación activa y hace desaparecer «Inventario» del raíl (UI-0017). Con un portafolio de veinte
  aplicaciones, comparar dos es un viaje de ida y vuelta por la raíz cada vez. Esto lo baja a dos
  clics desde cualquier vista, y le da a la miga un trabajo que hoy no tiene: la miga solo se lee.
- **Coste.** La barra de la miga, `MainViewModel.Shell` y `ActiveApp`. Arrastra lógica: hay que
  decidir qué hace cada página al cambiarle la aplicación debajo —Hallazgos e Informes ya tienen
  `SetApp`, la ficha tendría que volver a su lista—. Es la propuesta más cara de las veintiocho.
- *Origen: lupa 2.*

### P-14 — «Ir a…» (Ctrl+K) y números para el raíl

- **Qué.** `Ctrl+K` abre una búsqueda sobre todo lo que es un destino: las ocho páginas, las cinco
  secciones de Ajustes, las aplicaciones del portafolio y sus inventarios, y los informes por fecha.
  `Ctrl+1..8` van a las entradas del raíl en orden. `Ctrl+I`, al inventario de la aplicación activa.
- **Por qué.** Es la respuesta directa a UI-0042 y a UI-0017: hoy llegar al primer control de una
  página con el teclado cuesta entre 13 y 22 tabulaciones, y llegar a «Ajustes → Tarifas» son cuatro
  gestos de ratón. Ahorra más cuanto más crece el portafolio, que es donde la navegación actual se
  rompe.
- **Coste.** Una ventana, un servicio que enumere destinos y `InputBindings` en `MainWindow`. No
  toca ninguna vista: los destinos ya son datos (`NavGroups`, `SettingsSectionItem`).
- *Origen: lupa 2.*

### P-15 — «Saltar al contenido» como primera parada de tabulación

- **Qué.** Un enlace invisible hasta que recibe el foco, primera parada de la ventana, que lleva el
  foco al primer control de la página. Y quitar del recorrido los contenedores que hoy paran el foco
  sin hacer nada.
- **Por qué.** Es la mitad barata de UI-0042: sin tocar el orden general, baja de 22 paradas a 1 el
  coste de empezar a trabajar en Ajustes. Es un patrón conocido de la web que aquí no existe porque
  en escritorio nadie se lo plantea, y esta aplicación tiene una carcasa de catorce controles
  delante de cada página.
- **Coste.** `MainWindow.xaml` y un puñado de `KeyboardNavigation.IsTabStop="False"`.
- *Origen: lupa 2.*

### P-16 — Atalaya reabre donde la dejaste

- **Qué.** Guardar la última página y la última aplicación activa, y restaurarlas al arrancar, con
  la misma cautela que D-956 tiene con la ventana: si la aplicación ya no existe, se cae al
  Portafolio.
- **Por qué.** D-952 hace que la navegación conserve el estado **dentro de una sesión de ventana** y
  lo tira entero al cerrar. Una herramienta que se abre cada mañana para seguir con la misma
  aplicación arranca todos los días en la pantalla que menos falta hace. Y además es lo que haría
  que la aplicación activa dejara de ser un accidente del recorrido.
- **Coste.** `AppSettings`, `MainViewModel.InitializeAsync` y `SaveWindowPlacement`. Arrastra una
  decisión: si la última página era una sesión en vivo que ya terminó, se abre su cierre.
- *Origen: lupa 2.*

### P-17 — El bloque de la aplicación lleva su nombre en la raya que lo separa

- **Qué.** La línea de un píxel que D-1000 §2 puso entre bloques lleva, **solo en el bloque de la
  aplicación**, el nombre de la aplicación pegado a la izquierda, a 13 px, en tinta terciaria.
  Plegado, la raya se queda sola, como ahora.

```
 ─── XBLAST ────────────
  ▤  Inventario
  ⏱  Última sesión
```

- **Por qué.** UI-0045. **Contradice a medias D-1000 §2**, y el argumento nuevo es éste: los tres
  rótulos que se retiraron no eran la misma cosa. «TRABAJO» y «SISTEMA» no llevaban a ningún sitio y
  se fueron con razón; el nombre de la aplicación **sí dice algo**: de quién es todo lo que hay
  debajo. Retirarlo con los otros dos fue tirar el único rótulo que informaba. Y la objeción de
  D-1000 —que el rótulo desaparecía al plegar y el menú cambiaba de forma— no aplica: la raya
  sobrevive al plegado y el nombre se va con el texto de las entradas, que es coherente.
- **Coste.** El `DataTemplate` del grupo en `MainWindow.xaml` y una condición en `BuildRail`.
- *Origen: lupa 2.*

### P-18 — La flecha de volver, con su historial

- **Qué.** Mantener pulsada la flecha «‹» abre la pila de navegación —los últimos 20 que D-952 ya
  guarda— con el nombre de cada página y su aplicación. Y `Alt+←` / `Alt+→` como atajos.
- **Por qué.** La pila existe, está limitada a 20 y **no se puede ver**: la única forma de usarla es
  pulsar la flecha una vez por salto. Después de un rato entre una ficha, su informe y el
  inventario, volver tres sitios atrás son tres clics a ciegas.
- **Coste.** `NavigationService` tiene que exponer la pila —hoy la esconde— y la barra de miga, un
  menú. Sin lógica de negocio.
- *Origen: lupa 2.*

### P-19 — Un aviso al salir de Ajustes con cambios sin guardar

> **Sin objeto desde R5 (D-1003):** Ajustes guarda cada cambio en el momento de hacerlo, así que ya
> no hay cambios sin guardar con los que salir. La propuesta se queda escrita porque el contrato que
> pedía —que una página pueda vetar la salida— sigue siendo útil para «Nueva aplicación» a medio
> rellenar, y ese es el argumento que habría que volver a hacer.

- **Qué.** Salir de Ajustes por el raíl o por la miga con la marca de sucio puesta abre una
  confirmación de tres salidas: Guardar, Descartar, Seguir editando.
- **Por qué.** D-987 dice con todas las letras que cambiar de página perdía lo tocado en silencio, y
  lo que puso fue **la señal** —«Hay cambios sin guardar»— que vive en la página que estás
  abandonando y desaparece justo en el momento en que hace falta. La señal avisa mientras miras; la
  confirmación avisa cuando te vas. Son dos cosas distintas y la segunda no se hizo.
- **Coste.** `SettingsViewModel` ya tiene la huella de sucio; hace falta un gancho de «puedo salir»
  en `NavigationService` y el diálogo. Ese contrato nuevo —que una página pueda vetar la salida— es
  lo más caro, y lo que la haría útil también para «Nueva aplicación» a medio rellenar.
- *Origen: lupa 2.*

---

## D · Vistas que piden otra disposición

### P-20 — Portafolio: fundir la tira de gravedades con la rejilla

- **Qué.** Quitar los cuatro azulejos de arriba, llevar el total a la línea de subtítulo —que ya
  dice «1 aplicación · 111 hallazgos abiertos»— y que la rejilla de tarjetas ocupe el ancho desde el
  primer renglón, repartida por `ColumnsPanel`.

```
Portafolio                                     [+ Nueva aplicación]
1 aplicación · 0 críticas · 27 altas · 64 medias · 20 bajas
┌─ XBLAST ────────┐ ┌─ (hueco) ───────┐ ┌─ (hueco) ───────┐
```

- **Por qué.** Hoy (UI-0020) los azulejos gastan 1.635 px para enseñar cuatro números que la tarjeta
  de abajo vuelve a enseñar 200 px después, y la rejilla se queda con el 32 % del ancho. Con una
  aplicación la pantalla es casi toda vacío; con quince, los azulejos empujarían la rejilla fuera
  del primer pliegue.
- **Coste.** Solo `PortfolioView`. Ninguna lógica: los cuatro recuentos ya están en el view-model.
- *Origen: lupa 1.*

### P-21 — Que el informe se lea como un documento

- **Qué.** La tarjeta se ciñe a la medida (≈760 px) y se centra en su área; el anexo va justo
  después del último renglón; el texto en bandera. Y que «Descargar .md» y «Ver hallazgos de esta
  sesión» se queden pegados arriba al desplazar.
- **Por qué.** Hoy (UI-0032) la tarjeta mide 1.640 px y el texto 700: el ojo tiene que decidir dónde
  acaba el documento. Centrar la columna quita esa pregunta y, de paso, el hueco de 310 px.
- **Coste.** `ReportsView`. Ninguna lógica.
- *Origen: lupa 1.*

### P-22 — Que Hallazgos enseñe la gravedad como una barra, no como tres pastillas

- **Qué.** Sustituir las tres pastillas de recuento por una barra apilada de ancho fijo
  (rojo/ámbar/azul en proporción) con el total a su derecha, y el desglose exacto en el tooltip y al
  desplegar la fila.

```
HttpService.cs   XBLAST · XBLASTRecovery/Class/HttpService.cs   ███▊▍  29
RecoveryUtils.cs XBLAST · XBLASTRecovery/Class/RecoveryUtils.cs ███     3
```

- **Por qué.** Resuelve UI-0024 sin gastar tres ranuras por fila, y contesta mejor la pregunta que
  se le hace a esta lista —«¿cuál está peor?»— porque las barras se comparan de un vistazo y tres
  números no. **Roza D-944.4**: el color deja de ir acompañado de la palabra en la fila. Se
  compensa dejando la palabra en el total al desplegar y en el tooltip; si eso no basta, la barra
  puede llevar escrito al lado el recuento de la gravedad dominante.
- **Coste.** `FindingsView` y un control de barra apilada. Los datos ya están.
- *Origen: lupa 1.*

### P-23 — Sacar los diálogos a páginas cuando el diálogo es una tarea

- **Qué.** «Vincular clon local» y «Patrones silenciados» dejan de ser ventanas y pasan a ser
  páginas con miga, como hizo «Acerca de» en D-997 §6. Se quedan como diálogo solo los tres que
  interrumpen para confirmar (borrar aplicación, restablecimiento, lanzar auditoría), que es lo que
  un diálogo hace bien.
- **Por qué.** Además de borrar UI-0013 y UI-0014 por la vía de quitar la ventana, «Patrones
  silenciados» es una lista con edición y caducidades metida en 760×580 con dos barras de
  desplazamiento: es una pantalla, no un aviso. Y vincular un clon es una tarea con validación, que
  es exactamente lo que Nueva aplicación ya hace en página.
- **Coste.** Dos vistas nuevas, dos diálogos borrados, dos entradas de navegación. Arrastra el
  registro de contenedor de cada diálogo, como ya pasó con «Acerca de» y con el de tarifas.
- *Origen: lupa 1.*

### P-24 — Los diálogos que se quedan, con la vista que los abre y no en una tanda aparte

- **Qué.** BACKLOG apunta los diálogos pendientes como «trabajo acotado y mecánico». Cambiar el
  orden: convertir primero los que se abren desde una vista ya hecha, porque son los que un usuario
  ve el mismo día que ve la vista convertida.
- **Por qué.** Un diálogo blanco encima de una pantalla crema no se lee como «esto todavía no está
  hecho»: se lee como «esto es de otro programa». Y los dos más blancos son justo los dos que piden
  confirmar un borrado, que es donde la confianza importa. El coste es el mismo se haga antes o
  después; lo que cambia es cuánto tiempo se ve.
- **Coste.** Cinco XAML, por tokens, sin decisiones nuevas.
- *Origen: lupa 3. Complementaria de P-23, no alternativa: P-23 se lleva dos y ésta arregla los tres
  que quedan.*

### P-25 — Un modo «lista compacta» para Inventario y Hallazgos

- **Qué.** Un conmutador de densidad (cómoda / compacta) que baje la altura de fila de 54 a 36 px y
  suba de 13 a 20 las filas visibles a 1280.
- **Por qué.** El inventario real tiene 923 unidades y a 1280 se ven diez. La densidad no es un
  gusto en una herramienta que se mira horas: es cuántas veces hay que desplazar para encontrar
  algo. No contradice D-944.3 —los tamaños de letra no cambian, cambia el relleno de fila, que
  sigue siendo múltiplo de 4.
- **Coste.** Dos vistas, un ajuste en Apariencia y un token de alto de fila. Sin lógica.
- *Origen: lupa 1.*

### P-26 — El interruptor, el anillo y la barra dicen su estado con palabra, no solo con azul

- **Qué.** Poner rótulo al lado de los tres controles que hoy solo hablan por color:
  «Activado/Desactivado» junto al interruptor, «Comprobando…» también en la barra de estado, y el
  porcentaje al lado de la barra del ciclo (que hoy vive dos líneas más arriba).
- **Por qué.** D-989 fijó icono + COLOR + PALABRA para Cuenta y la regla es buena para toda la
  aplicación. Los interruptores de Ajustes son el sitio donde más caro sale equivocarse —«Modo
  exhaustivo» multiplica el gasto— y hoy la única señal de que está apagado es que el azul no está.
- **Coste.** `SettingsView` (tres filas), la tarjeta del portafolio y la barra de estado. Sin lógica.
- *Origen: lupa 3.*

### P-27 — «Auditar N seleccionadas» también cuando N es cero

- **Qué.** Que el primario de Inventario y el de Nueva aplicación se apaguen cuando no pueden hacer
  nada, con la razón escrita al lado en tinta apagada: «Auditar selección — marca al menos una
  unidad», «Crear e inventariar — elige un repositorio». Y que Tarifas siga la misma regla que las
  otras cuatro secciones de Ajustes.
- **Por qué.** Unifica los cuatro criterios de UI-0038 en uno y aplica a las vistas de trabajo la
  regla que D-1000 ya escribió para Ajustes. Ahorra el clic que no hace nada y, en Inventario,
  ahorra la duda de qué se va a gastar.
- **Coste.** Tres vistas, una propiedad `CanExecute` que ya existe en los tres view-models y una
  cadena de razón por botón. Sin lógica nueva.
- *Origen: lupa 3.*

---

## E · El instrumento

### P-28 — El banco de capturas deja el scratchpad y entra en `scripts/`

- **Qué.** Meter en el repositorio las dos piezas con las que se ha hecho esta auditoría: el
  recorrido por automatización sobre el `dist` (`tour.ps1`, 18 vistas × 4 combinaciones, con copia y
  restauración del `settings.json` real) y el banco del agente falso (`Atalaya.Shots`, las cuatro
  vistas densas y los cinco diálogos sobre un hub temporal). Con su README y su `.gitignore` para
  los PNG.
- **Por qué.** **Contradice D-977**, que decidió que el banco «vive en el scratchpad y no en el
  repositorio: es un instrumento de esta fase, no producto». El argumento nuevo es que **ya no es de
  una fase**: se ha necesitado en F26 §B, en F26 §C y otra vez aquí, y esta vez hubo que ir a
  rescatarlo de un scratchpad de otra sesión y volver a arreglarle tres cosas —el orden del
  recorrido, el clic que plegaba el raíl, los reintentos de la búsqueda—. Y hay una razón más
  fuerte: **P-10, P-11 y cualquier prueba que mire lo que se pinta dependen de él**. Un test no
  puede depender de un fichero temporal. Lo que D-977 dijo con razón es que el banco no es
  *producto*; `scripts/` no es producto, y ahí ya viven `PromptBench` y `IconGen` por el mismo
  motivo.
- **Coste.** Mover dos carpetas, un `README.md` y una entrada de `.gitignore`. `Atalaya.Shots`
  referencia `Atalaya.App`, así que entra en la solución o se queda fuera de ella con su propio
  `dotnet build`; lo segundo es más limpio y es lo que hace `PromptBench`.
- *Origen: **la consolidación**.*

---

## Anexos — las tres listas tal como salieron

Antes de consolidar, sin tocar una palabra. Sirven para ver qué vio cada lupa por su cuenta, qué
encontraron dos a la vez y qué no vio ninguna.

- [`anexo-lupa-1-legibilidad.md`](anexo-lupa-1-legibilidad.md) — 24 hallazgos, 10 propuestas.
- [`anexo-lupa-2-navegacion.md`](anexo-lupa-2-navegacion.md) — 19 hallazgos, 9 propuestas.
- [`anexo-lupa-3-color.md`](anexo-lupa-3-color.md) — 19 hallazgos, 8 propuestas.

**Cómo salen 61 de 62.** Ninguno se descartó. Cinco pares de dos lupas distintas describían el
mismo defecto y se fundieron en una entrada; tres hallazgos de la lupa 3 mezclaban dos defectos y se
repartieron en dos entradas cada uno. De ahí salen 60, más uno que añadió la consolidación al buscar
la raíz de UI-0031: **61**. Los ocho pares que siguen pareciéndose están marcados como posible
duplicado y **no** se han fusionado: eso lo decide la persona.
