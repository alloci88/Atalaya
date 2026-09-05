## Hallazgos

### A2-01 — El cajón «Resumen del ciclo» no recibe el foco, no lo retiene y no se cierra con Escape: a 1280 sus seis controles no existen para el teclado

- **Vista y lugar:** Inventario › el cajón «Resumen del ciclo» (el panel flotante de la derecha).
- **Combinación:** 1280×720 y 150 %, los dos temas (a 1920 el panel está desplegado y no hay cajón).
- **Qué pasa:** al abrir el cajón el foco se queda en el botón que lo abrió; Tab **no entra** en el cajón, sigue recorriendo la página tapada por debajo (medido: «Ver hallazgos» → «Expandir todo» → buscador → filtro → «Seleccionar pendientes» → «Seleccionar cambiadas» → «Auditar selección» → y luego casilla+fila, casilla+fila… de los 923 módulos que el cajón está tapando); y **Escape no lo cierra** («Cerrar» sigue en el árbol después de pulsarlo). Sus seis controles —`Cerrar`, `Configurar ciclo`, `Revisar`, y los tres `Gestionar` de Patrones silenciados / Directivas / Umbrales— están marcados como enfocables pero no hay ninguna secuencia de Tab que llegue a ellos. Como D-997 §2 retiró de Ajustes el aviso del umbral, ese cajón es hoy **el único sitio** de la aplicación desde donde se llega a la gobernanza del ciclo.
- **Qué debería pasar:** al abrirse, el foco entra en el cajón; Tab circula dentro de él y no por la página que tapa; Escape lo cierra y devuelve el foco al botón que lo abrió.
- **Evidencia:** `dark-1280/03b-inventario-cajon.png` — el panel ocupa la mitad derecha y oscurece la página de debajo; los seis controles («Configurar ciclo», «Revisar», «Gestionar» ×3, «Cerrar» arriba a la derecha) están todos dentro de él. Medido sobre `dist\Atalaya.exe` a 1280×720 con UIAutomation.
- **Principio:** D-944.1 y D-944.6.
- **Gravedad:** alta

### A2-02 — El menú «…» de una tarjeta no recibe el foco, Tab lo salta, y el tooltip tapa su única entrada

- **Vista y lugar:** Portafolio › el botón «···» de la tarjeta de una aplicación.
- **Combinación:** todas.
- **Qué pasa:** abierto con el teclado (Espacio sobre «···»), el foco **se queda en el botón**; el siguiente Tab salta a «Abrir inventario», que está detrás del menú abierto, y sigue por el raíl. La única entrada del menú, «Eliminar la aplicación…», nunca recibe el foco: **borrar una aplicación no se puede hacer sin ratón**. Además, al abrirse por esa vía se pinta a la vez el tooltip «Más acciones» **encima** del propio menú, y de su única entrada solo se lee «…aplicación…».
- **Qué debería pasar:** el desplegable toma el foco al abrirse, las flechas recorren sus entradas, Escape lo cierra y devuelve el foco al «···»; y el tooltip del botón no se pinta mientras su menú está abierto.
- **Evidencia:** `dark-completa/01b-portafolio-mas.png` — el menú, con «Eliminar la aplicación…» como única entrada, colgando del «···» de la esquina superior derecha de la tarjeta. Comportamiento de teclado y colisión del tooltip medidos sobre `dist\Atalaya.exe`.
- **Principio:** D-944.7 (el menú es un lugar al que se llega) y D-944.4 (la acción destructiva tiene que poder pulsarse a conciencia, no solo con el ratón).
- **Gravedad:** alta

### A2-03 — El foco se marca con el rectángulo de puntos de fábrica de WPF: 1,21:1 en tema oscuro

- **Vista y lugar:** toda la aplicación; se ve en cualquier control (raíl, botones de tarjeta, filtros).
- **Combinación:** todas; el problema de contraste es del tema oscuro.
- **Qué pasa:** ningún control del sistema declara su anillo de foco, así que WPF pinta el suyo: un rectángulo de puntos de 1 px en negro. Medido sobre el «···» del Portafolio en tema oscuro: los puntos son `#0F1216` sobre la superficie `#1F242D` — **1,21:1**. Es el único elemento visual de la aplicación que **no cambia con el tema**, porque no sale de la paleta (D-945): en claro se ve porque el negro sobre crema se ve, no porque nadie lo haya elegido. Y su forma —rectángulo a hueso, a sangre de la fila— no coincide con la pastilla redondeada que marca la entrada activa del raíl, así que en el raíl el foco y el «estás aquí» se dibujan con dos lenguajes distintos.
- **Qué debería pasar:** un anillo de foco propio, con su token y sus dos claves de paleta, por encima de 3:1 en los dos temas y con el radio del control que rodea.
- **Evidencia:** medido sobre `dist\Atalaya.exe`, tema oscuro, foco en el «···» de la tarjeta del Portafolio (zona centro-izquierda, esquina superior derecha de la tarjeta): borde `#0F1216` / superficie `#1F242D`, 1,21:1.
- **Principio:** D-944.5 y D-945 (el tema cambia todos los recursos), D-947/D-961.
- **Gravedad:** alta

