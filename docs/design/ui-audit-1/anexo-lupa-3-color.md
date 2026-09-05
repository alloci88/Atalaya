## Hallazgos

### A3-01 — Los cinco diálogos no llevan la paleta: en claro son blancos y su acción principal es el azul de Windows

- **Vista y lugar:** los cinco diálogos, superficie entera (fondo, cajas de aviso, campos, botones).
- **Combinación:** los dos temas (los diálogos solo tienen dos).
- **Qué pasa:** la ventana del diálogo se pinta con el tema de fábrica de la librería, no con la paleta: fondo `#202020` en oscuro y **`#FAFAFA` —blanco— en claro**, contra el crema `#F2EBDD`/`#FAF5EC` de la aplicación; la caja de peligro es `#382424`/`#F5E1E1` en vez de `Danger.Soft` (`#42211F`/`#F7DCD9`); los campos son `#1F1F1F`/`#FFFFFF`; y el botón principal es `#1E9BFA`/`#0071C7` en vez de `Primary.Fill` (`#3A72DD`/`#2F62C9`). Abrir cualquier diálogo desde una pantalla crema levanta una hoja blanca con otro azul.
- **Qué debería pasar:** el diálogo es una superficie más de la aplicación y lee las mismas claves: crema en claro, `Danger.Soft` para la caja de peligro, `Primary.Fill` para el primario.
- **Evidencia:** `light-d1-vincular-clon.png` (fondo blanco y botón «Vincular» en azul Windows); `light-d3-eliminar-aplicacion.png` y `light-d4-restablecimiento-de-fabrica.png` (fondo blanco, caja rosa `#F5E1E1`); `dark-d3-eliminar-aplicacion.png` (fondo `#202020`, caja `#382424`); `light-d5-lanzar-auditoria.png` («Confirmar y auditar» en `#0071C7`).
- **Principio:** D-944.5 (y D-948: «claro sobre crema, no sobre blanco»; D-945).
- **Gravedad:** alta.

### A3-02 — Hay dos juegos de color de gravedad, y conviven en el mismo panel

- **Vista y lugar:** Sesión en vivo › panel «Hallazgos» (contadores arriba y chips de la lista); ficha de hallazgo › pastilla bajo el título.
- **Combinación:** todas.
- **Qué pasa:** además de las pastillas de la paleta (`Sev.*` sobre `*.Soft`) existe un segundo juego escrito a mano —crítica `#D13A3A`, alta `#E07A2B`, media `#D2B036`, baja `#6C93C0`— **idéntico en los dos temas**. En el panel «Hallazgos» de Sesión en vivo los dos aparecen a 40 px de distancia: los contadores usan `#F2645E`/`#F0883E`/`#E3B341`/`#799CEA` y los chips de la lista, justo debajo, usan el otro juego. La misma «Alta» sale en dos naranjas distintos en la misma columna. La pastilla «Alta» de la ficha usa también `#E07A2B` y no cambia entre temas.
- **Qué debería pasar:** una sola forma de pintar una gravedad, la de `Pill.Sev`/`Pill.Sev.Text` que ya usan Portafolio, Hallazgos, Métricas y el informe (D-990 lo dejó dicho: «cuatro sitios pintando una gravedad y una sola forma de hacerlo»).
- **Evidencia:** `densas/dark-completa-05-sesion-en-vivo.png` y `densas/light-completa-05-sesion-en-vivo.png`, panel derecho: fila de contadores arriba y chips de la lista debajo; `vistas/dark-completa/05-hallazgo-ficha.png` y `vistas/light-completa/05-hallazgo-ficha.png`, pastilla «Alta» arriba a la izquierda, igual en los dos temas.
- **Principio:** D-944.4 (y D-945, D-971).
- **Gravedad:** alta.

### A3-03 — La tinta sobre color vivo está fijada a negro o a blanco y no cambia con el tema: cuatro pastillas y un botón por debajo de AA

