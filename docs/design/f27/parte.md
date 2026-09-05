# F27 — Cierre: qué cambia en cada vista

UI-AUDIT-1 dejó **61 hallazgos**. F27 los arregló por causa y no por parche —siete raíces y siete
sueltos—, y en el cierre el usuario abrió el `dist` y mandó **volver seis cosas** que se habían
movido sin que él las pidiera. Este parte es la lista completa de **lo que se ve distinto de la
Parte C**, vista por vista, una línea por cambio (N-6).

**Cómo leerlo.** El «antes (C)» es `docs/design/ui-audit-1/banco/` — la auditoría se hizo sobre la
Parte C, así que su banco **es** el estado de la C. El «después» es
`docs/design/f27/banco/`, con los mismos 100 nombres. Los dos ficheros se abren al lado.

> **Las capturas de «después» son de antes de las seis reversiones y de los dos últimos arreglos.**
> El recorrido del banco conduce la aplicación con el ratón de verdad (N-8), así que **no lo he
> vuelto a lanzar**. Donde una fila dice *(revertido)* o *(sin captura)*, lo que hay que mirar es el
> `dist`. La columna «se ve» dice qué buscar.

---

## Las seis reversiones

| Vista | Qué vuelve a como estaba en la C |
|---|---|
| **Raíl** | Todas las entradas seguidas, sin bloque anclado al pie. La fila de usuario vuelve a llevar a «Cuenta». Se conserva **solo** el icono nuevo de Inventario. |
| **Portafolio** | La rejilla reparte por ancho sin mirar cuántas tarjetas hay: la tarjeta recupera su medida y sus cuatro cifras vuelven a ir juntas dentro. |
| **Portafolio** | La tira de gravedades vuelve a ser los cuatro azulejos anchos. |
| **Portafolio** | Vuelve la **papelera** a la cara de la tarjeta; se va el menú «…». |
| **Cuenta** | Centrada en su ancho máximo, **con su título** — el defecto era que la cabecera se quedaba en el margen y el cuerpo se iba al centro. |
| **Nueva aplicación** | Igual: centrada, con su título y con su barra de acción. |
| **Ajustes** | Fuera la pastilla «no has cambiado nada» junto a Guardar. |

---

## Lo que sigue distinto de la Parte C, por vista

### Toda la aplicación (paleta y tipografía)

| Se ve | Antes (C) | Después |
|---|---|---|
| Las cuatro gravedades tienen **cuatro rellenos distintos**; antes crítica y alta compartían fondo y «baja» llevaba el azul de «estás aquí» | `banco/vistas/dark-completa/04-hallazgos.png` | `f27/banco/vistas/dark-completa/04-hallazgos.png` |
| Un solo rotulado de gravedad: «15 Altas», no «15 Alta» / «Crit 0» / «Critica» | ídem | ídem |
| El cero de un recuento se pinta en neutro, no en rojo de peligro | `…/01-portafolio.png` | ídem |
| Interruptores, radios, casillas y anillos usan el azul de la paleta, no el de la librería | `…/11d-ajustes-apariencia.png` | ídem |
| El foco se ve con un anillo de la paleta, no con el rectángulo de puntos de fábrica | — | abrir el `dist` y tabular |
| Las tres acciones de la ficha vuelven al sistema (eran `#DDDDDD` sobre negro en los dos temas) | `…/05-hallazgo-ficha.png` | ídem |

### Raíl y carcasa

| Se ve | Antes (C) | Después |
|---|---|---|
| «Inventario» tiene icono propio; antes eran las tres rayas del botón de plegar | `…/01-portafolio.png` | `f27/…/01-portafolio.png` |
| El marcador de «estás aquí» se pinta; antes no cabía en su carril y WPF lo recortaba entero | ídem | ídem |
| La miga acaba siempre en la página, y el informe pierde su segundo «← Volver» | `…/07-informe-abierto.png` | `f27/…/07b-informe-con-hallazgos.png` |
| Pasar por Portafolio ya no borra la aplicación activa del raíl | — | abrir el `dist` |
| El aviso flotante tiene carril propio: empuja el pie en vez de taparlo | `densas/dark-completa-06-arreglo-asistido.png` | `f27/…/densas/dark-completa-06-arreglo-asistido.png` |
| La flecha de volver apagada se lee apagada | `…/01-portafolio.png` | ídem |

### Portafolio

| Se ve | Antes (C) | Después |
|---|---|---|
| «Abrir inventario» pasa a secundario: un solo primario por vista | `…/01-portafolio.png` | *(revertido lo demás; esto se queda)* |

### Hallazgos

| Se ve | Antes (C) | Después |
|---|---|---|
| Las pastillas de gravedad caen en **una ranura fija por nivel**; antes se empaquetaban a la derecha y la misma gravedad cambiaba de columna según la fila | `…/04-hallazgos.png` | `f27/…/04-hallazgos.png` |
| «Expandir todo» baja a la barra de la lista y deja de ser un enlace azul | ídem | *(sin captura)* |
| Una ruta que no cabe se acorta **por el medio** y conserva el nombre del fichero | `banco/vistas/dark-1280/04-hallazgos.png` | *(sin captura)* |

