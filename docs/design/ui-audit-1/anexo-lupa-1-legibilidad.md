## Hallazgos

### A1-01 — Los cinco diálogos siguen con los colores de fábrica: blanco frío sobre una aplicación crema

- **Vista y lugar:** los cinco diálogos (`d1`–`d5`), el fondo de la ventana entera y las cajas de texto.
- **Combinación:** los dos temas (los diálogos no cambian con el tamaño).
- **Qué pasa:** el fondo de los cinco diálogos es `#FAFAFA` en claro y `#202020` en oscuro —medidos en el píxel— mientras la aplicación es `#F2EBDD` / `#171B22`. Las cajas de texto de `d3` y `d4` son `#FFFFFF` puro. Dentro de `d2` conviven las dos paletas: la página es `#FAFAFA` y las tarjetas de patrón son `#F2EBDD`, o sea la tarjeta sale **más oscura que la página que la sostiene**, justo al revés que en toda la aplicación (fondo `#F2EBDD` < superficie `#FAF5EC`). Abrir un diálogo desde una pantalla crema enseña un rectángulo blanco y frío.
- **Qué debería pasar:** los diálogos se pintan con las mismas claves que las vistas: fondo `Brush.Bg`, superficies `Brush.Surface`, cajas de texto con la superficie del tema. Un diálogo no es otra aplicación.
- **Evidencia:** `dialogos/light-d3-eliminar-aplicacion.png` y `dialogos/light-d4-restablecimiento-de-fabrica.png` (el campo blanco del centro y todo el fondo); `dialogos/light-d2-patrones-silenciados.png` (las dos tarjetas P-1/P-2, crema, sobre página blanca); `dialogos/dark-d1-vincular-clon.png` (fondo gris plano `#202020` frente al `#171B22` azulado de `vistas/dark-completa/01-portafolio.png`).
- **Principio:** D-944.5, D-945, D-948. (D-996 deja los diálogos en la lista de pendientes; esto es lo que esa lista vale en pantalla.)
- **Gravedad:** alta

### A1-02 — En la ficha, tres acciones se pintan con el botón de fábrica: tres losas casi blancas que pesan más que el primario

- **Vista y lugar:** Hallazgos › ficha › bloque «Acciones», los botones «Generar prompt de arreglo», «Verificar ahora» y «Abrir en el editor».
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** los tres se pintan con relleno `#DDDDDD` y texto `#000000` **idénticos en los dos temas** (el mismo recuento de píxeles exacto en las dos capturas). En tema oscuro son tres barras casi blancas sobre `#1F242D` —11,5:1 de relleno contra la tarjeta—, de modo que atraen el ojo antes que «Arreglar con agente», que es el primario verde y la acción de la vista. El tema no las toca.
- **Qué debería pasar:** las tres son `Button.Secondary` del sistema, con relleno de superficie neutra y tinta de texto, y quedan por debajo del primario en peso visual, en los dos temas.
- **Evidencia:** `vistas/dark-completa/05-hallazgo-ficha.png`, columna derecha, bajo «Acciones» (tres barras blancas de y≈628 a y≈710); `vistas/light-completa/05-hallazgo-ficha.png`, mismo sitio, el mismo gris.
- **Principio:** D-944.4, D-945, D-949.
- **Gravedad:** alta

### A1-03 — La pastilla de estado «Activo» usa la misma tinta en los dos temas: 2,38:1 sobre el crema

- **Vista y lugar:** Hallazgos › ficha, la fila de pastillas bajo el título (`Alta` · `Confianza media` · `Activo`).
- **Combinación:** tema claro, los dos tamaños.
- **Qué pasa:** la tinta de «Activo» es `#4A9EE0` **en los dos temas**. Sobre la pastilla clara (`#E4EAEA`) da **2,38:1**; sobre la oscura (`#243343`) da 4,45:1. En claro el estado del hallazgo —el dato que dice si esto sigue vivo— se lee mucho peor que las dos pastillas que tiene al lado (`Alta` 6,8:1, `Confianza media` 6,17:1).
- **Qué debería pasar:** la pastilla de estado tiene su par `.Soft` / `.Ink` por tema, como la gravedad, y pasa AA en los dos.
- **Evidencia:** `vistas/light-completa/05-hallazgo-ficha.png`, arriba a la izquierda de la tarjeta de identidad; se ve al lado de `vistas/dark-completa/05-hallazgo-ficha.png`, donde la misma pastilla sí se lee. También en `vistas/light-1280/05-hallazgo-ficha.png`.
- **Principio:** D-944.5, D-945, D-947.
- **Gravedad:** alta

### A1-04 — A 1280 (150 %) las rutas de las unidades se recortan sin elipsis contra el galón, y mienten