- **Vista y lugar:** Sesión en vivo › contadores del panel «Hallazgos» y botón «Detener»; ficha › pastilla de gravedad.
- **Combinación:** todas; el fallo se ve en claro (pastillas) y en oscuro (botón).
- **Qué pasa:** los contadores llevan tinta `#000000` en los dos temas. En claro el relleno pasa a los tonos oscuros del tema claro y el resultado es negro sobre marrón: **«Crítica 2» 3,59:1, «Alta 3» 3,56:1, «Media 2» 3,88:1, «Baja 1» ≈3,5:1**, los cuatro por debajo de 4,5:1. Al revés, «Detener» es `#F44336` (un rojo que no está en ninguna paleta) con tinta blanca en oscuro: **3,68:1**. La paleta ya tiene resuelto esto —`Ink.OnVivid` es `#10151B` en oscuro y `#FFFFFF` en claro—, y «Descartar todo» de la vista de al lado sí lo usa.
- **Qué debería pasar:** todo lo que se escribe encima de un relleno vivo pide `Ink.OnVivid`, que se invierte con el tema; y el rojo de un botón de peligro sale de `Danger.Fill`.
- **Evidencia:** `densas/light-completa-05-sesion-en-vivo.png`, fila de pastillas arriba del panel derecho (negro sobre `#B8352F`, `#9E4E17`, `#8D6200`, `#2E5FC3`); `densas/dark-completa-05-sesion-en-vivo.png`, «Detener» arriba a la derecha (`#F44336` con blanco); comparar con `densas/dark-completa-06-arreglo-asistido.png`, «Descartar todo» (`#F2645E` con `#10151B`).
- **Principio:** D-944.5 (D-947/D-961).
- **Gravedad:** alta.

### A3-04 — El estado de una unidad y el punto de «Vinculada» usan el color del tema oscuro también en el claro: entre 1,7:1 y 2,3:1

- **Vista y lugar:** Sesión en vivo › columna de unidades («completa», «pasada N», «pendiente» y el check); Portafolio › punto de estado de la tarjeta y la insignia del avatar del raíl.
- **Combinación:** las cuatro; ilegible en las dos de tema claro.
- **Qué pasa:** el check y el punto son `#3FB950` —el verde del tema **oscuro**— en los dos temas: sobre el crema dan **2,34:1**. Las palabras de estado son ese mismo verde/azul rebajado con opacidad, así que en claro salen `#7DCA81` («completa», **1,66:1**), `#84B9DF` («pasada», **1,77:1**) y `#ACAAA5` («pendiente», **1,96:1**). Y en oscuro, por el otro lado, «pendiente» se queda en `#606164` (**2,79:1**). El dato que dice si una unidad está hecha o no es lo menos legible de la columna.
- **Qué debería pasar:** los tres estados leen `Success.Ink`/`Primary.Ink`/`TextMuted` por `DynamicResource`, que ya existen con valor propio en cada tema, y el apagado se consigue cambiando de color, no bajando la opacidad (es exactamente lo que D-983 §4 resolvió para los botones).
- **Evidencia:** `densas/light-completa-05-sesion-en-vivo.png`, columna izquierda «Unidad 3 de 6»: «completa» y «pendiente» casi desaparecen sobre el crema; `vistas/light-completa/01-portafolio.png`, punto verde a la izquierda de «Vinculada» y el punto del avatar abajo a la izquierda.
- **Principio:** D-944.5 (D-945, D-947).
- **Gravedad:** alta.

### A3-05 — El panel derecho de la ficha se saltó el sistema: tres botones de fábrica y una pastilla de estado en 2,38:1

- **Vista y lugar:** ficha de hallazgo › tarjeta «Acciones» y pastilla «Activo» de la cabecera.
- **Combinación:** todas.
- **Qué pasa:** «Generar prompt de arreglo», «Verificar ahora» y «Abrir en el editor» se pintan `#DDDDDD` de fondo con texto `#000000` **exactamente igual en los dos temas**: en oscuro son tres barras blancas dentro de una tarjeta gris. Y la pastilla «Activo» lleva tinta `#4A9EE0` en los dos temas sobre un fondo que sí cambia (`#243343`/`#E4EAEA`), de modo que en claro se queda en **2,38:1**. Ninguno de los cuatro colores está en `Palette.*.xaml`.
- **Qué debería pasar:** las tres acciones son `Button.Secondary` del sistema (como «Sincronizar ahora» de Cuenta) y la pastilla de estado usa el par `*.Soft`/`*.Ink` que ya usan las demás pastillas.
- **Evidencia:** `vistas/dark-completa/05-hallazgo-ficha.png`, tarjeta «Acciones» a la derecha (tres barras claras bajo el botón verde) y pastilla «Activo» arriba a la izquierda; `vistas/light-completa/05-hallazgo-ficha.png`, las mismas cuatro piezas, idénticas píxel a píxel a las del tema oscuro.
- **Principio:** D-944.5 (D-949: cuatro variantes de botón y ninguna más).
- **Gravedad:** alta.

