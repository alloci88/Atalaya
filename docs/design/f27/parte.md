# F27 — Lo que la auditoría vio: siete raíces, no sesenta y un parches

UI-AUDIT-1 dejó **61 hallazgos** (18 altos, 31 medios, 12 bajos) y 28 propuestas. Sesenta y un
parches habrían sido sesenta y un sitios donde el mismo defecto puede volver, así que se arreglaron
**siete causas** y se comprobó, hallazgo a hallazgo, cuál cae con cada una. Los siete que no caían
con ninguna se arreglaron al final, uno por uno.

**Resultado: 60 cerrados, 1 descartado por decisión del usuario (UI-0045).** Ocho propuestas del
bloque 2 entran —P-01, P-02, P-04, P-05, P-08, P-12, P-27, P-28—; una queda descartada con su
hallazgo (P-17); las diecinueve restantes no se han tocado.

## Cómo se mira la evidencia

`docs/design/f27/banco/` tiene **exactamente los mismos 100 ficheros** que
`docs/design/ui-audit-1/banco/`, con los mismos nombres y en las mismas cuatro combinaciones
—oscuro y claro, 1920×1080 maximizada y 1280×720—. Cada hallazgo de abajo dice en qué fichero se
comprueba, y ese fichero se abre al lado del de la auditoría: la misma pantalla, antes y después.

Las capturas del recorrido salen del `dist` publicado con los datos reales de esta máquina; las de
las cuatro vistas densas y los nueve diálogos, del banco del agente falso (P-28, D-1009).

---

## Raíz 1 — El color que no salía de la paleta

**La causa.** Había **cuatro sitios donde nacía un color**: la paleta, un converter de C# con
hexadecimales dentro, un `Style` sin `BasedOn` que se llevaba la plantilla de la librería, y la
propia WPF-UI escribiendo su acento en `Application.Resources`. Los tres últimos no se enteran de
que el tema ha cambiado, y ninguno lo miraba `PaletteContrastTests`.

**Qué cambió.** `Brush.Ink.OnVivid` —la única clave de color sin su pincel, con seis referencias que
caían al negro en los dos temas— entra en las dos paletas junto con la regla que lo habría evitado
(**P-12**). La gravedad estrena sus cuatro rellenos propios, `Sev.{Crit,High,Med,Low}.Soft`, en vez
de tomar prestados `Danger.Soft` **dos veces**, `Warning.Soft` y `Primary.Soft` (**P-05**); `Sev.Low`
sale además del azul del primario, con el que era el mismo valor en el tema claro. **Nueve converters
de color escritos a mano se van** y su sitio son `DataTrigger` con `DynamicResource`, que se
reevalúan al cambiar de tema (D-971). `Primary.Soft` deja de significar cuatro cosas a la vez. Y el
acento de la librería se redirige por paleta y se tapa en `Application.Resources` desde
`ThemeService`, que es donde un diccionario fusionado no llega.

**Cierra 12:** UI-0005, 0006, 0007, 0008, 0010, 0018, 0019, 0027, 0034, 0049, 0050, 0051.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0005 · las tres acciones de la ficha | `vistas/dark-completa/05-hallazgo-ficha.png` — «Arreglar con agente» verde y las tres del sistema |
| UI-0006 · la pastilla de estado | `vistas/light-completa/05-hallazgo-ficha.png` — «Activo» sobre `Primary.Soft`, medida contra el crema |
| UI-0007 · la tinta sobre color vivo | `vistas/dark-completa/04-hallazgos.png` — las pastillas de recuento, ninguna negra |
| UI-0008 · el verde de estado de unidad | `densas/dark-completa-05-sesion-en-vivo.png` — la columna de unidades |
| UI-0010 · un solo juego de gravedad | `densas/dark-completa-05-sesion-en-vivo.png` — chips y contadores, a 40 px, del mismo juego |
| UI-0018 · el azul de la librería | `vistas/dark-completa/11d-ajustes-apariencia.png` — interruptores y radios |
| UI-0019, UI-0050 · la pastilla de temática | `vistas/dark-completa/05-hallazgo-ficha.png` y `11b-ajustes-auditoria.png` |
| UI-0027 · un solo rotulado | `vistas/dark-completa/04-hallazgos.png` — «15 Altas», «10 Medias», «4 Bajas» |
| UI-0034 · el color no lo pone un converter | `vistas/dark-completa/08-metricas.png` |
| UI-0049 · `Primary.Soft` no es cuatro cosas | `vistas/dark-completa/11-ajustes.png` — sección activa contra «estás aquí» del raíl |
| UI-0051 · el cero no se pinta de peligro | `vistas/dark-completa/01-portafolio.png` — «0 Críticas» en neutro |