- **Vista y lugar:** Sesión en vivo › columna central, las cabeceras de unidad.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** las rutas se cortan por el final **sin puntos suspensivos** y chocan con el galón de desplegar: se lee `…/Servicios/FormateadorInfc⌄`, `…/Servicios/RepositorioVolac⌄`, `…/Utilidades/Conversiones.c⌄`. No hay ninguna señal de que falte texto: `FormateadorInfc` parece un nombre de fichero, y `Conversiones.c` parece un fichero de C. Es un recorte que miente.
- **Qué debería pasar:** o la ruta se acorta por el medio conservando los extremos (como ya hace la cabecera del bloque de código tras D-983·5), o al menos lleva elipsis y deja aire antes del galón.
- **Evidencia:** `densas/dark-1280-05-sesion-en-vivo.png`, columna central, las tres filas plegadas de y≈466 a y≈562; `densas/light-1280-05-sesion-en-vivo.png` igual.
- **Principio:** D-944.1, D-944.8.
- **Gravedad:** alta

### A1-05 — A 1280 (150 %) la cabecera del arreglo se queda en «Age» y «atala…»

- **Vista y lugar:** Arreglo asistido › la tira de identidad de la cabecera (nombre de aplicación y pastilla de proveedor·modelo).
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la pastilla «Agente falso · claude-opus-4.7» se recorta **a mitad de palabra y sin elipsis** hasta dejar solo `Age`; el nombre de la aplicación queda en `atala…`. En la pantalla de cierre la misma pastilla enseña `Agente falso · clau`. El modelo con el que se está arreglando —el dato que decide cuánto cuesta y quién juzga— desaparece por completo, y lo que queda no se puede ni interpretar.
- **Qué debería pasar:** por debajo de un ancho, la tira baja a una segunda línea o pliega los datos secundarios; una pastilla que no cabe entera se retira, no se corta en tres letras.
- **Evidencia:** `densas/dark-1280-06-arreglo-asistido.png`, cabecera, entre «Volver al hallazgo» y «Pausar»; `densas/light-1280-07-arreglo-cierre.png`, mismo sitio.
- **Principio:** D-944.1, D-944.8.
- **Gravedad:** alta

### A1-06 — El aviso flotante se planta encima de la barra de cierre y tapa una casilla

- **Vista y lugar:** Arreglo asistido y Última sesión › la barra de resumen del pie; el aviso emergente de abajo a la derecha.
- **Combinación:** todas (1920 y 1280, los dos temas).
- **Qué pasa:** el aviso («Sesión completada: 13 nuevos…» / «Arreglo asistido de 01M1S…») se dibuja encima del pie de la vista y **tapa una casilla de verificación con su rótulo**: en la captura solo asoma el borde superior del cuadrito y la mitad de arriba de las letras. Mientras el aviso está en pantalla la casilla no se ve ni se sabe qué dice, y en 1280 se lleva por delante también «build/tests».
- **Qué debería pasar:** el aviso ocupa su propio carril (empuja el contenido o vive por encima de la barra), o al menos deja libre la franja de la barra de acciones. Un aviso informativo no puede taparse un control.
- **Evidencia:** `densas/dark-completa-06-arreglo-asistido.png`, esquina inferior derecha (x≈1660, y≈938: casilla mutilada por el globo); `densas/light-completa-06-arreglo-asistido.png` igual; `densas/dark-1280-06-arreglo-asistido.png` y `densas/light-1280-08-ultima-sesion.png`.
- **Principio:** D-944.8, D-944.4.
- **Gravedad:** alta

### A1-07 — La barra de desplazamiento del cajón del ciclo se pinta encima de los enlaces «Gestionar»

- **Vista y lugar:** Inventario › cajón «Resumen del ciclo» › bloque «Gobernanza», los enlaces «Gestionar».
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** el pulgar de la barra vertical se superpone al texto del enlace y lo cruza entre la `a` y la `r` de «Gestionar». El enlace se lee tachado y su zona de pulsación queda debajo de la barra.
- **Qué debería pasar:** el desplazamiento reserva su carril (o el contenido lleva el relleno derecho que lo evita); nunca se dibuja sobre texto pulsable.
- **Evidencia:** `vistas/dark-1280/03b-inventario-cajon.png`, panel derecho, la fila «Patrones silenciados: 0 · Gestionar» (y≈624); `vistas/light-1280/03b-inventario-cajon.png`, idéntico.
- **Principio:** D-944.1.
- **Gravedad:** alta

### A1-08 — La pastilla de temática «General» no llega a AA en ninguno de los dos temas