### A3-06 — El azul de fábrica de la librería (`#1E9BFA` / `#0071C7`) convive con el primario de Atalaya

- **Vista y lugar:** Ajustes › Auditoría y Apariencia (interruptores); Cuenta (los dos anillos de «Comprobando»); Portafolio (barra de progreso del ciclo); ficha › Gobernanza (radios); los cinco diálogos (primario, radios y foco de campo); Arreglo asistido («Responder» y «Enviar»); barra de estado de la sesión.
- **Combinación:** todas.
- **Qué pasa:** hay **tres azules distintos diciendo cosas cercanas en la misma pantalla**: `Primary.Fill` (`#3A72DD`/`#2F62C9`) en los botones propios, `Primary.Ink` (`#759DE8`/`#2E5FC3`) en los enlaces, y `#1E9BFA`/`#0071C7` —el acento de Windows que la paleta nunca declara— en todo lo que sigue siendo un control de la librería. En Cuenta se ven los tres a la vez; en Ajustes › Apariencia el interruptor encendido es azul Windows a 40 px de un aviso azul de la paleta.
- **Qué debería pasar:** la paleta ya redirige `AccentFillColorDefaultBrush` y compañía; falta cubrir las claves que leen estos controles (interruptor, anillo, `ProgressBar`, radio, foco), para que «encendido / en curso / esto es lo principal» sea un solo azul.
- **Evidencia:** `vistas/dark-completa/11b-ajustes-auditoria.png`, interruptor «Arreglo asistido» (`#1E9BFA` con maneta negra); `vistas/light-completa/11d-ajustes-apariencia.png`, interruptor `#0071C7` sobre el aviso `#D6E1F5`; `vistas/dark-completa/09-cuenta.png`, anillo de «Comprobando» y anillo de «Comprobando conexión…» junto al primario `#3A72DD`; `vistas/dark-completa/01-portafolio.png`, barra de «1,8 % auditado»; `densas/dark-completa-06-arreglo-asistido.png`, «Responder» y «Enviar».
- **Principio:** D-944.4 (D-945).
- **Gravedad:** alta.

### A3-07 — `Primary.Soft` significa cuatro cosas distintas, y tres de ellas caben en una captura

- **Vista y lugar:** raíl (entrada activa), Ajustes (sección activa), avisos informativos, pastilla de gravedad «baja».
- **Combinación:** todas.
- **Qué pasa:** el mismo relleno `#243554`/`#D6E1F5` se usa para «estás aquí» (entrada del raíl y sección de Ajustes), para «esto es información» (`Notice.Info`) y para «gravedad baja». En `dark-completa/11d-ajustes-apariencia.png` los tres primeros aparecen juntos; en `dark-completa/04-hallazgos.png` el mismo azul es a la vez la entrada «Hallazgos» del raíl y la pastilla «4 Baja» de la primera fila. En claro se suma que `Sev.Low` y `Primary.Ink` son literalmente el mismo valor (`#2E5FC3`), así que la cifra «20 Bajas» de Portafolio y el enlace «Expandir todo» de Inventario son el mismo color.
- **Qué debería pasar:** el azul suave se reserva para una cosa. La gravedad baja tiene su propio tono en la escala (o se pinta con la banda de gravedad y no con el mismo relleno que un aviso informativo).
- **Evidencia:** `vistas/dark-completa/11d-ajustes-apariencia.png` (raíl activo, sección «Apariencia» activa y el aviso azul, los tres `#243554`); `vistas/dark-completa/04-hallazgos.png` (raíl «Hallazgos» y pastilla «4 Baja», arriba a la derecha de la primera fila).
- **Principio:** D-944.4.
- **Gravedad:** media.

### A3-08 — Más de un botón primario por vista

- **Vista y lugar:** Portafolio (cabecera + tarjeta); Arreglo asistido (tarjeta de decisión + compositor).
- **Combinación:** todas.
- **Qué pasa:** Portafolio tiene «+ Nueva aplicación» y «Abrir inventario», los dos `Primary.Fill` macizo del mismo tamaño y peso; con N tarjetas serían N+1 azules. En Arreglo asistido hay dos primarios simultáneos —«Responder» dentro de la tarjeta ámbar y «Enviar» en el compositor de abajo— además de un aviso macizo («Detener») y un peligro macizo («Descartar todo»): cuatro botones gritando a la vez.
- **Qué debería pasar:** un primario por vista. La acción de la tarjeta puede ser secundaria (la tarjeta ya es su propio contexto) o la de la cabecera dejar de serlo; y de los dos campos de respuesta, solo uno lleva el primario.
- **Evidencia:** `vistas/dark-completa/01-portafolio.png` (arriba a la derecha y al pie de la tarjeta XBLAST, los dos `#3A72DD`); `densas/dark-completa-06-arreglo-asistido.png` (centro-abajo: «Responder» y «Enviar»; arriba a la derecha: «Detener» y «Descartar todo»).
- **Principio:** D-944.4.
- **Gravedad:** media.