### A2-04 — Media aplicación llega sin nombre al árbol de accesibilidad, y las secciones de Ajustes se anuncian como «Atalaya, ventana»

- **Vista y lugar:** la carcasa entera y las cinco vistas medidas (Portafolio, Hallazgos, Inventario, Ajustes).
- **Combinación:** todas.
- **Qué pasa:** volcado el árbol de `dist\Atalaya.exe`, llegan **sin nombre**: la flecha de volver (`Button name=''`), la fila de cuenta del pie del raíl (`Button name=''`), los cuatro combos de filtro de Hallazgos, el buscador, las 111 filas de hallazgo (`Button name=''`), las casillas y filas de módulo del Inventario, y los dos combos de Ajustes. Los bloques del raíl llegan como `DataItem 'Atalaya.App.ViewModels.NavGroup'` y las entradas como `'…NavItem'` —el nombre de la clase—, de modo que **la promesa de D-1000 §2 («el nombre del grupo pasa a ser el nombre de automatización del bloque») no se cumple en el binario**: el nombre está declarado sobre un `StackPanel`, que no llega a la vista de control. Y las cinco secciones de Ajustes, que sí declaran `AutomationProperties.Name="{Binding Label}"`, se enfocan como **`[Window] Atalaya`** con el rectángulo de la ventana entera (paradas 18–22 de las 28 del ciclo): el nombre está escrito y no llega. Encima, con el foco ahí **Espacio activa la sección pero Enter no hace nada y las flechas tampoco mueven**: en una lista de cinco entradas hay que tabular una a una.
- **Qué debería pasar:** todo control que se enfoca dice su nombre; una lista de cinco secciones se recorre con flechas y se activa con Enter y con Espacio.
- **Evidencia:** medido sobre `dist\Atalaya.exe` (volcado completo del árbol UIA en Ajustes y en Hallazgos, y las 34 tabulaciones del banco reproducidas literalmente). En captura: `dark-completa/11-ajustes.png`, columna de secciones a la izquierda del contenido — las cinco entradas que no tienen nombre en el árbol.
- **Principio:** D-944.7 y D-1000 §2 (que declara explícitamente lo contrario).
- **Gravedad:** alta

### A2-05 — El inventario no está a un paso desde Métricas, Cuenta, Ajustes, Acerca de ni Nueva aplicación

- **Vista y lugar:** el raíl, en las vistas 02, 08, 09, 10 y las cinco de Ajustes.
- **Combinación:** todas.
- **Qué pasa:** el bloque de la aplicación —donde vive «Inventario»— solo se pinta si hay aplicación activa, y **Portafolio la borra a propósito** (D-953). Consecuencia: en cuanto se pasa por Portafolio, el raíl de Métricas, Cuenta, Ajustes, Acerca de y Nueva aplicación se queda sin la entrada, y volver al inventario cuesta **dos pasos** (Portafolio → «Abrir inventario»). En el propio banco pasa de una captura a la siguiente: `07b` tiene «Inventario» en el raíl y `08` ya no.
- **Qué debería pasar:** la entrada no desaparece. O el raíl recuerda la última aplicación mirada aunque se pase por Portafolio, o «Inventario» se queda siempre y, sin aplicación elegida, abre el selector.
- **Evidencia:** `dark-completa/08-metricas.png`, `09-cuenta.png`, `10-acerca-de.png`, `11-ajustes.png` … `11e-ajustes-avanzado.png` y `02-nueva-aplicacion.png` — el raíl de la izquierda: Portafolio · Hallazgos · Informes · Métricas | Cuenta · Ajustes · Acerca de, sin «Inventario». Compárese con `07b-informe-con-hallazgos.png`, donde sí está. Igual en las cuatro carpetas.
- **Principio:** D-944.6 («el inventario es alcanzable en un paso desde cualquier sitio»).
- **Gravedad:** alta

### A2-06 — La miga dice «Portafolio › XBLAST › Hallazgos» mientras la lista enseña los hallazgos de todas las aplicaciones