- **Vista y lugar:** Portafolio › tarjeta de aplicación (junto a «1,8 % auditado») e Inventario › «Resumen del ciclo» (junto a «Configurar ciclo»); también en el cajón a 1280.
- **Combinación:** todas.
- **Qué pasa:** la pastilla neutra usa `#DDDBD7` con tinta `#6B7280` en claro (**3,50:1**) y `#383E47` con `#9CA3AF` en oscuro (**4,25:1**). Los dos por debajo de 4,5. Es el único par de los que he medido que falla en los **dos** temas, así que no es un descuido del crema: es un par que no entró en la comprobación.
- **Qué debería pasar:** la pastilla neutra usa el par `.Soft`/`.Ink` del gris del sistema y pasa AA, como ya lo pasan `Alta`, `Media`, `Baja` y `Confianza media`.
- **Evidencia:** `vistas/light-completa/01-portafolio.png`, tarjeta XBLAST, la pastilla «General» a la derecha de «1,8 % auditado»; `vistas/dark-completa/03-inventario.png`, panel derecho, «General» junto a «Configurar ciclo».
- **Principio:** D-944.5, D-947, D-961.
- **Gravedad:** media

### A1-09 — Cuenta y Nueva aplicación empiezan en otra x que el resto de la aplicación

- **Vista y lugar:** Cuenta y Nueva aplicación, el bloque de página entero (título, subtítulo y tarjetas).
- **Combinación:** todas, los dos temas.
- **Qué pasa:** medido sobre la fila del título, todas las vistas arrancan en **x = 264–266** (Portafolio, Hallazgos, Informes, Métricas, Inventario, ficha, Ajustes) menos dos: **Nueva aplicación en x = 633** y **Cuenta en x = 800**. Al pasar de Portafolio a Cuenta el contenido salta 536 px a la derecha, y la miga de pan —que sigue en x = 311— se queda 489 px a la izquierda de su propio título. Además la tarjeta de Cuenta mide 570 px de ancho fijo dentro de un área de 1.690: usa el 34 % del lienzo y deja el resto en blanco a los dos lados.
- **Qué debería pasar:** un margen de página por aplicación. Si una vista se centra, se centran todas las de su clase; y «Acerca de» (tarjeta de 640 centrada, D-999·6) es la excepción declarada, no el tercer patrón.
- **Evidencia:** `vistas/dark-completa/09-cuenta.png` y `vistas/dark-completa/02-nueva-aplicacion.png` frente a `vistas/dark-completa/06-informes.png`; a 1280 el mismo salto en `vistas/dark-1280/09-cuenta.png`.
- **Principio:** D-944.2, D-944.8.
- **Gravedad:** media

### A1-10 — El cuerpo del informe va justificado y abre ríos de espacio en una medida de 700 px

- **Vista y lugar:** Informes › informe abierto › «Cobertura» y «Hallazgos nuevos».
- **Combinación:** todas, los dos temas.
- **Qué pasa:** los párrafos se justifican a los dos lados en una medida de ~700 px y sin partición de palabras (WPF no la tiene), así que las líneas largas quedan con huecos de tres espacios entre palabras: «El·␣␣parámetro·␣␣saveFileDialog·␣␣no·␣␣se·␣␣valida·␣␣contra·␣␣null». Es el único texto de la aplicación que se justifica; todo lo demás va en bandera.
- **Qué debería pasar:** el informe se lee en bandera a la izquierda, como el resto.
- **Evidencia:** `vistas/dark-completa/07b-informe-con-hallazgos.png`, bloque «Hallazgos nuevos» (y≈905–940) y la línea de «Cobertura» (y≈676); `vistas/light-completa/07b-informe-con-hallazgos.png` igual.
- **Principio:** D-944.3, D-993.
- **Gravedad:** media

### A1-11 — Dentro de la tarjeta del informe conviven dos anchos y un hueco de 310 px

- **Vista y lugar:** Informes › informe abierto, la tarjeta del documento.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** medido: la regla que va bajo «Informe de sesión — XBLAST» ocupa **290→990** (700 px) y la barra «Anexo técnico — diagnóstico», dentro de la misma tarjeta, ocupa **290→1881** (1591 px). Y como la barra va anclada al pie de la tarjeta, en una sesión corta el último renglón acaba en y≈650 y la barra aparece en y≈960: **310 px de nada** entre las dos. Es el mismo defecto del `DockPanel` que D-983 quitó de «Última sesión».
- **Qué debería pasar:** el anexo mide lo que mide la medida del texto y va justo debajo del último renglón; la tarjeta acaba donde acaba su contenido.
- **Evidencia:** `vistas/dark-completa/07-informe-abierto.png`, del final de «Revisados: …» a la barra del anexo; `vistas/light-completa/07-informe-abierto.png` igual.
- **Principio:** D-944.2, D-944.8, D-993.
- **Gravedad:** media