### A3-09 — «El primario apagado hasta que haya algo que hacer» no es una regla: cuatro sitios y cuatro criterios

- **Vista y lugar:** Ajustes (Proveedor, Auditoría, Apariencia, Avanzado) vs. Ajustes › Tarifas; Inventario; Nueva aplicación.
- **Combinación:** todas.
- **Qué pasa:** en cuatro secciones de Ajustes «Guardar» está apagado en gris —correctamente contrastado, 6,4:1— **pero sin la razón al lado**; en la quinta sección, Tarifas, el mismo «Guardar tarifas» está encendido en `#3A72DD` sin que se haya tocado nada. Y fuera de Ajustes, «Auditar selección» está encendido con cero unidades marcadas y «Crear e inventariar» encendido con el repositorio sin elegir. La aplicación sí sabe hacerlo bien en otro sitio: la casilla «Importar el baseline al crear la aplicación» está deshabilitada, y tampoco dice por qué.
- **Qué debería pasar:** o el primario se apaga cuando no puede hacer nada y lleva su razón al lado, o se queda encendido; pero lo mismo en las cinco secciones y en las tres vistas.
- **Evidencia:** `vistas/dark-completa/11-ajustes.png` y `11e-ajustes-avanzado.png` («Guardar» gris, sin texto); `vistas/dark-1280/11c-ajustes-tarifas.png` («Guardar tarifas» azul, con la lista de secciones al lado para comparar); `vistas/dark-completa/03-inventario.png` («Auditar selección» azul con todas las casillas vacías); `vistas/dark-completa/02-nueva-aplicacion.png` («Crear e inventariar» azul con el repositorio en blanco, y la casilla apagada sin razón).
- **Principio:** D-944.4.
- **Gravedad:** media.

### A3-10 — «Crítica» y «Alta» comparten el mismo fondo de pastilla: la escala de cuatro niveles se lee como tres

- **Vista y lugar:** Métricas › tarjeta «Hallazgos activos»; Hallazgos › pastillas de la fila; informe abierto › «Resumen».
- **Combinación:** todas.
- **Qué pasa:** las pastillas toman el fondo de las familias semánticas y no de la escala de gravedad, y como no existe `Sev.Crit.Soft` la crítica reutiliza `Danger.Soft`: «Crít 0» y «Alta 27» tienen **exactamente el mismo relleno** (`#42211F` en oscuro, `#F7DCD9` en claro) y solo se distinguen por la tinta. A la distancia a la que se recorre una lista —que es justo para lo que D-973 puso el color— hay tres manchas, no cuatro.
- **Qué debería pasar:** cuatro rellenos para cuatro niveles, como ya hay cuatro tintas.
- **Evidencia:** `vistas/dark-completa/08-metricas.png` y `light-completa/08-metricas.png`, tarjeta «Hallazgos activos»: las cuatro pastillas en fila, las dos primeras con el mismo fondo.
- **Principio:** D-944.4 (D-973).
- **Gravedad:** media.

### A3-11 — Los mismos cuatro niveles se rotulan de cinco maneras, y una lleva falta de ortografía

- **Vista y lugar:** Portafolio, Hallazgos, Métricas, informe abierto, Sesión en vivo.
- **Combinación:** todas.
- **Qué pasa:** Portafolio dice «Críticas · Altas · Medias · Bajas»; Hallazgos, «15 Alta · 10 Media · 4 Baja»; Métricas, «Crít 0 · Alta 27»; el informe, «3 Altas · 6 Medias · 6 Bajas»; y Sesión en vivo, «Crítica 2» en los contadores y **«Critica» sin tilde** en los chips de la lista, a 40 px de distancia. Singular/plural, abreviado/entero, cifra delante/detrás: cinco combinaciones para el mismo dato.
- **Qué debería pasar:** un rótulo por nivel, escrito una vez, y la cifra siempre en el mismo lado.
- **Evidencia:** `densas/dark-completa-05-sesion-en-vivo.png`, panel «Hallazgos»: «Crítica 2» arriba y «Critica» en el segundo y cuarto chip; `vistas/dark-completa/08-metricas.png` («Crít 0»); `vistas/dark-completa/07b-informe-con-hallazgos.png` («3 Altas»); `vistas/dark-completa/04-hallazgos.png` («15 Alta»).
- **Principio:** D-944.4 (el significado tiene que llegar por palabra, no solo por color).
- **Gravedad:** media.