- **Vista y lugar:** Hallazgos › la barra de la miga, contra el combo «Aplicación» de la barra de filtros.
- **Combinación:** todas.
- **Qué pasa:** la miga mete el eslabón de la aplicación cuando hay **aplicación activa de ventana**, y el filtro de la lista es otra cosa. Reproducido: entrar en Hallazgos desde «Ver hallazgos» del Inventario y poner el filtro «Aplicación: Todas» deja la miga en `Portafolio › XBLAST › Hallazgos` con la lista enseñando los hallazgos de todo el portafolio. La miga dice que estás dentro de XBLAST y no lo estás — y su eslabón «XBLAST» no lleva a la lista que estás mirando, lleva al inventario. Al revés también pasa: la misma página con el mismo filtro sale con dos migas distintas según por dónde entres (`04-hallazgos.png` = `Portafolio › Hallazgos`).
- **Qué debería pasar:** el eslabón de la aplicación se pinta cuando la página está mostrando esa aplicación, no cuando la ventana la recuerda; una lista sin filtro de aplicación no lleva eslabón de aplicación.
- **Evidencia:** `dark-completa/04-hallazgos.png` (miga de dos eslabones, filtro «Todas») frente a la misma vista medida sobre el `dist` con miga de tres eslabones y el mismo filtro «Todas» — barra de miga arriba a la izquierda contra el combo «Aplicación:» de la barra de filtros.
- **Principio:** D-944.6 y D-955.
- **Gravedad:** alta

### A2-07 — Cada salto devuelve el foco a la raíz de la ventana: de 13 a 22 paradas de carcasa antes del primer control de la página

- **Vista y lugar:** toda la aplicación; medido en Portafolio, Hallazgos y Ajustes.
- **Combinación:** todas.
- **Qué pasa:** al navegar con el teclado (Enter sobre una entrada del raíl) el foco **no va a la página nueva ni se queda en la entrada pulsada**: vuelve al elemento ventana. A partir de ahí, para llegar al primer control del contenido hay que tabular por todo el raíl y toda la miga: **14 paradas en Portafolio, 13 en Hallazgos, 22 en Ajustes**. De esas paradas, 6 o 7 por ciclo son **contenedores que no hacen nada** (`[List]` de los grupos del raíl, `[List]` de la miga, una `[List]` fuera de pantalla, el `Pane` del scroll). Y el recorrido no sigue el orden visual: la primera parada es el botón de plegar (arriba), la **segunda es la fila de cuenta del pie del raíl** (y ~900 px más abajo), y solo después vienen las entradas del menú.
- **Qué debería pasar:** al cambiar de página el foco entra en la página; los contenedores no paran el foco; y en el raíl se tabula de arriba abajo.
- **Evidencia:** medido sobre `dist\Atalaya.exe`; las 28 paradas de Ajustes coinciden exactamente con las que trae el banco, y las de Portafolio y Hallazgos se han medido igual.
- **Principio:** D-944.6 (la navegación conserva el estado — también el del foco) y D-944.8.
- **Gravedad:** media

### A2-08 — El bloque de sistema del raíl se mueve hasta 137 px entre vistas

- **Vista y lugar:** el raíl, comparando vistas.
- **Combinación:** todas.
- **Qué pasa:** el bloque de la aplicación aparece, desaparece y crece (1, 2 o 3 entradas: Inventario, Última sesión, Último arreglo), y con él bajan «Cuenta», «Ajustes» y «Acerca de». Medido en las capturas: Cuenta está en y=299 en Portafolio, en y=356 en el Inventario, en y=396 con una sesión y en y=436 con sesión y arreglo. Son **137 px de recorrido** para tres entradas que el usuario aprende de memoria y va a pulsar sin mirar. D-954 se preocupó de que la entrada activa no bailara **tres píxeles**; el mismo defecto está aquí a escala de cuarenta veces.
- **Qué debería pasar:** el bloque de sistema queda anclado al pie del raíl (como ya lo está la fila de cuenta) y lo que crece es el hueco de en medio; o el bloque de la aplicación reserva su sitio.
- **Evidencia:** `dark-completa/01-portafolio.png` (Cuenta a y≈299) · `dark-completa/03-inventario.png` (y≈356) · `densas/dark-completa-05-sesion-en-vivo.png` (y≈396) · `densas/dark-completa-06-arreglo-asistido.png` (y≈436) — el bloque inferior del raíl.
- **Principio:** D-944.7, D-954.
- **Gravedad:** media

### A2-09 — La miga no tiene el mismo grano en todas las vistas: en tres sitios no dice en qué página estás

- **Vista y lugar:** la barra de la miga, comparada entre las 18 vistas.
- **Combinación:** todas.
- **Qué pasa:** tres desviaciones sobre la misma regla, comparando las 18 migas del recorrido:
  1. **Inventario** acaba en el nombre de la aplicación y no dice «Inventario» (`Portafolio › XBLAST`), cuando todas las demás acaban en su página. La propia documentación de `Crumbs` dice «Portafolio › XBLAST › Inventario» y el código salta el último eslabón.
  2. **Las cinco secciones de Ajustes tienen la misma miga** (`Portafolio › Ajustes`). Pero D-985 hizo de la sección un **destino** —Métricas enlaza a «Ajustes → Tarifas»—, así que se aterriza en un sitio que la miga no sabe nombrar y no se puede volver a la sección anterior.
  3. **El informe abierto tiene la miga de la lista** (`Portafolio › Informes`, idéntica a `06-informes.png`): la miga no distingue leer un informe de mirar la lista, y no ofrece la vuelta.