## Raíz 2 — Los nueve diálogos no leían la paleta

**La causa.** Los diálogos se escribieron antes que los tokens y nadie volvió: eran una isla con su
propio fondo, sus propios botones y sus propias medidas.

**Qué cambió.** Cada uno pinta su rejilla raíz con `Brush.Bg`, como `MainWindow`; sus botones son los
cuatro del sistema; y sus tamaños, márgenes y colores salen de los tokens. **Las dos listas de deuda
quedan a cero**: la de pendientes de `DesignTokenTests` y la de `ImplicitStyleTests`. Y sobre todo,
`PaletteContrastTests` **mide ahora la superficie de un diálogo**, que era el hueco por el que se
colaron: sin esa medida, el arreglo habría durado hasta el siguiente diálogo. El orden de conversión
fue P-24 —un diálogo se convierte con la vista que lo abre—, usado solo como criterio de orden.

**Cierra 2:** UI-0013, 0014.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0013 · el fondo del diálogo | `dialogos/dark-d1-vincular-clon.png` … `dark-d5-lanzar-auditoria.png`, y sus cinco en claro |
| UI-0014 · los botones del diálogo | los mismos cinco: primario, secundario y destructiva perfilada |

## Raíz 3 — El foco y la accesibilidad

**La causa.** El foco no tenía sitio propio —se veía con el rectángulo de puntos de fábrica, que da
**1,21:1 en oscuro** y era el único elemento visual de la aplicación que no cambiaba con el tema— y
las dos superposiciones no se comportaban como superposiciones.

**Qué cambió.** **P-04** se cierra con su uso: el anillo de 2 px dibujado **fuera** del control, con
su token de separación, en botones, campos, interruptores, radios y listas. **P-03 sí sale más barato
como un patrón único**: `Controls/Overlay.cs` es un comportamiento adjunto con un solo contrato —al
abrirse toma el foco, lo retiene, `Escape` cierra, al cerrarse lo devuelve—, y lo usan el menú «…» de
una tarjeta y el cajón del ciclo, que eran **el mismo defecto escrito dos veces**. **P-15 no hace
falta** para UI-0042: el orden de tabulación ya es el visual, así que basta con que el foco entre en
la página al cambiar de página.

**Cierra 6:** UI-0001, 0003, 0015, 0016, 0042, 0061.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0001 · el menú «…» retiene el foco | `vistas/dark-completa/01b-portafolio-mas.png` |
| UI-0003 · el cajón del ciclo | `vistas/dark-1280/03b-inventario-cajon.png` |
| UI-0015 · el foco se ve | cualquier captura con foco; la regla vive en `Focus.Visual` y su paleta |
| UI-0016 · nombres de accesibilidad | no es visual: se comprueba en el árbol de automatización, que es lo que recorre el propio banco |
| UI-0042 · el foco entra en la página | ídem — el recorrido del banco depende de ello |
| UI-0061 · la fila de usuario no navega | `vistas/dark-completa/12-rail-plegado.png` |

## Raíz 4 — La carcasa: el raíl y la miga

**La causa.** El raíl y la miga se movían entre vistas, y el marcador de «estás aquí» **no se pintaba
nunca**: pedía 3 px de barra más 4 de relleno **a cada lado** dentro de un carril de 8, así que WPF
lo recortaba en silencio. Es la aritmética de D-966 fallando por tercera vez; por eso esta vez la
cuenta la vigila un test.