### A3-12 — Hay dos pastillas neutras distintas, y la que más se usa no llega a AA

- **Vista y lugar:** Portafolio › tarjeta e Inventario › «Resumen del ciclo» (pastilla de temática «General») frente a Informes › columna «Tipo» (pastilla «Sesión»).
- **Combinación:** todas; peor en claro.
- **Qué pasa:** la pastilla del sistema usa `Surface2` + `TextMuted` (`#262C37`/`#A8B0BD`, `#F6F0E4`/`#4F5761`) y da **6,45:1**. La pastilla de temática usa grises que no están en ninguna paleta —`#383E47`/`#9CA3AF` en oscuro, `#DDDBD7`/`#6B7280` en claro— y se queda en **4,25:1 en oscuro y 3,50:1 en claro**, con un texto de 12 px.
- **Qué debería pasar:** una sola pastilla neutra, la del sistema, en los tres sitios.
- **Evidencia:** `vistas/light-completa/01-portafolio.png` («General», junto a «Ciclo 1 · 1,8 % auditado»); `vistas/light-completa/03-inventario.png` («General» en el panel «Resumen del ciclo», arriba a la derecha); comparar con `vistas/light-completa/06-informes.png`, columna «Tipo».
- **Principio:** D-944.5 (D-947/D-961) y D-944.4.
- **Gravedad:** media.

### A3-13 — El mismo peso de acción destructiva se pinta macizo en una vista y perfilado en otra, y en el cierre del arreglo tapa al primario

- **Vista y lugar:** Cuenta («Desconectar»), Ajustes › Avanzado («Restablecimiento de fábrica»), Arreglo asistido / cierre («Descartar todo»).
- **Combinación:** todas.
- **Qué pasa:** D-999 §4 dejó la regla escrita —perfilado por defecto, macizo solo cuando la acción de ese color **es** la acción de la vista—. «Desconectar» la cumple (borde y tinta `Danger`), pero «Restablecimiento de fábrica» es macizo `#F2645E`/`#BA3630` y acaba siendo lo más saturado de una pantalla cuyo primario («Guardar») está apagado en gris. Peor en el cierre del arreglo: «Descartar todo» sigue macizo arriba a la derecha mientras el paso siguiente real, «Verificar ahora», está abajo y en el azul de la librería.
- **Qué debería pasar:** los dos, perfilados; el macizo se reserva para cuando destruir es lo que se ha venido a hacer (el diálogo de confirmación).
- **Evidencia:** `vistas/dark-completa/11e-ajustes-avanzado.png` (bloque «Zona peligrosa» y el «Guardar» gris justo debajo); `vistas/dark-completa/09-cuenta.png` («Desconectar» perfilado); `densas/dark-completa-07-arreglo-cierre.png` («Descartar todo» arriba a la derecha frente a «Verificar ahora» abajo).
- **Principio:** D-944.4.
- **Gravedad:** media.

### A3-14 — El estado del hub se dice con icono + color + palabra en una tarjeta y en texto neutro dos tarjetas más abajo

- **Vista y lugar:** Cuenta › «Estado de la conexión» (fila «Acceso al hub») y › «Hub local».
- **Combinación:** todas.
- **Qué pasa:** la misma verdad —el hub está sincronizado— se cuenta arriba con check verde + «Disponible» + el sello de hora, y abajo como «Estado: sincronizado · última sincronización: …» en tinta neutra, sin icono y sin color. D-989 fijó el patrón de icono + COLOR + PALABRA para las filas de conexión y la tarjeta de al lado no lo sigue: si el hub estuviera desincronizado, esa línea se leería exactamente igual.
- **Qué debería pasar:** un estado se dice siempre con las tres cosas, en las dos tarjetas.
- **Evidencia:** `vistas/dark-completa/09-cuenta.png`, tercera fila del bloque «Estado de la conexión» frente al primer renglón de la tarjeta «Hub local».
- **Principio:** D-944.4 (D-989).
- **Gravedad:** media.