- **Qué debería pasar:** la miga acaba siempre en la página que estás mirando, con el mismo grano: `… › Inventario`, `… › Ajustes › Tarifas`, `… › Informes › Sesión 5 sept 10:34`.
- **Evidencia:** `dark-completa/03-inventario.png` (miga arriba a la izquierda: solo dos eslabones) · `11-ajustes.png` a `11e-ajustes-avanzado.png` (cinco capturas, la misma miga) · `06-informes.png` frente a `07-informe-abierto.png` y `07b-informe-con-hallazgos.png` (tres capturas, la misma miga).
- **Principio:** D-944.6, D-955, D-985.
- **Gravedad:** media

### A2-10 — El raíl nunca dice de qué aplicación es su bloque, y el arreglo llama a la misma aplicación de tres formas en una pantalla

- **Vista y lugar:** el raíl (bloque de la aplicación) y la cabecera del Arreglo asistido.
- **Combinación:** todas.
- **Qué pasa:** desde D-1000 §2 el raíl no pinta rótulos de grupo, y con ellos se fue el único sitio donde ponía el nombre de la aplicación. Hoy el bloque dice «Inventario», «Última sesión», «Último arreglo» **sin decir de quién son**: el raíl es el único mapa permanente de la ventana y no contesta «¿sobre qué aplicación estoy trabajando?». Y donde sí se dice, se dice de tres maneras a la vez: en la pantalla de Arreglo asistido la miga pone `atalayabanco-app-for-tests`, la cabecera pone `atalayabanco-app-for-tests` (recortado a «atala…» a 1280) y la tira del pie de la carcasa pone `atalayabanco`.
- **Qué debería pasar:** el bloque lleva el nombre de la aplicación —como rótulo, como primera entrada o como pastilla en la raya que lo separa— y la aplicación se nombra igual en la miga, en la cabecera y en el pie.
- **Evidencia:** `dark-completa/03-inventario.png` y `densas/dark-completa-06-arreglo-asistido.png`, bloque central del raíl. Los tres nombres, en `densas/dark-completa-06-arreglo-asistido.png`: miga arriba, cabecera a la derecha del identificador, y pie de la ventana. A 1280, `densas/dark-1280-06-arreglo-asistido.png` lo recorta a «atala…».
- **Principio:** D-944.6, D-944.7, D-953.
- **Gravedad:** media

### A2-11 — Cuando el arreglo termina, el raíl dice «Último arreglo» y el título y la miga siguen diciendo «Arreglo asistido»

- **Vista y lugar:** Arreglo asistido › pantalla de cierre: entrada activa del raíl, título de la vista y último eslabón de la miga.
- **Combinación:** todas.
- **Qué pasa:** al acabar el arreglo, la entrada del raíl cambia de rótulo y de icono («Arreglo asistido» + punto verde → «Último arreglo» + llave), pero **la página no**: el título sigue siendo «Arreglo asistido» y la miga también. En la misma pantalla, los tres sitios que dicen dónde estás dicen dos cosas distintas. La sesión, en el mismo caso, sí cuadra («Última sesión» en los tres).
- **Qué debería pasar:** el rótulo del raíl, el título y el último eslabón de la miga son la misma cadena.
- **Evidencia:** `densas/dark-1280-07-arreglo-cierre.png` — raíl a la izquierda: «Último arreglo» resaltado; miga arriba: `… › Arreglo asistido`; título: «Arreglo asistido». Compárese con `densas/dark-completa-08-ultima-sesion.png`, donde los tres dicen «Última sesión».
- **Principio:** D-944.6, D-955.
- **Gravedad:** media

### A2-12 — El marcador de «estás aquí» del raíl no se pinta: el carril de 8 px está vacío en las cuatro combinaciones

- **Vista y lugar:** el raíl y la lista de secciones de Ajustes (comparten plantilla).
- **Combinación:** todas.
- **Qué pasa:** la plantilla declara un marcador de 3 px que se pinta de `Brush.Primary.Fill` cuando la entrada está activa, y D-954 lo describe como columna propia que existe siempre. **No aparece un solo píxel de azul primario en el raíl de ninguna de las cuatro capturas** (barrido de los 232 px de ancho del raíl por su alto entero, en `dark-completa`, `dark-1280`, `light-1280` y el raíl plegado): el carril de 8 px queda con el fondo del raíl y toda la señal de «estás aquí» recae en el relleno de la pastilla, que mide **1,27:1 en oscuro y 1,21:1 en claro** contra el fondo del raíl. Desplegado lo salva el texto en seminegrita; **plegado no hay texto**, y lo único que queda es ese relleno más un icono un punto más claro.
- **Qué debería pasar:** o la barra se pinta (probablemente no cabe: 4 de margen + 3 de barra + 4 de margen = 11 en un carril de 8, que es el defecto de D-963/D-999 §3 por tercera vez), o se retira del sistema y el «estás aquí» se resuelve con un relleno que se vea.
- **Evidencia:** `dark-completa/03-inventario.png`, `dark-1280/03-inventario.png`, `light-1280/03-inventario.png` y `dark-completa/12-rail-plegado.png` — los 8 px a la izquierda de la pastilla activa, medidos: fondo del raíl `#1F242D`, pastilla `#243554`, ni un píxel de `#3A72DD`.
- **Principio:** D-944.7, D-954, D-966.
- **Gravedad:** media