### A1-12 — La barra de acciones fija corta el texto por la mitad de la letra

- **Vista y lugar:** Ajustes › Avanzado (última ayuda), Arreglo asistido › cierre («Resultado del último build/tests») e Informes › informe abierto («Cobertura»).
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la barra pegada al pie de la columna (D-1000·1) recorta el contenido en seco: la línea que queda debajo se ve a media altura de glifo, sin elipsis, sin degradado y sin nada que diga que hay más. En el cierre del arreglo el resultado es peor: el titular «Resultado del último build/tests» queda a la vista y su única línea de contenido, tapada — un epígrafe sin cuerpo.
- **Qué debería pasar:** el corte cae entre líneas, no dentro de una, y lleva un degradado o una sombra que diga que el contenido sigue.
- **Evidencia:** `vistas/dark-1280/11e-ajustes-avanzado.png` (y≈620–632: «modelo antes de dar el turno por fallido. Aplica al», cortada a media letra); `densas/light-1280-07-arreglo-cierre.png` (titular en y=545, botones en y=584); `vistas/dark-1280/07-informe-abierto.png` (la línea de «Cobertura» seccionada por la barra del anexo).
- **Principio:** D-944.1, D-944.3.
- **Gravedad:** media

### A1-13 — «Descubrimiento» se recorta a «Descubrimient» en la tarjeta de coste de Métricas

- **Vista y lugar:** Métricas › azulejo «Coste del periodo», el desglose por fase.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** la etiqueta de la primera fila se corta a mitad de la `o` final, sin elipsis, y queda pegada sin ni un píxel de aire a la columna de valores («Descubrimient|5 ses. · 97 llam. …»). La segunda fila, «Arreglo», deja 60 px de hueco: las dos filas que existen para compararse no comparten ni el ancho de su columna.
- **Qué debería pasar:** la columna de etiquetas mide lo que mide la etiqueta más larga, con su aire, y las dos filas alinean.
- **Evidencia:** `vistas/dark-1280/08-metricas.png`, tercer azulejo, y≈512; `vistas/light-1280/08-metricas.png` idéntico.
- **Principio:** D-944.3, D-990.
- **Gravedad:** media

### A1-14 — Las pastillas de gravedad de Hallazgos se empaquetan a la derecha, así que la columna no significa nada

- **Vista y lugar:** Hallazgos › la lista, las pastillas de recuento por gravedad al final de cada fila.
- **Combinación:** todas, los dos temas.
- **Qué pasa:** medidas las x: las filas con tres pastillas las ponen en 1670/1747/1819; las que solo tienen una la ponen en **1819**, que es la ranura de «Baja». Recorriendo la columna de la derecha se lee «4 Baja, 1 Baja, 6 Baja, **3 Alta**, 1 Baja, 1 Baja, **3 Media**, 2 Baja, **1 Media**…». La misma gravedad cambia de columna según cuántas tenga la fila, y una pastilla roja aparece donde el ojo ya se ha acostumbrado a ver azul.
- **Qué debería pasar:** tres ranuras fijas —Alta, Media, Baja—, con hueco vacío cuando esa gravedad no tiene casos. Así la columna se puede recorrer.
- **Evidencia:** `vistas/dark-completa/04-hallazgos.png`, filas «RecoveryUtils.cs» (y=515) y «Program.cs» (y=677) frente a las tres de arriba; `vistas/light-completa/04-hallazgos.png` igual.
- **Principio:** D-944.8, D-973.
- **Gravedad:** media

### A1-15 — Azulejos de 393 px con 55 px de contenido, y vistas con más de la mitad del lienzo vacío

- **Vista y lugar:** Portafolio (la tira de gravedades y la rejilla), Última sesión, informe abierto y Ajustes.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** medido en Portafolio: los cuatro azulejos ocupan 266→659, 680→1073, 1094→1487 y 1508→1901 —**393 px cada uno**— y dentro de cada uno el contenido («0 / Críticas») mide ~55 px: el 86 % del azulejo está vacío. Debajo, la rejilla de aplicaciones usa 531 px de esos mismos 1.635 y deja el resto en blanco, con las **mismas cuatro cifras repetidas** 200 px más abajo dentro de la tarjeta. En Última sesión el contenido acaba en y=470 de 1.032 y en Ajustes › Proveedor y modelo acaba en y=545 y termina en x=1.290: en las dos, más de la mitad del lienzo está en blanco.
- **Qué debería pasar:** o los azulejos se encogen a su contenido y dejan que la rejilla de aplicaciones use el ancho, o la tira de gravedades se funde con la tarjeta que ya las enseña. Y una vista con tres renglones no se estira hasta 1.920: se reparte o se ciñe.
- **Evidencia:** `vistas/dark-completa/01-portafolio.png` (tira superior y hueco a la derecha de la tarjeta XBLAST); `densas/dark-completa-08-ultima-sesion.png` (todo por debajo de y=470); `vistas/dark-completa/11-ajustes.png`.
- **Principio:** D-944.2.
- **Gravedad:** media