### A3-15 — «Expandir todo» es un enlace azul en Inventario y un botón neutro en Hallazgos

- **Vista y lugar:** Inventario › barra de la lista (extremo derecho) y Hallazgos › cabecera (arriba a la derecha).
- **Combinación:** todas.
- **Qué pasa:** el mismo rótulo y la misma acción —desplegar todos los grupos de una lista— se pintan como enlace `Primary.Ink` (`#759DE8`/`#2E5FC3`) en Inventario y como botón secundario con borde y tinta neutra (`#E6E9EF`) en Hallazgos, y además en sitios distintos de la pantalla. El azul, que en el resto de la aplicación anuncia navegación, aquí anuncia un plegado.
- **Qué debería pasar:** un gesto, una forma: o los dos enlace o los dos botón secundario, y en el mismo sitio de la vista.
- **Evidencia:** `vistas/dark-completa/03-inventario.png` (derecha de la barra de filtros) y `vistas/dark-completa/04-hallazgos.png` (arriba a la derecha, junto al título).
- **Principio:** D-944.4.
- **Gravedad:** media.

### A3-16 — El aviso ámbar unas veces lleva icono y otras no

- **Vista y lugar:** Métricas, Ajustes › Tarifas y Ajustes › Apariencia (con icono) frente a ficha › tarjeta «Código» y cierre del arreglo (sin icono).
- **Combinación:** todas.
- **Qué pasa:** el mismo control de aviso aparece en tres formas: relleno + icono + enlace (Métricas, Tarifas, Apariencia), relleno sin icono (el «El código de la línea 145 ya no es el que se auditó» de la ficha) y relleno sin icono ni cabecera en el cierre del arreglo. En los dos últimos, «esto es un aviso» viaja solo en el color.
- **Qué debería pasar:** el aviso es un patrón: icono, texto y —si la hay— la acción, siempre.
- **Evidencia:** `vistas/dark-completa/08-metricas.png` (con ⚠ y enlace); `vistas/dark-1280/05-hallazgo-ficha.png` (banda ámbar dentro de la tarjeta «Código», sin icono); `densas/dark-completa-07-arreglo-cierre.png` (banda ámbar bajo «Arreglo terminado», sin icono).
- **Principio:** D-944.4 (D-991).
- **Gravedad:** baja.

### A3-17 — El cero se pinta de peligro

- **Vista y lugar:** Portafolio › azulejo «Críticas» y cifra de la tarjeta; Métricas › pastilla «Crít 0».
- **Combinación:** todas.
- **Qué pasa:** con cero críticas, el azulejo conserva el borde rojo, la cifra «0» va en `Sev.Crit` y la pastilla «Crít 0» sigue teñida de rojo. El color está diciendo «hay una crítica» donde el número dice lo contrario, y es el primer sitio donde cae la vista al abrir la aplicación.
- **Qué debería pasar:** un recuento a cero se pinta en neutro; el color de gravedad aparece cuando hay algo de esa gravedad.
- **Evidencia:** `vistas/light-completa/01-portafolio.png`, primer azulejo de la fila y primera cifra de la tarjeta XBLAST; `vistas/dark-completa/08-metricas.png`, pastilla «Crít 0».
- **Principio:** D-944.4 («color con significado, y solo con significado»).
- **Gravedad:** baja.

### A3-18 — La tarjeta de permiso ya contestada sigue pintada de aviso

- **Vista y lugar:** Arreglo asistido › conversación, tarjeta «El agente pide permiso».
- **Combinación:** todas.
- **Qué pasa:** la tarjeta resuelta («Tu respuesta: Autorizar») y la que sigue esperando («El agente necesita que decidas») tienen el mismo relleno `Warning.Soft` (`#3A3014`) y el mismo borde ámbar. Al bajar por el hilo, cada permiso ya contestado vuelve a reclamar atención, y lo único que las distingue es una línea de texto pequeña.
- **Qué debería pasar:** contestada la pregunta, la tarjeta baja a superficie neutra y deja el ámbar para la que sigue abierta.
- **Evidencia:** `densas/dark-completa-06-arreglo-asistido.png`, columna central: la tarjeta ámbar de arriba («Tu respuesta: Autorizar») y la de abajo, idénticas de color.
- **Principio:** D-944.4.
- **Gravedad:** baja.

### A3-19 — En Informes, «+15 / −0» se pinta igual que «sin cambios»