### A2-13 — Plegado, «Inventario» lleva el icono del botón de plegar, y la sesión y el arreglo no llevan icono sino un punto

- **Vista y lugar:** el raíl plegado a iconos.
- **Combinación:** todas.
- **Qué pasa:** dos entradas del raíl no se distinguen cuando se pierde el texto:
  - El icono de **Inventario** son tres rayas horizontales; el botón de **plegar o desplegar el menú** son tres rayas horizontales. La única diferencia es que la tercera raya de Inventario es más corta. Plegado quedan los dos en la misma columna de 40 px, separados por 40 px de alto: el gesto de plegar el menú y el sitio más visitado de la aplicación se dibujan igual.
  - **«Sesión en vivo» y «Arreglo asistido» no tienen icono**: en el canal de 24 px llevan un punto verde de ~10 px, mientras todas las demás llevan un trazo de 24. Plegado, la entrada de lo que está corriendo —la que más falta hace localizar— queda como un punto suelto, indistinguible del piloto de estado que lleva el avatar dos filas más abajo.
- **Qué debería pasar:** un icono por entrada, distinguible del botón de plegar; y el «está corriendo» se dice con el color o el latido **del icono**, no sustituyéndolo.
- **Evidencia:** `dark-completa/03-inventario.png` (el icono de «Inventario», y el botón de plegar justo encima, en la misma columna) junto a `dark-completa/12-rail-plegado.png` (plegado solo quedan los iconos) · `densas/dark-completa-05-sesion-en-vivo.png` y `densas/dark-completa-06-arreglo-asistido.png` — el punto verde en el canal de iconos.
- **Principio:** D-944.7, D-950, D-966.
- **Gravedad:** media

### A2-14 — La flecha de volver se pinta igual apagada que encendida: en la primera pantalla hay una flecha muerta

- **Vista y lugar:** la barra de la miga › la flecha «‹» de la izquierda.
- **Combinación:** todas.
- **Qué pasa:** recién arrancada la aplicación, en Portafolio, la flecha de volver está **deshabilitada** (medido: `IsEnabled=False`, ni siquiera entra en la tabulación) y se pinta con **exactamente el mismo color, `#A8B0BD`**, que cuando funciona. Es la primera cosa arriba a la izquierda de la primera pantalla, tiene forma de control y no hace nada. Y en el propio Portafolio la miga es un solo eslabón, así que la flecha es el único gesto de «atrás» que se ofrece ahí.
- **Qué debería pasar:** un control apagado se lee como apagado (D-983 §4 lo resolvió para los botones y esta flecha se quedó fuera); o, si no hay a dónde volver, no se pinta.
- **Evidencia:** `dark-completa/01-portafolio.png` y `light-completa/01-portafolio.png`, esquina superior izquierda de la barra de miga — la flecha, con el mismo `#A8B0BD` medido en `03-inventario.png`, donde sí está habilitada. `IsEnabled=False` medido sobre `dist\Atalaya.exe` recién arrancado.
- **Principio:** D-944.4, D-949.
- **Gravedad:** media

### A2-15 — «Ver hallazgos» borra en silencio el filtro que el raíl sí conserva

- **Vista y lugar:** Inventario › cabecera, botón «Ver hallazgos» → Hallazgos › barra de filtros.
- **Combinación:** todas.
- **Qué pasa:** D-952 funciona por el raíl: puesto «Gravedad: Alta», salir a Métricas o a Portafolio y volver a Hallazgos por el raíl devuelve «Alta» (comprobado). Pero llegar a la misma página por «Ver hallazgos» **restablece toda la barra**: la gravedad vuelve a «Todas» y solo se conserva la aplicación. La misma vista vuelve de dos maneras distintas según la puerta, y nada lo dice.
- **Qué debería pasar:** un enlace que añade un filtro añade ese filtro y respeta los demás; si limpia, lo dice —«filtrado por esta sesión», con su «Limpiar filtros» al lado, que ya existe—.
- **Evidencia:** medido sobre `dist\Atalaya.exe`: «Gravedad: Alta» sobrevive a Métricas y a Portafolio y muere al pulsar «Ver hallazgos» de `dark-completa/03-inventario.png` (cabecera, arriba a la derecha).
- **Principio:** D-944.6, D-952, D-973.
- **Gravedad:** media