**Qué cambió.** El bloque de sistema se ancla al pie —«Cuenta» recorría 137 px entre vistas—;
«Inventario» tiene icono propio, que antes eran tres rayas como el botón de plegar; lo que late es el
icono y no un punto que lo sustituye; y pasar por Portafolio ya no borra la aplicación activa, que
hacía desaparecer «Inventario» del raíl en cinco vistas. La miga **acaba siempre en la página**, con
el mismo grano: el hallazgo cuelga de «Hallazgos» y el informe de «Informes», lo que permite retirar
el segundo «volver» que el informe pintaba dentro.

**UI-0045 no se hace**: el usuario retiró los rótulos del raíl a propósito (D-1000 §2), y **P-17
queda descartada** salvo que él la reabra.

**Cierra 10:** UI-0004, 0017, 0025, 0029, 0043, 0044, 0046, 0047, 0048, 0058. **Descarta 1:**
UI-0045.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0004, UI-0044 · la miga acaba en la página | `vistas/dark-completa/05-hallazgo-ficha.png` — «Portafolio › XBLAST › Hallazgos › BUG-0008» |
| UI-0017 · la aplicación activa no se borra | `vistas/dark-completa/01-portafolio.png` — «Inventario» sigue en el raíl |
| UI-0025 · «Ver hallazgos» conserva filtros | `vistas/dark-completa/04-hallazgos.png` |
| UI-0029 · raíl, título y miga dicen lo mismo | cualquiera de las diecisiete vistas |
| UI-0043 · el bloque de sistema, anclado | comparar `01-portafolio.png` con `11-ajustes.png`: Cuenta en la misma y |
| UI-0046 · el marcador cabe en su carril | `vistas/dark-completa/04-hallazgos.png` — la barra azul a la izquierda de «Hallazgos» |
| UI-0047 · «Inventario» tiene icono propio | `vistas/dark-completa/01-portafolio.png` |
| UI-0048 · la flecha apagada se lee apagada | `vistas/dark-completa/01-portafolio.png` |
| UI-0058 · un solo «volver» en el informe | `vistas/dark-completa/07b-informe-con-hallazgos.png` |

## Raíz 5 — Los recortes que mentían

**La causa.** Cada vista recortaba a su manera —o no recortaba—, así que lo mismo mentía en un sitio
y no en otro. D-983 §5 arregló uno de los tres con un presupuesto de caracteres fijo, y volvió a
aparecer en cuanto la ventana bajó a 1280.

**Qué cambió. P-01 — tres primitivas y nada más:** `Text.Name` recorta un nombre por el final con
elipsis; `Text.Path` acorta una ruta **por el medio** y conserva los dos extremos, sobre `c:PathText`,
que **mide** contra el ancho que hay en vez de contar caracteres; `Text.Chip` no recorta una pastilla
—si no cabe entera, no se pinta (`c:ChipHost`)—, porque una etiqueta a medias no es una versión corta
de la etiqueta, es otra palabra. Y el aire de un desplazamiento pasa a un token y un estilo: los
`ScrollViewer` de WPF-UI pintan la barra **superpuesta**, que es por lo que tapaba los enlaces
«Gestionar» del cajón del ciclo. Donde el corte cae en medio, un degradado dice que el contenido
sigue.

**Cierra 6:** UI-0002, 0009, 0011, 0028, 0033, 0037.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0002 · la barra no tapa los enlaces | `vistas/dark-1280/03b-inventario-cajon.png` |
| UI-0009 · las rutas de unidad | `densas/dark-completa-05-sesion-en-vivo.png` — «src/AtalayaBanco…/FormateadorInforme.cs» |
| UI-0011 · la pastilla de proveedor | `densas/dark-completa-06-arreglo-asistido.png` (entera) contra `densas/dark-1280-06-arreglo-asistido.png` (retirada) |
| UI-0028, UI-0037 · el degradado del corte | `densas/dark-1280-07-arreglo-cierre.png` |
| UI-0033 · el coste por fase, en columna | `vistas/dark-completa/08-metricas.png` |

## Raíz 6 — El primario, las acciones y los avisos

**La causa.** El primario tenía **cuatro criterios en cuatro sitios**, y un aviso flotante que se
pinta sobre todo acaba pintándose sobre un control.