### A1-16 — Los números de Informes no se alinean, y son los únicos de la aplicación que no lo hacen

- **Vista y lugar:** Informes › la tabla, columnas «Unidades», «Hallazgos» y «Coste».
- **Combinación:** todas, los dos temas.
- **Qué pasa:** las tres columnas numéricas van alineadas a la izquierda y sin dígitos tabulares, así que «1 unidad» y «12 unidades» empiezan en la misma x y sus cifras no comparten columna; lo mismo con «20,2 AI credits» y «64,6 AI credits» y con el «—» del coste desconocido. En Tarifas (D-997·4) y en el desglose por fase de Métricas los números ya van a la derecha y tabulares. La tabla que existe para comparar gasto es la única que no deja compararlo de un vistazo.
- **Qué debería pasar:** cifras a la derecha, con `NumeralAlignment="Tabular"`, como en Tarifas.
- **Evidencia:** `vistas/dark-completa/06-informes.png`, columnas de x≈1378 a x≈1800, filas de y=367 a y=703 (la fila de «12 unidades» frente a las de «1 unidad»).
- **Principio:** D-944.3, D-944.8, D-992.
- **Gravedad:** media

### A1-17 — En la ficha, la monoespaciada marca tres filas al azar y se lee como énfasis

- **Vista y lugar:** Hallazgos › ficha › tarjeta «Metadatos».
- **Combinación:** todas, los dos temas.
- **Qué pasa:** de las doce filas, tres valores van en monoespaciada y más brillantes (`Regla`, `Unidad`, `Commit anclado`) y nueve en la proporcional del sistema. El criterio no se sostiene: `Identificador: BUG-0008` es un identificador y va proporcional, mientras `Commit anclado: e34fc69` va monoespaciada. Como las tres monoespaciadas también pesan más, la tabla dice con la tipografía que esas tres filas son las importantes, y no lo son.
- **Qué debería pasar:** la monoespaciada marca una sola clase de dato y la marca siempre (rutas y hashes, por ejemplo), y no cambia además el peso: el énfasis de una tabla de metadatos lo pone el sitio, no la familia.
- **Evidencia:** `vistas/dark-completa/05-hallazgo-ficha.png`, tarjeta «Metadatos», filas «Identificador» (y=261) y «Commit anclado» (y=485); `vistas/light-completa/05-hallazgo-ficha.png` igual.
- **Principio:** D-944.3, D-976.
- **Gravedad:** media

### A1-18 — El borde superior de la conversación siega la primera línea por la mitad de la letra

- **Vista y lugar:** Arreglo asistido › columna de la conversación, el primer mensaje visible.
- **Combinación:** todas, los dos temas.
- **Qué pasa:** el panel de desplazamiento empieza pegado a la cabecera, sin relleno ni degradado, así que el mensaje de arriba aparece cortado a media altura de glifo: se lee «Atalaya · Ha leído src/AtalayaBanco.Core/Servicios/ClienteRemoto.cs» con la mitad superior de las letras ausente. No parece un texto que continúe hacia arriba: parece un texto roto.
- **Qué debería pasar:** relleno superior en el desplazamiento y, si acaso, un degradado de dos o tres píxeles que diga que hay más arriba.
- **Evidencia:** `densas/dark-completa-06-arreglo-asistido.png`, y≈195–210, primera fila de la columna central; `densas/light-completa-06-arreglo-asistido.png` igual.
- **Principio:** D-944.3.
- **Gravedad:** media

### A1-19 — «Zona peligrosa» acaba 41 px antes que el resto de bloques de Ajustes

- **Vista y lugar:** Ajustes › Avanzado, la tarjeta «Zona peligrosa».
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** medidos los bordes derechos de los bloques a ancho completo dentro del mismo armazón de Ajustes: el aviso de Apariencia acaba en **x = 1441**, el aviso y la tabla de Tarifas en **x = 1441**, y la tarjeta «Zona peligrosa» en **x = 1400**. Son 41 px de diferencia —y 41 no es múltiplo de 4— entre bloques del mismo nivel de la misma vista. No hay barra de desplazamiento que lo explique: en esa captura no la hay.
- **Qué debería pasar:** el tope lo pone la columna, no cada bloque; los cuatro acaban en la misma x.
- **Evidencia:** `vistas/dark-completa/11e-ajustes-avanzado.png` (borde derecho de la tarjeta roja) frente a `vistas/dark-completa/11d-ajustes-apariencia.png` (borde derecho del aviso azul).
- **Principio:** D-944.3, D-946, D-951.
- **Gravedad:** baja