### A2-16 — En «Nueva aplicación» el botón que crea la aplicación se va con el scroll, y no hay por dónde cancelar

- **Vista y lugar:** Nueva aplicación › el pie del formulario.
- **Combinación:** todas; a 1280 el botón no se ve en ningún momento sin desplazar.
- **Qué pasa:** «Crear e inventariar» es la última fila de un formulario que hace scroll. A 1920 queda a ras del borde inferior de la ventana; a 1280 está fuera de la pantalla y nada anuncia que exista. Es el defecto que D-987 describió para Ajustes («Guardar estaba al fondo de un scroll») y que D-1000 §1 resolvió allí fijando la fila de acciones al pie de la columna: **la otra pantalla de formulario de la aplicación no recibió el mismo tratamiento**. Y no hay «Cancelar»: para salir hay que usar la flecha o la miga, mientras el raíl marca «Portafolio» como entrada activa — la entrada resaltada del menú es, literalmente, el botón que abandona el formulario sin avisar.
- **Qué debería pasar:** la misma fila de acciones que Ajustes, pegada al pie de la columna, con su «Cancelar» al lado.
- **Evidencia:** `dark-completa/02-nueva-aplicacion.png` — «Crear e inventariar» a ras del borde inferior, con la barra de desplazamiento a la derecha; `dark-1280/02-nueva-aplicacion.png` — el formulario acaba en «Stack / Detectar stack» y no hay ningún botón de acción a la vista. En los dos, el raíl marca «Portafolio».
- **Principio:** D-944.1, D-944.8, D-987, D-1000 §1.
- **Gravedad:** media

### A2-17 — «Auditar selección» está encendido con cero unidades seleccionadas y sin decir cuántas

- **Vista y lugar:** Inventario › la barra de la lista, botón primario.
- **Combinación:** todas.
- **Qué pasa:** con ninguna casilla marcada, el primario se pinta a plena intensidad y está habilitado (medido: `IsEnabled=True`). D-999 §1 bajó ese botón a la barra de la lista precisamente **con su recuento** —«Auditar 3 seleccionadas»— «porque es lo que se va a gastar»; con cero seleccionadas no hay recuento y tampoco hay freno: el botón que gasta créditos del usuario está encendido sin nada que auditar y sin decir la razón al lado.
- **Qué debería pasar:** deshabilitado mientras no haya selección, con la razón al lado («Marca las unidades que quieras auditar»), y el recuento en el rótulo en cuanto la haya.
- **Evidencia:** `dark-completa/03-inventario.png` y `dark-1280/03-inventario.png` — barra de la lista, el botón azul «Auditar selección» con las 923 casillas de debajo sin marcar. `IsEnabled=True` medido sobre `dist\Atalaya.exe`.
- **Principio:** D-944.4, D-999 §1.
- **Gravedad:** media

### A2-18 — Ni la ficha ni el informe abierto ofrecen en la miga la vuelta a su lista, y el informe apila dos «volver»

- **Vista y lugar:** ficha de un hallazgo y informe abierto › barra de miga y cabecera de la vista.
- **Combinación:** todas.
- **Qué pasa:** en la ficha la miga es `Portafolio › XBLAST › BUG-0008` y no contiene «Hallazgos», que es de donde vienes; su eslabón intermedio lleva al **inventario**, no a la lista, así que la miga no sirve para volver a los hallazgos filtrados y solo queda la flecha. En el informe abierto pasa lo mismo y además la vista añade **su propio «← Volver»** dentro del contenido, a 95 px por debajo de la flecha «‹» de la carcasa: dos controles de volver, con dos formas distintas y dos alcances distintos, uno encima del otro. En el arreglo hay un tercer patrón, «Volver al hallazgo», en la cabecera.
- **Qué debería pasar:** un solo gesto de volver, el de la carcasa, y una miga que incluya la lista de la que cuelga la ficha.
- **Evidencia:** `dark-completa/05-hallazgo-ficha.png` (miga arriba, sin «Hallazgos») · `dark-completa/07-informe-abierto.png` (la flecha «‹» a y≈86 y el botón «← Volver» a y≈180, en la misma columna) · `densas/dark-completa-06-arreglo-asistido.png` («Volver al hallazgo» en la cabecera).
- **Principio:** D-955.
- **Gravedad:** baja

### A2-19 — Dos controles del raíl llevan a «Cuenta», y el de abajo no tiene nombre y va el segundo en la tabulación