- **Vista y lugar:** Informes › columna «Hallazgos».
- **Combinación:** todas.
- **Qué pasa:** la columna que dice si una sesión encontró algo escribe «+15 / −0» y «sin cambios» con el mismo color y el mismo peso, así que la tabla no se puede recorrer buscando las sesiones que trajeron trabajo. En Métricas, el delta equivalente («▲ 2 vs periodo anterior») sí va en verde.
- **Qué debería pasar:** el signo que ya está escrito lleva su color —altas en la tinta de nuevo, bajas en la de resuelto— o al menos el «sin cambios» baja a tinta apagada.
- **Evidencia:** `vistas/dark-completa/06-informes.png`, columna «Hallazgos», filas 3, 7 y 8 frente al resto; comparar con `vistas/dark-completa/08-metricas.png`, tarjeta «Resueltos en el periodo».
- **Principio:** D-944.4.
- **Gravedad:** baja.

## Propuestas

### P-A3-01 — Una escala de gravedad con cuatro rellenos propios, y una prueba que la mide en la captura

- **Qué.** Añadir a la paleta `Sev.*.Soft` (cuatro rellenos) además de las cuatro tintas que ya hay, y hacer que `Pill.Sev` los use en vez de tomar prestados `Danger.Soft`/`Warning.Soft`/`Primary.Soft`. De paso, una regla de prueba que compruebe que los cuatro rellenos y las cuatro tintas son distintos entre sí en los dos temas, con distancia mínima, y que ninguna de ellas coincide con un color de otra familia.
- **Por qué.** Cierra de raíz A3-10 (crítica y alta con el mismo fondo) y A3-07 (baja = azul de primario), y convierte «la gravedad se ve sin leer» —que es una decisión, D-973— en algo que falla solo cuando se rompe. Hoy la única forma de descubrirlo es mirar una captura y contar manchas.
- **Coste.** `Palette.Dark/Light.xaml` (8 claves nuevas), `Styles.xaml` (`Pill.Sev`), y ninguna vista: todas piden la pastilla, no el color. Una regla nueva en `PaletteContrastTests`.

### P-A3-02 — Un barrido de «color que no sale de la paleta», medido sobre el píxel y no sobre el XAML

- **Qué.** El test de D-983 §8 mira el XAML convertido y prohíbe hexadecimales escritos a mano. Propongo el complementario: un test que renderice cada vista a PNG en los dos temas y compruebe que **el conjunto de colores distintos de cada captura está contenido en la paleta de ese tema** (más una lista blanca corta: capturas de código, logotipo, avatar). Los colores que se cuelan hoy —`#DDDDDD`, `#E07A2B`, `#D13A3A`, `#4A9EE0`, `#1E9BFA`, `#F44336`, `#E0A030`, `#383E47`— saldrían todos en la primera ejecución.
- **Por qué.** Es la única prueba que habría visto A3-02, A3-04, A3-05 y A3-06 sin que un humano abriera la aplicación: ninguno de esos colores está escrito en un XAML convertido —vienen de controles de la librería, de estilos antiguos o de opacidades—, así que el test actual pasa en verde con la ficha llena de botones blancos. El banco de capturas ya existe (D-977), o sea que la mitad del coste está pagada.
- **Coste.** Un test nuevo sobre el arnés de capturas; comparación de histograma, sin dependencias. La lista blanca hay que mantenerla, y ése es el precio honesto: cada excepción obliga a justificarse por escrito, que es exactamente lo que se quiere.

### P-A3-03 — El interruptor, el anillo y la barra dicen su estado con palabra, no solo con azul

- **Qué.** Además de resolver el azul de fábrica (A3-06), poner rótulo al lado de los tres controles que hoy solo hablan por color: «Activado/Desactivado» junto al interruptor, «Comprobando…» ya está en Cuenta pero no en la barra de estado, y el porcentaje al lado de la barra del ciclo (que hoy vive dos líneas más arriba).
- **Por qué.** D-989 fijó icono + COLOR + PALABRA para Cuenta y la regla es buena para toda la aplicación; los interruptores de Ajustes son el sitio donde más caro sale equivocarse —«Modo exhaustivo» multiplica el gasto por tres— y hoy la única señal de que está apagado es que el azul no está.
- **Coste.** `SettingsView` (tres filas), `PortfolioCard`, la barra de estado. Sin lógica.

### P-A3-04 — Que el estado de un hallazgo se pinte una vez, en un control, y se pueda ver la tabla entera