### A1-20 — La fila «Stack» rompe el ritmo del formulario de alta

- **Vista y lugar:** Nueva aplicación › «El clon en esta máquina», fila «Stack».
- **Combinación:** todas, los dos temas.
- **Qué pasa:** en las tres filas de arriba el control arranca en x≈997 y el botón de apoyo («Recargar», «Examinar…», «Elegir…») se alinea a la derecha del bloque, terminando en x≈1513. En la fila «Stack» el valor («Unknown») arranca en x≈997 pero cae **22 px por debajo** de su rótulo —las otras filas alinean rótulo y control—, y «Detectar stack» se queda en x≈1100, sin compartir x ni con los botones de arriba ni con nada. Tres reglas distintas en cuatro filas.
- **Qué debería pasar:** una columna de rótulos, una de controles y una de botones de apoyo, y las cuatro filas dentro de ellas.
- **Evidencia:** `vistas/dark-completa/02-nueva-aplicacion.png`, filas «Carpeta» (y=557) y «Stack» (y=657–700).
- **Principio:** D-944.3, D-944.8, D-994.
- **Gravedad:** baja

### A1-21 — A 1280, las dos filas de la barra de filtros de Informes no comparten margen izquierdo

- **Vista y lugar:** Informes › la barra de filtros, cuando se parte en dos filas.
- **Combinación:** 1280×720 (= 150 %), los dos temas.
- **Qué pasa:** medida la primera tinta de cada fila dentro de la tarjeta: la primera fila empieza en **x = 281** (el borde de «Buscar…») y la segunda en **x = 274** (la `P` de «Periodo:»). Siete píxeles, que además no son múltiplo de 4. En Hallazgos, con la misma barra partida, las dos filas empiezan las dos en x = 285.
- **Qué debería pasar:** el ritmo lo pone la barra, como quedó en D-984, y vale igual cuando envuelve.
- **Evidencia:** `vistas/dark-1280/06-informes.png`, tarjeta de filtros (y≈238–322), frente a `vistas/dark-1280/04-hallazgos.png`.
- **Principio:** D-944.3, D-984.
- **Gravedad:** baja

### A1-22 — «8 informes» vive a 1.600 px del título que cuenta

- **Vista y lugar:** Informes › cabecera.
- **Combinación:** 1920×1080, los dos temas.
- **Qué pasa:** el recuento se pinta en el extremo derecho (x≈1832–1905) a la altura del subtítulo, mientras el título está en x=264. En Hallazgos el mismo dato («111 hallazgos») va bajo el título, y en Portafolio también («1 aplicación · 111 hallazgos abiertos»). Tres listas hermanas, dos sitios para el mismo dato; y el que está solo es el que hay que ir a buscar al otro extremo de la pantalla.
- **Qué debería pasar:** el recuento de una lista va donde va en las otras dos: en el subtítulo, bajo el título.
- **Evidencia:** `vistas/dark-completa/06-informes.png`, esquina superior derecha, frente a `vistas/dark-completa/04-hallazgos.png`.
- **Principio:** D-944.8.
- **Gravedad:** baja

### A1-23 — Dos barras de estado seguidas dicen lo mismo con dos notaciones

- **Vista y lugar:** Sesión en vivo › la barra de resumen y, justo debajo, la tira de estado de la carcasa.
- **Combinación:** todas, los dos temas.
- **Qué pasa:** a 44 px de distancia se lee «**Unidad 3 de 6** · 00:07 · 7 llamadas · coste no calculable…» y «Auditando atalayabanco · **unidad 3/6** · pasada 2». El mismo dato, dos veces y escrito de dos maneras. La repetición gasta el único renglón que queda para lo que sí cambia.
- **Qué debería pasar:** el progreso se dice una vez, en la barra de la vista, y la tira de la carcasa dice lo que la vista no puede decir (qué aplicación, si el hub responde).
- **Evidencia:** `densas/dark-completa-05-sesion-en-vivo.png`, y=948 y y=1010.
- **Principio:** D-944.8.
- **Gravedad:** baja

### A1-24 — «Temática» es pastilla en dos vistas y texto corrido en otras dos