- **Vista y lugar:** el raíl › entrada «Cuenta» y fila de usuario del pie.
- **Combinación:** todas.
- **Qué pasa:** la fila del avatar del pie del raíl abre Cuenta (su tooltip lo dice: «… · abrir Cuenta») y la entrada «Cuenta» del bloque de sistema también. Son dos entradas para el mismo sitio en el mismo menú, y **la de abajo no marca «estás aquí»** cuando estás en Cuenta (lo marca la de arriba), no tiene nombre de accesibilidad y es la **segunda parada de tabulación** de toda la ventana, antes que las entradas del menú que tiene encima.
- **Qué debería pasar:** o la fila de usuario deja de navegar y es solo identidad y piloto, o la entrada «Cuenta» se retira del bloque de sistema; y en cualquier caso el pie se tabula al final.
- **Evidencia:** `dark-completa/09-cuenta.png` — «Cuenta» resaltado en el bloque de sistema y la fila «alopezciller» del pie sin resaltar, las dos en el mismo raíl. Orden de tabulación y nombre vacío medidos sobre `dist\Atalaya.exe`.
- **Principio:** D-944.7.
- **Gravedad:** baja

## Propuestas

### P-A2-01 — Un selector de aplicación en la barra de la miga

- **Qué.** El eslabón de la aplicación de la miga se convierte en un desplegable: `Portafolio › XBLAST ▾ › Hallazgos`. Al abrirlo, la lista de aplicaciones del portafolio, con búsqueda si pasan de diez. Elegir una **cambia la aplicación activa sin salir de la página**: sigues en Hallazgos, ahora los de otra aplicación.

```
 ‹  Portafolio ›  XBLAST ▾ ›  Hallazgos
                  ┌──────────────────┐
                  │ ⌕ buscar…        │
                  │ • XBLAST      111│
                  │   AtalayaBanco  13│
                  │   Visor          0│
                  └──────────────────┘
```

- **Por qué.** Hoy cambiar de aplicación pasa **siempre** por Portafolio, que además borra la aplicación activa y hace desaparecer «Inventario» del raíl (A2-05). Con un portafolio de veinte aplicaciones, comparar dos es un viaje de ida y vuelta por la raíz cada vez. Esto lo baja a dos clics desde cualquier vista y le da a la miga un trabajo que hoy no tiene: la miga solo se lee, nunca se usa.
- **Coste.** La barra de la miga (una vista), `MainViewModel.Shell` y `ActiveApp`. Arrastra lógica: hay que decidir qué hace cada página al cambiarle la aplicación debajo —Hallazgos e Informes ya tienen `SetApp`, la ficha tendría que volver a su lista—. Es la propuesta más cara de las nueve y la que más cambia el mapa.

### P-A2-02 — «Ir a…» (Ctrl+K) y números para el raíl

- **Qué.** `Ctrl+K` abre un cuadro de búsqueda sobre todo lo que es un destino: las ocho páginas, las cinco secciones de Ajustes, las aplicaciones del portafolio y sus inventarios, y los informes por fecha. `Ctrl+1..8` van a las entradas del raíl en orden. `Ctrl+I` va al inventario de la aplicación activa.
- **Por qué.** Es la respuesta directa a A2-07 y a A2-05: hoy llegar al primer control de una página con el teclado cuesta entre 13 y 22 tabulaciones, y llegar a «Ajustes → Tarifas» son cuatro gestos de ratón. Con esto, cualquier destino son tres teclas. Ahorra más cuanto más crece el portafolio, que es justo donde la navegación actual se rompe.
- **Coste.** Una ventana nueva, un servicio que enumere destinos y `InputBindings` en `MainWindow`. No toca ninguna vista y no arrastra lógica de negocio: los destinos ya son datos (`NavGroups`, `SettingsSectionItem`).

### P-A2-03 — Un solo patrón de superposición, con foco, trampa y Escape

- **Qué.** Un estilo `Overlay` que gobierne las tres cosas que hoy flotan con reglas distintas: el cajón del resumen del ciclo, los desplegables de tarjeta y los toasts. Contrato único: al abrirse toma el foco, lo retiene mientras está abierto, Escape lo cierra, y al cerrarse lo devuelve a quien lo abrió. Los toasts, además, no se pintan encima de un control: se apilan en un carril propio del pie.
- **Por qué.** Arregla A2-01 y A2-02 de una vez y evita el tercer caso que vendrá. Hoy cada superposición se comporta distinta porque cada una la escribió su vista, que es exactamente el argumento de D-946 y D-951 aplicado al comportamiento en vez de a las medidas.
- **Coste.** Un `Behavior` o un `ContentControl` en `Styles.xaml` y tres sitios de uso (Inventario, Portafolio, la carcasa de toasts). Sin lógica de negocio.

### P-A2-04 — El anillo de foco entra en la paleta