- **Qué.** Un `StatusPill` con los estados que existen —activo, por revisar, arreglado sin verificar, silenciado, caducado, falso positivo, resuelto— con su color, su forma y su palabra fijados en un solo `Style`, y una página de sistema (oculta, para desarrollo) que los pinte todos en fila en los dos temas.
- **Por qué.** Hoy «activo» es una pastilla azul en la ficha, una opción de un desplegable en Hallazgos y una palabra en negrita al final de una línea de metadatos en el diálogo de patrones silenciados. Nadie puede contestar «¿cómo se ve un hallazgo caducado?» sin fabricar el dato; con la página de muestras se contesta mirando. Es el mismo argumento que llevó a `EmptyState` (D-991): siete sitios que necesitan lo mismo divergen si no hay un control.
- **Coste.** Un control y un estilo en `Styles.xaml`; toca la ficha, la lista de Hallazgos, el diálogo de silenciados y el informe. La página de muestras es un XAML sin lógica y no entra en el instalador.

### P-A3-05 — Contraste medido sobre lo que se ve, no solo sobre los pares declarados

- **Qué.** `PaletteContrastTests` comprueba pares de recursos. Propongo añadir, sobre las mismas capturas del banco, una comprobación de contraste de **las zonas que llevan texto pequeño sobre color** (pastillas, botones macizos, avisos): recortar la caja, tomar el color dominante como fondo y el más alejado como tinta, y exigir 4,5:1.
- **Por qué.** Los cinco fallos de AA que he podido medir aquí —los cuatro contadores de Sesión en vivo, «Detener», «Activo», «General», «completa»— son todos de composición: el par declarado está bien y lo que se pinta encima no es el que se declaró. Un test de pares nunca los verá.
- **Coste.** Reutiliza el arnés de P-A3-02; hay que anotar en cada vista qué cajas se miden (o derivarlo del árbol visual, que es más trabajo y más fiable).

### P-A3-06 — Los diálogos, con la vista que los abre y no en una tanda aparte

- **Qué.** BACKLOG apunta diez diálogos pendientes «como trabajo acotado y mecánico». Propongo cambiar el orden: convertir primero los cinco que se abren desde una vista ya hecha (vincular clon, silenciados, borrar aplicación, restablecimiento, lanzar auditoría), porque son los cinco que un usuario ve el mismo día que ve la vista convertida.
- **Por qué.** Un diálogo blanco encima de una pantalla crema no se lee como «esto todavía no está hecho», se lee como «esto es de otro programa» —y los dos más blancos son justo los dos que piden confirmar un borrado, que es donde la confianza importa. El coste es el mismo se haga antes o después; lo que cambia es cuánto tiempo se ve.
- **Coste.** Cinco XAML, tipografía/espaciado/color por tokens, sin decisiones nuevas. Se lleva por delante A3-01 entero.

### P-A3-07 — «Auditar N seleccionadas» también cuando N es cero

- **Qué.** Que el primario de Inventario y el de Nueva aplicación se apaguen cuando no pueden hacer nada, con la razón escrita a su lado en tinta apagada: «Auditar selección — marca al menos una unidad», «Crear e inventariar — elige un repositorio». Y que Tarifas siga la misma regla que las otras cuatro secciones de Ajustes.
- **Por qué.** Unifica los cuatro criterios de A3-09 en uno, y aplica a las vistas de trabajo la regla que D-1000 ya escribió para Ajustes. Ahorra el clic que no hace nada y, en Inventario, ahorra además la duda de qué se va a gastar.
- **Coste.** Tres vistas, una propiedad `CanExecute` que ya existe en los tres view-models y una cadena de razón por botón. Sin lógica nueva.

### P-A3-08 — Un aviso sabe a qué alcanza, y se coloca por eso

- **Qué.** Dar al control de aviso dos tamaños declarados —**de pantalla** (ancho de la columna, icono, cabecera opcional, acción) y **de bloque** (dentro de una tarjeta, icono e hilo de texto)— y prohibir el tercero que existe hoy de facto, el ámbar sin icono.
- **Por qué.** D-997 §8 ya sacó una conclusión que vale como regla general: «lo que abarca un aviso decide dónde va». Convertirla en dos variantes con nombre cierra A3-16 y evita que el próximo aviso invente una cuarta forma. También da sitio natural al aviso del cierre del arreglo, que hoy es de pantalla pero se pinta como de bloque.
- **Coste.** `Styles.xaml` (una variante más) y los cinco sitios que ya usan `Notice.*`.