**Qué cambió. P-27 — el primario se apaga cuando no puede hacer nada, y dice por qué al lado.**
Inventario tenía «Auditar selección» **encendido con cero casillas marcadas** —el botón que gasta
créditos del usuario, sin nada que auditar—; Ajustes apagaba «Guardar» en cuatro secciones **sin la
razón al lado** mientras la quinta lo tenía encendido sin que se hubiera tocado nada. La razón va en
`Reason.Chip`, **pegada al botón**, y no en un tooltip: hay que saber que existe para verlo, y quien
mira un botón apagado no sabe que hay nada que ver. **Un primario por vista**, y la destructiva
perfilada en todas partes, con el macizo reservado al diálogo que confirma. Y el aviso flotante gana
**carril propio**: una fila entre la página y el pie, que empuja el contenido en vez de taparlo y sin
avisos mide cero.

**Cierra 6:** UI-0012, 0021, 0022, 0038, 0039, 0041.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0012 · el aviso no tapa el pie | `densas/dark-completa-06-arreglo-asistido.png` — el toast sobre el pie, con «Compilar solución completa» entera |
| UI-0021 · un primario por vista | `vistas/dark-completa/01-portafolio.png` — «Abrir inventario» secundario |
| UI-0022, UI-0038 · el primario apagado dice por qué | `vistas/dark-completa/11-ajustes.png` — «no has cambiado nada» junto a «Guardar» |
| UI-0039 · la destructiva perfilada | `vistas/dark-completa/09-cuenta.png` — «Desconectar» |
| UI-0041 · la acción no se sale de la pantalla | `vistas/dark-1280/02-nueva-aplicacion.png` |

## Raíz 7 — La página y el documento

**La causa.** No había ningún sitio donde estuviera escrito **dónde empieza una página y hasta dónde
llega**, así que cada vista lo decidía por su cuenta: quince arrancaban en x = 264–266 y dos no
—Nueva aplicación en 633 y Cuenta en 800—, porque se centraban imitando a «Acerca de», que es una
excepción declarada y no un patrón.

**Qué cambió. P-02 — `PageShell`:** título, subtítulo, recuento y acciones tienen un sitio, y **una
página arranca en el margen de la página**; lo que una vista declara es el **techo** de su cuerpo, no
su centro. El recuento vive bajo el título que cuenta, no a 1.600 px de él. **P-08 — `Cell.Number`:**
las cifras de Informes y Métricas se alinean a la derecha con cifras de ancho fijo, que eran las
únicas de la casa que no lo hacían. El informe se lee alineado a la izquierda, con la tipografía del
sistema, con techo de línea de lectura y con documento y anexo en **un** solo desplazamiento. La
rejilla no pone más columnas que tarjetas tiene —con una sola aplicación daba tres y la tarjeta se
quedaba con 531 de 1.635 px— y los azulejos se ciñen a su contenido en vez de gastar 393 px para
enseñar 55. Y la gravedad estrena **una ranura fija por nivel**, siempre las cuatro, la vacía sin
pintar: se empaquetaban a la derecha, así que la misma gravedad caía en una columna distinta según
cuántas tuviera la fila.

**Cierra 10:** UI-0020, 0024, 0030, 0031, 0032, 0035, 0040, 0055, 0056, 0059.

| Hallazgo | Dónde se comprueba |
|---|---|
| UI-0020 · los azulejos se ciñen | `vistas/dark-completa/01-portafolio.png` — cuatro de 150 px a la izquierda |
| UI-0024 · una ranura por nivel | `vistas/dark-completa/04-hallazgos.png` — la columna se recorre; `dark-1280` igual |
| UI-0030 · las cifras se alinean | `vistas/dark-completa/06-informes.png` — Unidades, Hallazgos y Coste |
| UI-0031, UI-0032, UI-0059 · el documento | `vistas/dark-completa/07b-informe-con-hallazgos.png` |
| UI-0035 · la página arranca en el margen | `vistas/dark-completa/09-cuenta.png` y `02-nueva-aplicacion.png` |
| UI-0040 · el tope lo pone la columna | `vistas/dark-completa/11e-ajustes-avanzado.png` |
| UI-0055 · la barra partida comparte margen | `vistas/dark-1280/06-informes.png` — las dos filas en x = 282 |
| UI-0056 · el recuento bajo su título | `vistas/dark-completa/06-informes.png` — «8 informes ·» bajo «Informes» |