### Ficha de un hallazgo

| Se ve | Antes (C) | Después |
|---|---|---|
| La monoespaciada marca **una** clase —identificadores, rutas y hashes— y no cambia el tamaño | `…/05-hallazgo-ficha.png` | `f27/…/05-hallazgo-ficha.png` |
| El aviso de re-anclaje lleva icono, como los demás avisos de la casa | `banco/vistas/dark-1280/05-hallazgo-ficha.png` | ídem |
| La pastilla «Activo» sale de la paleta (daba 2,38:1 sobre el crema) | `banco/vistas/light-completa/05-hallazgo-ficha.png` | ídem |

### Informes

| Se ve | Antes (C) | Después |
|---|---|---|
| Unidades, Hallazgos y Coste se alinean a la derecha con cifras de ancho fijo | `…/06-informes.png` | `f27/…/06-informes.png` |
| «8 informes» vive bajo el título, no a 1.600 px de él | ídem | ídem |
| «sin cambios» baja a tinta apagada; el delta se lee de un vistazo | ídem | ídem |
| La barra de filtros partida empieza sus dos filas en la misma x | `banco/vistas/dark-1280/06-informes.png` | `f27/…/dark-1280/06-informes.png` |
| La tabla **cabe** a 1280: antes los mínimos sumaban 8 px más de los que había | ídem | ídem |
| El informe se lee alineado a la izquierda, con la tipografía del sistema y con anexo en el mismo desplazamiento | `…/07-informe-abierto.png` | `f27/…/07-informe-abierto.png` |

### Métricas

| Se ve | Antes (C) | Después |
|---|---|---|
| Las cifras de coste por fase se alinean; las dos filas comparten columna | `…/08-metricas.png` | `f27/…/08-metricas.png` |
| Los recuentos por gravedad usan los cuatro rellenos nuevos | ídem | ídem |

### Inventario

| Se ve | Antes (C) | Después |
|---|---|---|
| «Auditar selección» está **apagado** con cero casillas, y dice por qué al lado | `…/03-inventario.png` | `f27/…/03-inventario.png` |
| «Expandir todo» deja de ser un enlace azul | ídem | *(sin captura)* |
| La barra del cajón del ciclo no tapa los enlaces «Gestionar» | `banco/vistas/dark-1280/03b-inventario-cajon.png` | `f27/…/dark-1280/03b-inventario-cajon.png` |

### Ajustes

| Se ve | Antes (C) | Después |
|---|---|---|
| Los bloques del mismo nivel acaban donde acaba su columna (antes en 1441, 1460 y 1400) | `…/11-ajustes.png` | `f27/…/11e-ajustes-avanzado.png` |
| «Guardar tarifas» se apaga cuando no hay nada que guardar | `…/11c-ajustes-tarifas.png` | `f27/…/11c-ajustes-tarifas.png` |
| Las cinco secciones se recorren con flechas y se activan con Enter | — | abrir el `dist` |

### Cuenta

| Se ve | Antes (C) | Después |
|---|---|---|
| «Hub local» dice su estado con **icono + color + palabra**, como la lista de arriba | `…/09-cuenta.png` | `f27/…/09-cuenta.png` |
| «Desconectar» es perfilada, no maciza | ídem | ídem |

### Nueva aplicación

| Se ve | Antes (C) | Después |
|---|---|---|
| La fila «Stack» tiene la forma de las otras tres: rótulo, control y botón de apoyo en sus columnas | `…/02-nueva-aplicacion.png` | `f27/…/02-nueva-aplicacion.png` |
| La razón de un control apagado **envuelve** en vez de cortarse contra el borde | ídem | ídem |
| La fila de acción se queda pegada al pie de la columna; antes «Crear e inventariar» quedaba fuera de pantalla a 1280 | `banco/vistas/dark-1280/02-nueva-aplicacion.png` | `f27/…/dark-1280/02-nueva-aplicacion.png` |

### Sesión en vivo y Arreglo asistido

| Se ve | Antes (C) | Después |
|---|---|---|
| Las rutas de unidad se acortan por el medio en vez de cortarse contra el borde | `densas/dark-completa-05-sesion-en-vivo.png` | `f27/…/densas/dark-completa-05-sesion-en-vivo.png` |
| La tira de la carcasa deja de repetir el progreso que la barra de la vista ya da | ídem | ídem |
| La tarjeta de permiso **ya contestada** baja a superficie neutra; el ámbar queda para la abierta | `densas/dark-completa-06-arreglo-asistido.png` | `f27/…/densas/dark-completa-06-arreglo-asistido.png` |
| «Responder» baja a secundario: había dos primarios a la vez | ídem | ídem |
| La pastilla de proveedor y modelo se retira entera a 1280 en vez de quedarse en «Age» | `densas/dark-1280-06-arreglo-asistido.png` | `f27/…/densas/dark-1280-06-arreglo-asistido.png` |
| El cierre del arreglo lleva icono en su aviso | `densas/dark-completa-07-arreglo-cierre.png` | `f27/…/densas/dark-completa-07-arreglo-cierre.png` |