- **Qué.** Dos claves nuevas (`Focus.Ring` y `Focus.RingOffset`), un `FocusVisualStyle` propio de 2 px con el radio del control, y prohibirlo escrito a mano con el test que ya vigila los colores (D-983 §8).
- **Por qué.** A2-03. Es lo último de la interfaz que sigue pintándolo Windows y no Atalaya, y es lo único que se rompió en las dos revisiones sin que nadie lo viera: 1,21:1 no se ve en una captura, se ve midiendo, que es la lección de D-945.
- **Coste.** `Themes/Palette.*.xaml`, `Tokens.xaml`, `Styles.xaml`. Cero vistas.

### P-A2-05 — Atalaya reabre donde la dejaste

- **Qué.** Guardar en el ajuste la última página y la última aplicación activa, y restaurarlas al arrancar (con la misma cautela que D-956 tiene con la ventana: si la aplicación ya no existe, se cae al Portafolio).
- **Por qué.** D-952 hace que la navegación conserve el estado **dentro de una sesión de ventana** y lo tira entero al cerrar. Una herramienta que se abre cada mañana para seguir con la misma aplicación arranca todos los días en la pantalla que menos falta hace. Es un ajuste, no un cambio de mapa, y cuesta un clic menos al día por usuario — pero además es lo que hace que la aplicación activa deje de ser un accidente del recorrido.
- **Coste.** `AppSettings.Window` (o una sección nueva), `MainViewModel.InitializeAsync` y `SaveWindowPlacement`. Arrastra una decisión: si la última página era una sesión en vivo que ya terminó, se abre su cierre y no la sesión.

### P-A2-06 — «Saltar al contenido» como primera parada de tabulación

- **Qué.** Un enlace invisible hasta que recibe el foco, primera parada de la ventana, que lleva el foco al primer control de la página. Y quitar del recorrido los contenedores que hoy paran el foco sin hacer nada.
- **Por qué.** Es la mitad barata de A2-07: sin tocar el orden general, baja de 22 paradas a 1 el coste de empezar a trabajar en Ajustes. Es un patrón conocido de la web que aquí no existe porque en escritorio nadie se lo plantea, y esta aplicación tiene una carcasa de catorce controles delante de cada página.
- **Coste.** `MainWindow.xaml` y un puñado de `KeyboardNavigation.IsTabStop="False"` en los `ItemsControl`. Nada más.

### P-A2-07 — El bloque de la aplicación lleva su nombre en la raya que lo separa

- **Qué.** La línea de un píxel que D-1000 §2 puso entre bloques lleva, en el bloque de la aplicación y solo ahí, el nombre de la aplicación pegado a la izquierda, a 13 px, en tinta terciaria. Plegado, la raya se queda sola, como ahora.

```
 ─── XBLAST ────────────
  ▤  Inventario
  ⏱  Última sesión
```

- **Por qué.** A2-10. D-1000 retiró «TRABAJO» y «SISTEMA» con razón —dos palabras que no llevaban a ningún sitio— pero el nombre de la aplicación **sí lleva a un sitio**: dice de quién es todo lo que hay debajo. Retirarlo con los otros dos fue tirar el único rótulo que informaba. Contradice a medias D-1000 §2, y el argumento nuevo es ése: los tres rótulos no eran la misma cosa.
- **Coste.** El `DataTemplate` del grupo en `MainWindow.xaml` y una condición en `BuildRail`. Sin lógica.

### P-A2-08 — La flecha de volver, con su historial

- **Qué.** Mantener pulsada la flecha «‹» abre la pila de navegación (los últimos 20 que D-952 ya guarda), con el nombre de cada página y su aplicación. Y `Alt+←` / `Alt+→` como atajos.
- **Por qué.** La pila existe, está limitada a 20 y **no se puede ver**: la única forma de usarla es pulsar la flecha una vez por salto. Después de un rato entre una ficha, su informe y el inventario, volver tres sitios atrás son tres clics a ciegas. Es el gesto de cualquier navegador y aquí ya está la mitad hecha.
- **Coste.** `NavigationService` tiene que exponer la pila (hoy la esconde) y la barra de miga, un menú. Sin lógica de negocio.

### P-A2-09 — Un aviso al salir de Ajustes con cambios sin guardar

- **Qué.** Salir de Ajustes por el raíl o por la miga con la marca de sucio puesta abre una confirmación de tres salidas: Guardar, Descartar, Seguir editando.
- **Por qué.** D-987 dice, con todas las letras, que cambiar de página perdía lo tocado en silencio, y lo que puso fue **la señal** —«Hay cambios sin guardar»— que vive en la página que estás abandonando y desaparece justo en el momento en que hace falta. La señal avisa mientras miras; la confirmación avisa cuando te vas. Son dos cosas distintas y la segunda no se hizo.
- **Coste.** `SettingsViewModel` (ya tiene la huella de sucio), un gancho de «puedo salir» en `NavigationService` y el diálogo. Arrastra un contrato nuevo en la navegación —que una página pueda vetar la salida—, que es lo más caro de esta propuesta y lo que la haría útil también para «Nueva aplicación» a medio rellenar.