---

## Las siete bajas que no caían con ninguna raíz

Una línea cada una, que es lo que son.

- **UI-0026** — la monoespaciada marcaba tres filas de doce y no por lo que eran, y además bajaba a
  11 px: ahora una sola clase —lo que escribió la máquina y hay que poder copiar carácter a
  carácter—, marcada siempre y sin tocar el tamaño. `vistas/dark-completa/05-hallazgo-ficha.png`.
- **UI-0036** — «Hub local» contaba en tinta neutra lo que la lista de arriba cuenta con icono +
  color + palabra; ahora las tres cosas. `vistas/dark-completa/09-cuenta.png`.
- **UI-0052** — el aviso salía en tres formas según la vista; ahora icono, texto y —si la hay— la
  acción, siempre. `vistas/dark-1280/05-hallazgo-ficha.png`.
- **UI-0053** — la tira de la carcasa repetía el progreso que la barra de la vista ya da con más
  detalle; ahora dice qué se audita, que es lo que la vista no puede decir.
  `densas/dark-completa-05-sesion-en-vivo.png`.
- **UI-0054** — la tarjeta de permiso ya contestada baja a superficie neutra; el ámbar es de lo que
  sigue abierto. `densas/dark-completa-06-arreglo-asistido.png`.
- **UI-0057** — «sin cambios» baja a tinta apagada, así que la columna que dice si una sesión trajo
  trabajo se puede recorrer. `vistas/dark-completa/06-informes.png`.
- **UI-0060** — la fila «Stack» adopta la forma de las otras tres del formulario: una columna de
  rótulos, una de controles y una de botones de apoyo. `vistas/dark-completa/02-nueva-aplicacion.png`.

---

## Las reglas nuevas

Trece tests, uno por línea, con la regla que protege cada uno. Todos comprobados contra su defecto:
se dejó el defecto en su sitio y se vio fallar el test.

| Test | La regla que protege |
|---|---|
| `PaletteResourceTests.Cada_color_de_la_paleta_tiene_su_pincel` | Por cada `Color.X` hay un `Brush.X`, **en las dos paletas** |
| `PaletteResourceTests.Ningun_XAML_pide_un_pincel_que_no_existe` | Todo `{DynamicResource Brush.*}` resuelve contra los diccionarios fusionados de verdad, WPF-UI incluida |
| `PaletteContrastTests.Los_cuatro_rellenos_y_las_cuatro_tintas_de_gravedad_son_distintos` | Los ocho colores de gravedad son ocho, y cada tinta se mide contra **su** relleno |
| `PaletteContrastTests.Cada_dialogo_pinta_su_fondo_con_una_superficie_medida` | Un diálogo no puede inventarse un fondo que el contraste no haya medido |
| `DesignTokenTests.El_marcador_de_estas_aqui_cabe_en_su_carril` | Barra + relleno ≤ carril, en los dos raíles: un marcador que no cabe WPF lo recorta en silencio |
| `DesignTokenTests.Un_texto_que_no_envuelve_recorta_con_una_de_las_tres_primitivas` | Nadie escribe su propio `TextTrimming`: o `Text.Name`, o `Text.Path`, o `Text.Chip` |
| `DesignTokenTests.Ningun_estilo_se_apoya_en_una_clave_que_se_declara_mas_abajo` | Un diccionario se lee de arriba abajo: una clave declarada después no existe todavía |
| `DesignTokenTests.Ninguna_pagina_centra_su_cuerpo` | Una página arranca en el margen de la página; lo que declara es el techo de su cuerpo |
| `PathTextTests.Una_ruta_que_cabe_se_deja_entera` | Acortar lo que cabe es esconder sin motivo |
| `PathTextTests.Una_ruta_que_no_cabe_conserva_el_nombre_del_fichero` | El acortado es por el **medio**, y siempre con puntos suspensivos |
| `PathTextTests.Una_fila_reciclada_vuelve_a_acortar_su_ruta` | En una lista virtualizada, cambiar el texto sin cambiar de tamaño también tiene que acortar |
| `ExhaustiveModeTests.La_tira_de_la_carcasa_dice_la_palabra_y_no_repite_el_progreso` | El progreso lo dice la vista una vez; la carcasa dice lo que la vista no puede decir |
| `ShellNavigationTests.Pasar_por_el_portafolio_conserva_el_grupo_de_la_aplicacion` | Navegar a Portafolio no puede borrar la aplicación activa del raíl |