- **Vista y lugar:** Portafolio (tarjeta) e Inventario (resumen del ciclo) frente a Hallazgos › ficha (metadatos) e informe abierto.
- **Combinación:** todas, los dos temas.
- **Qué pasa:** el mismo valor —«General»— se pinta como pastilla teñida en Portafolio y en el resumen del ciclo, y como texto plano en la fila «Temática» de la ficha y dentro de la línea «Ciclo: 1 · Temática: General · Modo: Lotes» del informe. Quien busca la temática la busca en dos formas distintas según por dónde entre.
- **Qué debería pasar:** un dato, un tratamiento. Si la temática es una etiqueta, es pastilla en todas partes; si es un metadato, no es pastilla en ninguna.
- **Evidencia:** `vistas/dark-completa/01-portafolio.png` (pastilla «General» tras «1,8 % auditado») frente a `vistas/dark-completa/05-hallazgo-ficha.png` (fila «Temática · General», y=361) y `vistas/dark-completa/07-informe-abierto.png` (y=407).
- **Principio:** D-944.8, D-989.
- **Gravedad:** baja

---

## Propuestas

### P-A1-01 — Una regla de recorte para toda la aplicación, y que se pueda comprobar

- **Qué.** Tres primitivas de texto y nada más: `Text.Path` (acorta por el MEDIO, conserva los extremos), `Text.Name` (elipsis al final) y `Text.Chip` (no se recorta: si no cabe, no se pinta). Todo lo que hoy se recorta a pelo pasa a una de las tres, y un test recorre los XAML buscando texto con `TextTrimming="CharacterEllipsis"` o con ancho tope sin una de ellas.
- **Por qué.** A1-04 y A1-05 son el mismo defecto en dos sitios, y D-983·5 ya lo arregló una vez en la cabecera del bloque de código: el arreglo no se generalizó y volvió a aparecer a 150 %. Un nombre recortado que miente es el fallo más caro de esta interfaz, porque no se nota. Vale para las 8 vistas que enseñan rutas.
- **Coste.** Un control adjunto o tres estilos en `Tokens.xaml` y un barrido por los XAML que hoy recortan (sesión, arreglo, ficha, inventario, informes). No arrastra lógica.

### P-A1-02 — Un carril de avisos, en vez de un globo que aterriza donde cae

- **Qué.** Los avisos emergentes dejan de flotar sobre el contenido y pasan a una fila propia entre la cabecera y el cuerpo (como la tira de versión nueva de D-957), con su cierre. Cuando hay dos, se apilan; cuando no hay ninguno, la fila mide cero.
- **Por qué.** Arregla A1-06 de raíz y de paso deja de haber dos gramáticas de aviso (la tira fina de la carcasa y el globo de la esquina). Un aviso que hay que apartar para poder pulsar debajo es un aviso que estorba dos veces.
- **Coste.** La carcasa (`MainWindow`) y el servicio que hoy levanta los globos. Las vistas no se tocan.

### P-A1-03 — Portafolio: fundir la tira de gravedades con la rejilla

- **Qué.** Quitar los cuatro azulejos de arriba y llevar el total a la línea de subtítulo, que ya dice «1 aplicación · 111 hallazgos abiertos»; que la rejilla de tarjetas ocupe el ancho desde el primer renglón, con las tarjetas repartidas por `ColumnsPanel`.

```
Portafolio                                     [+ Nueva aplicación]
1 aplicación · 0 críticas · 27 altas · 64 medias · 20 bajas
┌─ XBLAST ────────┐ ┌─ (hueco) ───────┐ ┌─ (hueco) ───────┐
```

- **Por qué.** Hoy (A1-15) los azulejos gastan 1.635 px para enseñar cuatro números que la tarjeta de abajo vuelve a enseñar 200 px después, y la rejilla se queda con el 32 % del ancho. Con una sola aplicación la pantalla es casi toda vacío; con quince, los azulejos empujarían la rejilla fuera del primer pliegue.
- **Coste.** Solo `PortfolioView`. Ninguna lógica: los cuatro recuentos ya están en el view-model.

### P-A1-04 — Métricas y Portafolio comparten el mismo azulejo

- **Qué.** Un `StatTile` único —rótulo arriba a 13, cifra a 30, una línea secundaria opcional— y que Portafolio, Métricas y el resumen del ciclo lo usen. Hoy hay tres dibujos parecidos y ninguno igual (Portafolio pone la cifra arriba y el rótulo debajo; Métricas al revés).
- **Por qué.** Es lo que hace que las tres pantallas de cifras se lean como la misma herramienta, y quita de en medio la discusión de si un azulejo se estira o no: la decide el panel, una vez.
- **Coste.** Un control nuevo en `Controls/`, tres vistas tocadas. Sin lógica.

### P-A1-05 — Que el informe se lea como un documento: medida fija, columna centrada, anexo debajo