### Los nueve diálogos

| Se ve | Antes (C) | Después |
|---|---|---|
| Fondo, botones, márgenes y colores salen del sistema; antes eran una isla | `dialogos/dark-d1-vincular-clon.png` … `d5` | `f27/banco/dialogos/…` |
| «Restablecimiento de fábrica» y «Descartar todo» son perfiladas fuera del diálogo que confirma | `…/d4-restablecimiento-de-fabrica.png` | ídem |

---

## Las reglas nuevas

Trece tests. Uno por línea, con lo que protege cada uno. Todos comprobados contra su defecto.

| Test | La regla |
|---|---|
| `PaletteResourceTests.Cada_color_de_la_paleta_tiene_su_pincel` | Por cada `Color.X` hay un `Brush.X`, en las dos paletas |
| `PaletteResourceTests.Ningun_XAML_pide_un_pincel_que_no_existe` | Todo `{DynamicResource Brush.*}` resuelve contra los diccionarios fusionados de verdad |
| `PaletteContrastTests.Los_cuatro_rellenos_y_las_cuatro_tintas_de_gravedad_son_distintos` | Los ocho colores de gravedad son ocho, y cada tinta se mide contra **su** relleno |
| `PaletteContrastTests.Cada_dialogo_pinta_su_fondo_con_una_superficie_medida` | Un diálogo no se inventa un fondo que el contraste no haya medido |
| `DesignTokenTests.El_marcador_de_estas_aqui_cabe_en_su_carril` | Barra + relleno ≤ carril: lo que no cabe, WPF lo recorta en silencio |
| `DesignTokenTests.Un_texto_que_no_envuelve_recorta_con_una_de_las_tres_primitivas` | Nadie escribe su propio `TextTrimming` |
| `DesignTokenTests.Ningun_estilo_se_apoya_en_una_clave_que_se_declara_mas_abajo` | Un diccionario se lee de arriba abajo: lo declarado después no existe todavía |
| `DesignTokenTests.La_cabecera_y_el_cuerpo_de_una_pagina_van_en_la_misma_columna` | O los dos en el margen o los dos centrados; nunca uno a cada lado |
| `PathTextTests` (tres) | Lo que cabe se deja entero; lo que no, se acorta por el medio; y una fila reciclada vuelve a acortar |
| `ExhaustiveModeTests.La_tira_de_la_carcasa_dice_la_palabra_y_no_repite_el_progreso` | El progreso lo dice la vista una vez |
| `ShellNavigationTests.La_miga_acaba_en_la_pagina_y_solo_el_ultimo_eslabon_lo_parece` | Sólo un eslabón es la página; los demás llevan separador aunque no sean enlace |
| `ShellNavigationTests.Una_geometria_que_no_es_un_numero_no_se_guarda` | El autochequeo no puede acabar en excepción: su código de salida es lo que mira la Release |

---

## El recuento de los 61

| | |
|---|---|
| **Cerrados** | **56** |
| **Revertidos por decisión del usuario** | **4** — UI-0020 (los azulejos), UI-0043 (el bloque de sistema al pie), UI-0061 (la fila de usuario), y la mitad de UI-0038 que tocaba a «Guardar» |
| **Descartados por decisión del usuario** | **1** — UI-0045 (los rótulos del raíl) |

UI-0035 se mantiene cerrado, pero al revés de como F27 lo cerró: la cabecera se centra con el
cuerpo, en vez de el cuerpo alinearse con la cabecera.

---

## Lo que el `dist` enseñó y el verde no

Cuatro defectos propios, y son la razón de N-8. Los cuatro compilaban y pasaban los tests:

1. **La aplicación publicada no arrancaba.** Un estilo heredaba de otro declarado 88 líneas más
   abajo en el mismo diccionario: build verde, 1.920 tests verdes, autochequeo verde, y
   `dist\Atalaya.exe` se cerraba solo al pintar la primera pantalla. **Desde ahora `--selfcheck`
   pinta la primera vista**, que es donde se aplican los estilos.
2. **El propio autochequeo acababa en excepción**, guardando la geometría de una ventana que nunca
   se enseñó —infinitos, que `System.Text.Json` no escribe—. Imprimía «Arranca.» y reventaba: su
   código de salida, que es lo que mira el workflow de release, no significaba nada.
3. **`TextTrimming="None"` impedía el acortado** en vez de dejarlo trabajar: un `TextBlock` sin
   recorte declarado pide el ancho del texto entero y no acepta menos.
4. **La miga juntaba dos eslabones** —`Portafolio › XBLASTInventario`— porque «ser la página» se
   deducía de «no ser enlace», y el eslabón de la aplicación no es enlace cuando ya estás en su
   inventario.

---

## Estado

- **Build:** correcto, 0 errores.
- **Tests:** **2.437 en verde**, 0 fallos (1.928 de `Atalaya.App.Tests`).
- **`dist`:** reconstruido, y `--selfcheck` en verde con la primera vista pintada.
- **El push es del usuario** (N-3).