Y dos que cambian porque cambió la regla que protegían: `PolishLayoutTests` pasa de contar columnas
a exigir **una sola elástica y que sea la del texto** —el aviso lleva ahora también su icono—, y
`DesignTokenTests`/`ImplicitStyleTests` se quedan con sus listas de pendientes **vacías**.

---

## El recuento de los 61

| | |
|---|---|
| **Cerrados** | **60** |
| **Descartados por decisión del usuario** | **1** — UI-0045 (los rótulos del raíl; D-1000 §2, P-17 con él) |
| **Pendientes** | **0** |

Por raíz: raíz 1 · 12 · raíz 2 · 2 · raíz 3 · 6 · raíz 4 · 10 (+1 descartado) · raíz 5 · 6 · raíz 6 ·
6 · raíz 7 · 10 · bajas sueltas · 7. Suma 59 + 7 bajas = **60 cerrados y 1 descartado**.

---

## Lo que el `dist` enseñó y el verde no

Tres defectos propios, y son la razón por la que estas capturas se miran en el `dist`: **compilan
igual y los tests pasan igual.** Están en D-1010 con su detalle; en resumen:

1. **La aplicación publicada no arrancaba.** `Stat.Number.Sev` heredaba de un estilo declarado **88
   líneas más abajo**. Build verde, 1.920 tests verdes, y `dist\Atalaya.exe` se cerraba solo al
   pintar el Portafolio, que es la primera pantalla.
2. **Anclar la cabecera al margen no bastaba.** Con el cuerpo centrado, el título quedaba a la
   izquierda y el contenido en el medio, con 530 px en blanco entre los dos. Una página no se centra.
3. **`TextTrimming="None"` era lo que impedía el acortado**, no lo que lo dejaba trabajar: un
   `TextBlock` sin recorte declarado pide el ancho del texto entero y no acepta menos, así que en una
   fila apretada empujaba su columna y el contenedor la cortaba en seco.

Y de camino, dos cosas que las capturas destaparon y que no son de la auditoría: la tabla de Informes
**cabe a 1280** —sus mínimos sumaban 960 con 952 disponibles— y la razón de un control apagado
**envuelve** en vez de cortarse contra el borde de su tarjeta. Más una del propio banco: `tour.ps1`
seguía buscando el «← Volver» que la raíz 4 retiró del informe, así que dejó de tomar una captura sin
decir por qué; ahora vuelve por el raíl.

---

## Lo que NO se ha tocado

Ninguna regla de negocio, ningún dato, nada del hub, ningún prompt. Y ninguna de las decisiones del
usuario: el raíl sigue **sin rótulos de grupo** (D-1000 §2), la vista rápida sigue **descartada**, y
«Guardar» sigue **bajo su sección** (D-987, D-1000 §1). De las 28 propuestas del bloque 2 entran las
ocho de la lista y **ninguna más**.

Lo que queda apuntado está en `BACKLOG.md`: las trece vistas que aún escriben su cabecera a mano
—ya alineadas, así que no hay defecto, solo patrón por generalizar—, el ancho de las tarjetas de
Cuenta, las diecinueve propuestas del bloque 2 que el usuario no ha pedido, y el `$PSScriptRoot`
vacío de `tour.ps1` cuando se le invoca con `powershell -File`.

---

## Estado

- **Build:** correcto, 0 errores, 0 advertencias en la aplicación.
- **Tests:** **2.434 en verde**, 0 fallos (1.925 de `Atalaya.App.Tests`, trece nuevos).
- **`dist`:** reconstruido con `scripts/publish.ps1` y arrancado para comprobarlo.
- **Capturas:** recorrido completo en las cuatro combinaciones más las densas y los diálogos, en
  `docs/design/f27/banco/`.
- **El push es del usuario** (N-3): los commits están hechos y listados; nadie ha publicado nada.