- **Qué.** La tarjeta del informe se ciñe a la medida (≈760 px) y se centra dentro de su área; el anexo va inmediatamente después del último renglón; el texto en bandera (A1-10). Y una barra de progreso de lectura no hace falta, pero sí que «Descargar .md» y «Ver hallazgos de esta sesión» se queden pegados arriba al desplazar.
- **Por qué.** Ahora mismo (A1-11) la tarjeta mide 1.640 px y el texto 700: el ojo tiene que decidir dónde acaba el documento. Centrar la columna quita esa pregunta y, de paso, quita el hueco de 310 px.
- **Coste.** `ReportView`. Ninguna lógica.

### P-A1-06 — Cifras alineadas en todas las tablas, por defecto y no por vista

- **Qué.** Un estilo `Cell.Number` (derecha + `NumeralAlignment="Tabular"`) y que lo usen Informes, Tarifas, Métricas y los recuentos del inventario. Quien escriba una columna de números no tiene que acordarse.
- **Por qué.** A1-16. La alineación de cifras es la diferencia entre poder recorrer una columna de costes y tener que leerla número a número; hoy se resolvió en Tarifas y se quedó ahí.
- **Coste.** Un estilo y tres vistas.

### P-A1-07 — Que Hallazgos enseñe la gravedad como una barra, no como tres pastillas

- **Qué.** Sustituir las tres pastillas de recuento por una barra apilada de ancho fijo (rojo/ámbar/azul en proporción) con el total a su derecha, y el desglose exacto en el tooltip y al desplegar la fila.

```
HttpService.cs   XBLAST · XBLASTRecovery/Class/HttpService.cs   ███▊▍  29
RecoveryUtils.cs XBLAST · XBLASTRecovery/Class/RecoveryUtils.cs ███     3
```

- **Por qué.** Resuelve A1-14 sin gastar tres ranuras por fila, y contesta mejor la pregunta que se hace en esta lista —«¿cuál está peor?»— porque las barras se comparan de un vistazo y tres números no. **Roza D-944.4** (el color deja de ir acompañado de la palabra en la fila): lo compenso dejando la palabra en el total al desplegar y en el tooltip; si eso no basta, la barra puede llevar el recuento de la gravedad dominante escrito al lado.
- **Coste.** `FindingsView` y un control de barra apilada. Los datos ya están.

### P-A1-08 — Un patrón de página, escrito y comprobado

- **Qué.** Un `PageShell` con tres huecos —título, acciones de vista, cuerpo— que fije el margen (264), el tope de medida y dónde va el recuento. Cuenta, Nueva aplicación y Acerca de declaran «cuerpo centrado» como una propiedad del shell, no reinventando el margen.
- **Por qué.** A1-09 y A1-22 son la misma raíz: no hay un sitio donde esté escrito dónde empieza una página. Con el shell, la próxima vista nace alineada, y la excepción de «Acerca de» se declara en vez de imitarse mal.
- **Coste.** Un control y las once vistas reencajadas; es el cambio más caro de la lista y el único que evita que esto vuelva.

### P-A1-09 — Sacar los diálogos a páginas cuando el diálogo es una tarea

- **Qué.** «Vincular clon local» y «Patrones silenciados» dejan de ser ventanas y pasan a ser páginas con miga (como hizo «Acerca de» en D-997·6). Se quedan como diálogo solo los tres que interrumpen para confirmar (`d3`, `d4`, `d5`), que es lo que un diálogo hace bien.
- **Por qué.** Además de arreglar A1-01 por la vía de borrar el problema, «Patrones silenciados» es una lista con búsqueda, edición y caducidades metida en 760×580 con dos barras de desplazamiento: es una pantalla, no un aviso. Y vincular un clon es una tarea con validación, que es exactamente lo que Nueva aplicación ya hace en página.
- **Coste.** Dos vistas nuevas, dos diálogos borrados, dos entradas de navegación. Arrastra el registro de contenedor de cada diálogo, como ya pasó con `AboutDialog` y con el de tarifas.

### P-A1-10 — Un modo «lista compacta» para Inventario y Hallazgos

- **Qué.** Un conmutador de densidad (cómoda / compacta) que baje la altura de fila de 54 a 36 px y suba de 13 a 20 las filas visibles a 1280.
- **Por qué.** El inventario real tiene 923 unidades y a 1280 se ven diez. La densidad no es un gusto en una herramienta que se mira horas: es cuántas veces hay que desplazar para encontrar algo. No contradice D-944.3 —los tamaños de letra no cambian, cambia el relleno de fila, que sigue siendo múltiplo de 4.
- **Coste.** Dos vistas, un ajuste en Apariencia y un token de alto de fila. Sin lógica.
