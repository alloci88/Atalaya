# MANUAL — Atalaya

Manual de uso. Qué hace cada pantalla, en qué orden se usan y qué significa lo que
enseñan. Para montar el entorno o compilar, ver `README.md`; para saber **por qué**
algo está hecho como está, `DECISIONS.md`.

> Atalaya no tiene servidor. El «backend» es un repositorio git compartido —el
> **hub**— que la aplicación clona en tu máquina y sincroniza sola. Todo lo que
> ves sale de ficheros; nada se calcula en otro sitio.

---

## Empezar

1. **Instala.** Descarga el zip de la última versión desde la
   [página de Releases](https://github.com/Applied-Advanced-Solutions-AAS/Atalaya/releases),
   descomprímelo donde quieras y ejecuta **`Atalaya.exe`**. No hay instalador ni permisos de
   administrador: es una carpeta. Y no hace falta tener .NET instalado — el paquete lo lleva
   dentro.

   > **Mejor fuera de OneDrive** (o de Dropbox, o de Google Drive). «Donde quieras» sigue siendo
   > verdad, pero el Escritorio y Documentos de un equipo corporativo suelen estar redirigidos a
   > OneDrive, y ahí la carpeta de Atalaya —medio giga— se sube entera **en cada actualización**,
   > y el cliente de sincronización retiene ficheros mientras copia, que es lo que puede hacer
   > fallar la sustitución. Un `C:\Apps\Atalaya` va mejor. **Mover la carpeta no pierde nada**:
   > tus ajustes, tu cuenta, los clones y el hub viven en `%LOCALAPPDATA%\Atalaya`, no ahí dentro.
   > Si ya la tienes en OneDrive, tampoco pasa nada: Atalaya te avisa y la mayoría de los días
   > funciona igual.

   > **La primera vez, Windows avisa.** «Windows protegió su PC»: el ejecutable no está firmado
   > todavía. Pulsa **Más información** → **Ejecutar de todas formas**. Solo pasa la primera vez.

   Para **actualizar** no hace falta nada de esto: cuando salga una versión nueva, Atalaya te
   avisa y se actualiza sola con un botón (ver «Actualizar Atalaya», más abajo). El reemplazo a
   mano sigue funcionando, y es la salida cuando el botón no puede.

2. **Cuenta.** La primera vez aterrizas aquí. Pulsa **Conectar con GitHub**, escribe
   el código que te muestra en `github.com/login/device` y autoriza. Ese login sirve
   para las tres cosas: acceso git al hub, autenticación de Copilot y autoría de los
   commits. No hay PAT que pegar ni URL que escribir.
3. **Nueva aplicación.** Elige el repositorio en el desplegable —son los de tu
   organización— y señala dónde lo tienes clonado. El nombre lo pone él. El asistente
   escanea el clon, arma el inventario y, si encuentra un baseline v4 (`CodeAudit/`),
   te ofrece importarlo.
4. **Inventario → Auditar selección.** Elige unidades y lanza. El progreso se sigue
   en **Sesión en vivo**.

---

## Moverse por Atalaya

La ventana tiene tres piezas fijas, y las tres están siempre en el mismo sitio.

**El raíl**, a la izquierda. Sus entradas van agrupadas por lo que son:

- **Trabajo** — Portafolio, Hallazgos, Informes, Métricas. Lo que se hace a diario, esté donde
  esté puesta la aplicación.
- **La aplicación en la que estás** — el grupo lleva su nombre (`XBLAST`) y dentro va su
  **Inventario**, más **Sesión en vivo** y **Arreglo asistido** cuando los hay. Este grupo aparece
  en cuanto entras en algo de una aplicación y te sigue mientras estés dentro: el inventario está
  **a un clic desde cualquier página**, sin pasar por Portafolio.
- **Sistema** — Cuenta, Ajustes y **Acerca de**. Tu cuenta también está abajo del todo, con tu avatar — y en la
  esquina del avatar, **el piloto del hub**.

La entrada donde estás va resaltada, con una barra de color a su izquierda. Un punto que late
sustituye al icono cuando algo está corriendo — una auditoría o un arreglo.

Todos los iconos del raíl van en **una sola columna** —el botón de las tres rayas, cada entrada y
tu avatar— y todos los textos empiezan en la misma vertical, incluidos los rótulos de los grupos.
Plegado o desplegado, los iconos no se mueven de sitio: lo único que desaparece es el texto.

Si estrechas mucho la ventana, el raíl **se pliega a solo iconos** para devolverle el sitio al
contenido. El botón de arriba lo pliega y lo despliega a mano; a partir de que lo toques manda lo
que tú digas, y **Atalaya lo recuerda** para la próxima vez. Plegado, cada icono lleva su nombre en
el tooltip.

> **«Nueva aplicación» ya no está en el raíl.** Es una acción, no un sitio: se da de alta desde el
> botón **+ Nueva aplicación** de Portafolio, que es donde siempre estuvo el mismo botón.

**La miga de pan**, arriba. Dice dónde estás y te devuelve por donde viniste:
`Portafolio › XBLAST › Inventario`. El nombre de la aplicación lleva a su inventario, que es la
portada de una aplicación en Atalaya. La flecha de su izquierda **deshace un paso**, como en un
navegador: la página anterior vuelve **tal cual la dejaste**, con su filtro y su selección puestos.
Y no dice nada más: esa barra tiene un solo trabajo.

Esa misma regla vale para el raíl: pulsar **Hallazgos** te devuelve *tus* hallazgos, con el filtro
que tenían, no una lista virgen de todo el portafolio.

**El pie**, abajo, aparece **solo cuando hay algo corriendo** y dice qué: la sesión y el arreglo,
con su progreso y un clic para ir a verlos.

### El piloto del hub

El puntito de color **en la esquina de tu avatar**, abajo del todo del raíl. **Verde**: conectado a
GitHub y al hub, todo publicado. **Ámbar**: sin conexión con el hub, o con cambios tuyos sin
publicar — puedes seguir trabajando, y lo que escribas se publica en cuanto vuelva la conexión.
**Rojo**: la última sincronización falló, y lo que escribas se queda en esta máquina hasta que se
arregle.

Pasa el ratón por el avatar y lo dice con palabras, con la hora de la última sincronización y —en
rojo— el error concreto.

### La ventana

Atalaya **arranca maximizada la primera vez**. A partir de ahí recuerda cómo la dejaste —tamaño,
posición y si estaba maximizada— y vuelve a abrirse así. Si la habías dejado en un monitor que ya
no está conectado, conserva el tamaño y se centra en la pantalla que haya.

Funciona a partir de 1100 × 700, y está diseñada para 1920 × 1080. A anchos pequeños las cosas se
**reorganizan** —las columnas se apilan, los paneles se pliegan, el raíl se encoge— en vez de
apretarse.

### Tema claro y tema oscuro

En **Ajustes → Apariencia → Tema claro**. El cambio se aplica al guardar, sin reiniciar, y cambia la aplicación
entera: fondos, textos, botones, pastillas de gravedad y avisos. El modo claro es **crema**, no
blanco: un blanco puro a pantalla completa cansa la vista, y esto es una herramienta de mirar
código durante horas.

El texto de trabajo va a 15 px, el secundario a 14 y los metadatos —rutas, horas, contadores— a
13, que es el tamaño más pequeño que Atalaya usa para nada.

Los colores significan siempre lo mismo, en los dos temas: **azul** la acción principal (hay una
sola por pantalla), **verde** lo que adelanta trabajo y lo que ha ido bien, **ámbar** lo que hay
que atender, **rojo** lo que destruye o lo que es crítico. Y cuando un botón está apagado por una
razón, **la razón se lee al lado**, en ámbar — no hace falta pasar el ratón por encima para
enterarse.

---

## Las vistas

### Portafolio

La portada: una tarjeta por aplicación con su progreso del ciclo, sus hallazgos
activos por severidad y el estado de su clon local. Un clic entra al inventario.

El piloto de vinculación dice si el clon de esa app está donde debería: verde
vinculado, ámbar con avisos, rojo sin clon. Sin clon no se puede auditar ni medir.

Junto al número de ciclo va un **distintivo con su temática** —«General», «Seguridad»,
«Rendimiento»…—: la lupa con la que se está auditando ese ciclo. Qué significa está en
**[Ciclos temáticos](#ciclos-temáticos)**.

Cada tarjeta dice además **cuántas clases han cambiado desde que se auditaron**
—«12 clases cambiadas desde su auditoría»—, y es un enlace: lleva al inventario
con el filtro puesto. Es lo que convierte esto en un hábito: la deuda nueva que
puede haber entrado sale sola cada mañana, sin que nadie la busque. Sin clon en
esta máquina pone «vincula tu clon para ver la deriva» y no un cero: la deriva se
calcula del historial local, y no tenerlo no es lo mismo que no haber cambiado
nada. Ver «Auditar lo que ha cambiado», más abajo.

### Inventario

Las unidades de la aplicación en el ciclo vigente, por módulos, con su estado
—**pendiente**, **auditada** o **grande**— y el panel lateral del ciclo.

Con la ventana estrecha ese panel no cabe al lado y se pliega: aparece el botón **Resumen del
ciclo** en la cabecera, y abre lo mismo como un cajón sobre la lista. Todo lo que hay dentro
—incluidos «Configurar ciclo» y los «Gestionar» de la gobernanza— sigue estando a un clic.

- **Grande** significa que la unidad supera el umbral de tamaño (LOC o caracteres):
  queda excluida del ciclo y genera su propio hallazgo.
- **Auditar selección** lanza una sesión sobre lo marcado. Está en la **barra de la lista**, junto a los dos botones que seleccionan, y dice sobre cuántas unidades va a actuar («Auditar 3 seleccionadas»): la acción va donde está lo que actúa.
- **Seleccionar cambiadas** marca las unidades cuyo código ha cambiado desde que
  se auditaron. Es el gesto de cada sprint; a partir de ahí, el flujo es el de
  siempre. Al lado, el **filtro de deriva** recorta la lista por lo que le ha
  pasado al código, que es una dimensión aparte del estado de auditoría.
- **Re-escanear** vuelve a medir el clon: actualiza el inventario **y** los hallazgos
  medidos en el mismo gesto, y cuenta en un aviso qué cambió.
- **Reiniciar ciclo** abre uno nuevo con todo pendiente, sin borrar nada. Es
  distinto del cierre normal, que **siembra** el ciclo siguiente con la deriva
  del que termina (ver «Cambiar de ciclo», más abajo). Antes de reiniciar te
  pregunta con qué **temática** y qué **modelo preferido** quieres el ciclo nuevo;
  cancelar el diálogo cancela el reinicio.
- El panel del ciclo lleva la **temática** del ciclo como distintivo, el **modelo
  preferido** del equipo para él, y **Configurar ciclo**, que abre el diálogo para
  cambiar cualquiera de las dos cosas (ver «Ciclos temáticos», más abajo).
- El panel del ciclo lleva **Patrones silenciados**, **Directivas** y **Umbrales**,
  cada uno con su «Gestionar» al lado: las tres cosas que condicionan qué se
  reporta en esta aplicación (ver «Directivas del proyecto», más abajo).
- **Umbrales · Gestionar** fija a partir de cuántas líneas —o de cuántos
  caracteres— una unidad es demasiado grande para auditarla de una vez. Es
  **política de la aplicación**, no una preferencia tuya: vive en el hub, vale
  para todo el equipo y el historial de git dice quién la cambió y cuándo. La
  pantalla cuenta, antes de guardar, cuántas unidades pasarían a ser grandes o
  dejarían de serlo. **Aplica al re-escanear**: guardar no reclasifica nada por
  sí solo, y cualquier máquina que tenga el cambio clasificará igual.
- Y las líneas de **deriva**: cambiadas, arregladas sin verificar y sin historial,
  cada una por su lado. No se suman nunca: piden acciones distintas.

Cada unidad lleva **dos** indicadores, no uno: su estado de auditoría
(pendiente / auditada / grande) y, si la tiene, su **deriva**. Son ortogonales —
una clase puede estar «Auditada» y «Cambiada» a la vez, y eso es justamente lo
que hay que saber para decidir.

### Hallazgos

La lista de todo lo detectado, agrupada por fichero. **La lista encuentra; la ficha
actúa**: aquí no se silencia, ni se asigna, ni se resuelve.

Filtros: búsqueda de texto, aplicación, severidad, estado (activos / resueltos /
silenciados / todos), **temática** (la lupa del ciclo que lo detectó) y dos
interruptores — **Por revisar** y **Disputados**. Todos los combos arrancan en
«Todas/Todos» y **Limpiar filtros** los devuelve ahí.

- **Por revisar**: la última comprobación no pudo confirmarlo, o su ubicación en el
  código se perdió. Pide una decisión humana.
- **Disputados** ⚖: un auditor sostiene que eso nunca fue un defecto. No cierra el
  hallazgo; lo marca. Si discrepan varios modelos, la señal es fuerte.

### Ficha de hallazgo

El detalle y **todas** las acciones: fragmento de código anclado, historial completo,
comentarios, y las decisiones de gobernanza — silenciar (con motivo y caducidad),
asignar, cambiar severidad, resolver a mano con justificación, cerrar una disputa y
**generar prompt de arreglo** al portapapeles.

- **Arreglar con agente** abre una sesión en la que el agente arregla el hallazgo
  **sobre tu clon local**, explicándote lo que hace y preguntándote en las
  decisiones. Ver *Arreglar con agente*, más abajo.
- El **prompt de arreglo** —el camino de siempre, intacto— viaja con **quién usa ese
  código**: los llamadores directos que hay en el clon local —ruta, línea, método que
  llama y la línea de la llamada—, y las
  reglas para que el agente no rompa el contrato que esos llamadores esperan. Si no hay
  clon, o si el símbolo no se puede buscar, el prompt sale igual diciendo que va sin la
  lista: nunca deja entender que un método no se usa cuando lo que pasa es que no se ha
  podido mirar. Una vez generado, los metadatos enseñan **Usado desde: N sitios**.
- **Verificar ahora** vuelve a preguntar al auditor si el defecto sigue ahí. Lo que se le
  enseña es el **método completo** que contiene el punto del hallazgo, no la línea suelta:
  con una línea sola no se puede juzgar nada que no quepa en esa línea. Si el método no se
  puede resolver, van las líneas de alrededor.
- Un arreglo se confirma **juzgando el código que hay ahora**, no buscando el código
  viejo: que el fragmento auditado haya desaparecido es lo que pasa cuando algo se
  arregla, así que si el método sigue ahí se le enseña al auditor tal y como está hoy.
  Solo se dice «no localizado» cuando no queda nada que juzgar — ni el fragmento, ni el
  método, ni una unidad que haya cambiado.
- Si el auditor **mira el código y no puede decidir**, el resultado es **«No concluyente»**,
  no una confirmación: una no-respuesta no es evidencia de nada, así que no sube «Veces
  confirmado» ni la confianza. Se anota con su causa y con el paso siguiente — ampliar el
  contexto, o re-auditar la unidad—, y el hallazgo queda marcado **Por revisar**.
- **Un arreglo que borra el ancla Y el símbolo tampoco es un callejón.** Si un arreglo
  reestructura la clase y hace desaparecer el campo o el método que el hallazgo nombraba, se
  te enseña **la unidad entera** y se pide un veredicto sobre ella. La cadena es método →
  margen → unidad si cambió, y funciona **también antes de commitear**: es el estado en el que
  Atalaya deja tu clon al terminar un arreglo.
- Resolver sigue exigiendo **evidencia de cambio**: si la unidad es la misma que la
  última vez que se vio el hallazgo —el mismo fichero, byte a byte—, un «arreglado» se degrada
  a «presente».
- **Verificar deja constancia siempre**: el historial gana un evento con el desenlace, con qué
  casa y qué modelo se juzgó, y con un enlace al **informe de esa verificación**. También
  cuando el desenlace es frustrante: un «no concluyente» es justo el que más cuesta reconstruir
  meses después.
- El aviso dice siempre **qué ha pasado** — el veredicto, o la causa concreta si no se
  pudo verificar.
- La franja de encima del código **depende del estado**: en los activos avisa en ámbar
  de la deriva sin verificar y trae la acción; en los resueltos dice que el arreglo está;
  en los silenciados no dice nada. Y si el código lo cambió **un arreglo de Atalaya** sobre
  este hallazgo, lo dice con esas palabras —«este código lo cambió el arreglo de Atalaya el
  {fecha}; pendiente de verificar»— en vez de extrañarse de su propio trabajo.
- Un hallazgo **silenciado dice por qué lo está**: quién, cuándo, con qué motivo y con qué
  notas, o el patrón que lo tapa con su frase y quién lo puso. Y dice cómo deshacerlo, ahí
  mismo. Si lo tapa un patrón, el camino no es «Des-silenciar» —el patrón seguiría puesto y
  la siguiente auditoría volvería a callarlo— sino **gestionar el patrón**: al retirarlo,
  este hallazgo vuelve a activo **al instante y sin re-auditar nada**. Lo que se silenció a
  mano, en cambio, es una decisión sobre ese caso y se conserva pase lo que pase con los
  patrones.
- En los hallazgos de tamaño el botón dice **Medir ahora**: los cuenta la aplicación
  leyendo el fichero, sin consultar al modelo y sin gastar tokens.

### Sesión en vivo · Última sesión

Aparece en el menú solo cuando hay una sesión (en curso o recién terminada), y el
punto late mientras corre. Enseña el progreso unidad a unidad, los hallazgos según
van llegando, el coste y los tokens consumidos, y al terminar el resumen de cierre.

Cada unidad se audita en **pasadas**, y cada pasada dice lo que hizo: **nuevos ·
confirmados · disputados**. Una pasada que no aporta hallazgos nuevos se llama **seca**, y
eso no significa que no haya pasado nada — puede haber confirmado siete. La unidad se da
por barrida con **dos pasadas secas seguidas**: con un modelo no determinista, que una
pasada no vea nada nuevo no prueba que no quede nada. El tope de pasadas de Ajustes sigue
mandando por encima; si se agota antes, la unidad se marca **cobertura posiblemente
incompleta**, con todas las letras.

> **El tope es un presupuesto, y las dos secas salen de él.** Con el tope de fábrica —**6**—
> quedan cuatro pasadas que puedan aportar algo más las dos que cierran. Eran 5 desde el
> principio, cuando bastaba UNA seca para converger; al subir la condición a dos, las
> productivas bajaron a tres sin que nadie lo re-ajustara, y con un modelo minucioso el barrido
> se quedaba corto. Si tu tope era 5 y no lo habías tocado, Atalaya lo sube a 6 una vez y te lo
> dice; si habías elegido tu propio número, no se toca.

> Y una unidad barrida **no es** una unidad sin defectos: es una unidad de la que el
> auditor no saca más con este criterio.

El resumen de cierre agrupa sus hallazgos **por clase**, con el recuento por severidad al
lado, igual que la vista de Hallazgos. Cada línea se despliega de un clic.

Si la sesión falló, lo dice con el motivo y el atajo para arreglarlo. **Ver informe de
sesión** abre el informe en la vista **Informes**.

### Arreglo asistido

Aparece en el menú solo cuando hay un arreglo (en curso, terminado, o con cambios que
todavía puedes descartar), y el punto late mientras el agente escribe.

**Se arregla con el proveedor que tengas elegido**, sea Copilot o Claude Code. La pantalla es
**la misma con los dos** —las mismas herramientas, las mismas tarjetas de pregunta, el mismo
diff y el mismo cierre— y por eso dice, arriba, **cuál está trabajando**: «Claude Code · modelo
opus». También lo dice el botón de la ficha antes de empezar, y queda escrito en el informe del
arreglo. Quien revise un diff tiene derecho a saber quién lo escribió.

Dos paneles: la **conversación** —lo que el agente va explicando, y las preguntas
como tarjetas con sus opciones— y el **diff**, una pestaña por fichero tocado, que
compara con lo que había antes de empezar. Abajo: tiempo, coste, ficheros tocados y
el resultado del último build.

Arriba, la cabecera dice **qué hallazgo** se está arreglando —con su identificador entero, una
sola vez—, **en qué aplicación** y **con qué motor**, y a la derecha lo que puedes pulsar:
**Pausar**, **Detener**, **Cerrar** y, separada del resto porque deshace el trabajo, **Descartar
todo**. Si estrechas la ventana, la cabecera **baja a dos filas** en vez de apretarse. El ajuste
**Compilar solución completa** no está entre los botones: vive abajo, junto al resultado del
build, que es donde se ve lo que hace.

El pie **cuenta desde la primera llamada al modelo**, no al terminar, y en las dos casas dice lo
mismo y en el mismo orden: **llamadas, coste y tokens**. Con Copilot el coste son **AI credits**;
con Claude Code, que no factura a la organización, es **«coste: incluido en tu suscripción de
Claude»**. Los tokens van una sola vez, detrás, por tipo (entrada, salida y la caché leída y
escrita). Si la ventana no da para todo, el pie **no corta nada a media palabra**: primero los
tokens se quedan en su total («330.124 tokens») y luego se retiran, después el coste se abrevia
(«coste: suscripción»), y las llamadas se leen siempre. El texto entero está en el tooltip del
pie, y el desglose, en el informe.

- **Pausar** no congela al agente —eso no se puede prometer— sino lo que importa: no
  cae ni un cambio más en tu clon ni se compila nada hasta que continúes.
- **Descartar todo** devuelve cada fichero tocado a como estaba, byte a byte, con
  confirmación previa.
- **Detener** para al agente; lo que ya haya aplicado se queda.
- El campo de entrada de abajo está **siempre** disponible: escribe y pulsa Enter para
  dirigirle («no toques ese fichero», «prefiero TryParse»). Si está a mitad de un paso,
  el mensaje se le entrega al empezar el siguiente, y la aplicación te lo dice.
- **Los ficheros del hallazgo se editan directamente; cualquier otro te pide permiso**, uno a
  uno, con el fichero y el motivo del agente delante. Un «no» se le devuelve como decisión, no
  como error: replantea el arreglo sin ese fichero y no vuelve a pedirlo. **Esto lo gobierna
  Atalaya**, con los dos proveedores — el permiso no se delega en el del CLI ni en el del SDK.

Si cierras Atalaya con un arreglo en curso, se detiene ordenadamente y **los cambios
se quedan** en tu clon: la próxima vez que abras, esta pantalla te ofrece descartarlos.

### Métricas

El panel de mando, filtrable por **aplicación** y **periodo** — y los dos filtros
afectan a todo lo de abajo.

Cuatro cifras arriba: hallazgos activos por severidad, resueltos en el periodo (con su
delta), coste del periodo y cobertura del ciclo.

**Los porcentajes no redondean hacia una mentira.** Si hay una sola unidad auditada, la
cobertura nunca se enseña como 0 % —3 de 1.335 son «0,2 %», no «0 %»—, y si queda una sola
sin auditar nunca se enseña como 100 %: eso se lee como «aquí ya no hay nada que mirar» y
cierra la pregunta. Cuando el número es tan pequeño (o tan grande) que ni un decimal lo
salva, se dice **«< 0,1 %»** o **«> 99,9 %»**. El 0 % y el 100 % exactos sí aparecen: son
verdad y significan algo. Los decimales solo salen cuando hacen falta — «42 %» se lee de un
vistazo y «42,0 %» no dice nada más.

El **coste del periodo** va en **AI credits** —la misma unidad que el panel de Copilot de tu
organización— e incluye **todas** las sesiones de Copilot que gastaron: auditorías, arreglos
asistidos y verificaciones. Debajo tienes el equivalente en dólares (1 credit = 0,01 $). Si alguna
sesión usó un modelo sin tarifa, el azulejo lo dice —falta gasto por contar y no se disimula— y el
enlace **Ajustes → Tarifas** te lleva a arreglarlo.

**Esta cifra es la factura de tu organización, y solo eso.** Las sesiones de Claude Code no entran:
ese consumo va contra la suscripción de quien las lanzó y no se tarifa. Si las hubo, el azulejo lo
dice con cuántas fueron — no para que busques una tarifa que falta, sino para que sepas por qué el
coste no cubre toda la actividad que ves más abajo. Esas sesiones **sí** están en la sexta gráfica,
con su proveedor y sus tokens.

El «por unidad auditada» que va debajo divide solo lo que costó **auditar** entre las unidades
auditadas — un arreglo no audita ninguna unidad, así que repartir su gasto entre ellas daría un
número que no significa nada. Cómo se calcula todo esto está en **[El coste, dicho como
es](#el-coste-dicho-como-es)**.

Debajo, siete gráficas:

1. **Coste en el tiempo** — una línea por aplicación, con toggle *Acumulado*.
2. **Resoluciones en el tiempo** — cuántos hallazgos se dieron por resueltos en cada
   tramo, por aplicación, contando todas las vías (veredicto del auditor, resolución
   manual y medida). Misma forma y **mismo color por aplicación** que la de coste, con
   su propio toggle *Acumulado*.
3. **Cobertura por aplicación** — un rosco por app; un clic abre su inventario. Un tramo
   diminuto pero real se dibuja igualmente: un rosco con 3 de 1.335 no puede parecerse a uno
   vacío, que significa lo contrario.
4. **Severidad por aplicación** — un rosco por app con el reparto de su deuda **viva**
   (hallazgos activos a día de hoy: el periodo no la recorta). Un clic en un tramo abre
   Hallazgos con esa app y esa severidad; en el centro, esa app entera.
5. **Flujo de hallazgos** — lo que entra, lo que se cierra y cuántos quedan vivos.
6. **Ciclos y temáticas** — la secuencia de ciclos de cada aplicación: **no es una
   línea de tiempo**, es la lista de capítulos. Una fila por aplicación (en el orden
   del Portafolio, con el nombre en una columna fija) y, en cada fila, sus ciclos **en
   orden, uno tras otro, como bloques del mismo ancho**: un ciclo de tres horas y uno de
   tres semanas cuentan lo mismo como capítulo. Cada bloque dice «C2 · Seguridad» y,
   debajo, sus fechas («14 ago – 2 sept», o «2 sept» si empezó y acabó el mismo día,
   o «14 ago – en curso»); si el rótulo se lleva las dos líneas, las fechas caen —nunca
   el identificador ni la temática— y el tooltip las trae. El color es el de la temática
   (la misma paleta de siempre), y el ciclo abierto va el último, desvaneciéndose por la
   derecha. **Un ciclo que cambió de temática a mitad se pinta partido** dentro de su
   bloque, cada trozo con su color y en proporción a lo que duró cada lupa, con una
   muesca en el cambio y un tooltip propio; el rótulo lo resume («C1 · Rendimiento →
   Seguridad»). **Los huecos se cuentan, no se dibujan**: entre dos ciclos separados por
   una semana o más aparece un separador discreto con el dato —«3 semanas sin
   auditar»—, que informa más que un desierto de píxeles. Una aplicación sin ciclos
   conserva su fila, rotulada «sin ciclos en este periodo»; solo cuenta como ciclo lo
   que tiene apertura registrada o alguna sesión. El tooltip de cada bloque lleva el
   ciclo, la temática, las fechas, las unidades auditadas sobre las auditables al
   cierre, los hallazgos nuevos y resueltos y el coste en AI credits (solo lo
   facturable). **Un clic abre el informe de cierre**; el ciclo abierto abre el
   inventario. Con muchos ciclos la fila se desplaza dentro de su tarjeta, nunca la
   página, y la vista **arranca por el final**: las filas se alinean por su último
   capítulo, así que al abrir se ve el ciclo más reciente de todas; si retrocedes, se
   respeta tu posición. El filtro de periodo recorta por pertenencia: se enseñan los
   ciclos que lo solapan, y si alguno queda fuera la fila lo dice («2 ciclos anteriores
   fuera del periodo») en vez de fabricar un eje.
7. **Actividad de sesiones** — el registro del periodo, con el **tipo** de cada sesión
   (auditoría, arreglo asistido, verificación, cierre…), su proveedor, su coste y sus
   **tokens**. Las de Claude Code dicen «suscripción» donde las otras dicen credits, y sus
   tokens siguen ahí: es con lo que puedes comparar el peso de dos sesiones de cualquier casa.
   **Un clic en una línea abre su informe en la vista Informes.**

Los colores de la secuencia de ciclos son una **paleta propia de temáticas**, fija: gris sobrio para General
y cinco colores distinguibles para las demás, elegidos con un paso para cada tema y medidos
contra su fondo. No se parecen a los cuatro de severidad, que siguen reservados a chips y
roscos. La leyenda los nombra siempre.

**El eje temporal.** Llega **siempre hasta hoy**, aunque el último tramo esté a cero: un
eje que termina en el pasado afirma que desde entonces no ha pasado nada. Los tramos
salen del periodo elegido — por día en «4 semanas», por semana en «8» y «26», por mes
cuando «Todo» pasa del año — y un tramo semanal **se rotula por su último día**: si hoy
es 28 de agosto, el último dice «28 ago» y cubre del 22 al 28. Pasa el ratón por encima
y el tooltip escribe el tramo entero («22–28 ago»), para que no haya que adivinarlo. Las
fechas se guardan en UTC y se enseñan **en tu hora local**, y los tramos se cortan
también en tu hora: lo que hiciste a las 00:30 aparece en el día en que lo hiciste.

**Cuándo se recalcula.** Con cada sincronización con el hub y **al volver a la vista**.
Una sesión que acabas de terminar en esta máquina —una auditoría, un arreglo, una
verificación— aparece sin reiniciar la aplicación.

Donde no hay medida se escribe «—» y qué haría falta para que aparezca. Un cero con
formato sería una medida que nadie ha tomado.

### Auditar lo que ha cambiado

Un ciclo completo sobre una aplicación grande cuesta caro y se hace una vez. Lo
sostenible es **auditar lo que ha cambiado desde que se auditó**: barato,
frecuente, y coge las regresiones recién nacidas. Es la operación de cada sprint.

Cada unidad guarda el commit en el que se auditó, y tu clon tiene el historial:
con esas dos cosas Atalaya puede decir exactamente qué clases han cambiado desde
entonces. **No se guarda nada**: la deriva se calcula del historial local cada
vez que se mira. En el hub solo viven hechos —el commit de cada auditoría, la
huella de lo que dejó cada arreglo—, porque un «cambiada: sí» guardado sería un
dato que envejece solo y que dos máquinas podrían contradecir.

Y siempre **entre commits**, nunca contra tu carpeta de trabajo: un fichero a
medio editar o un `core.autocrlf` distinto convertirían medio repositorio en
deriva inventada.

> **Esto no añade pasos a nada.** El ciclo de un hallazgo es siempre el mismo —
> **auditas → arreglas → verificas**—, lo arregles con el agente o con un prompt
> manual: en los dos casos lo cierra **Verificar**, con evidencia. La deriva es
> otra cosa: una **anotación del inventario sobre la unidad** («cambió desde que
> se auditó») que no te pide nada en el momento. La recoge la operación rutinaria
> del sprint: «Seleccionar cambiadas» → auditar.

#### Qué significa cada estado

- **Cambiada desde la auditoría (N commits)** — el código ha cambiado por mano
  ajena. Es candidata a re-auditar, y es lo que marca «Seleccionar cambiadas».
  Dice cuántos commits la tocaron y la fecha del último; los merges no cuentan
  (contarían otra vez lo que ya traen dentro).
- **Arreglada — pendiente de verificar** — lo único que la ha tocado son arreglos
  hechos desde Atalaya. **No** es deriva: es la anotación de que ese cambio ya
  tiene dueño conocido y todavía no se ha confirmado. No te pide nada nuevo —
  ibas a verificar de todas formas—, y por eso no entra en «Seleccionar
  cambiadas»: auditar entera una clase que solo espera un verify sería pagar de
  más. En cuanto la verificación sale en verde, la anotación desaparece sola.
- **Historial no disponible** — no se puede saber, y se dice cuál de los tres
  casos es: el commit de su auditoría no está en tu clon (clon superficial o
  recién hecho), el historial se reescribió (rebase o force-push), o auditaron en
  otra máquina en un commit que tú aún no tienes («haz pull»). Ni «sin cambios»
  ni «cambiada» serían ciertas.
- **Ya no existe** — el fichero desapareció del repositorio. Sus hallazgos
  activos salen en «Sin código» (más abajo).
- Lo **nunca auditado** no aparece aquí. Eso es cobertura inicial, otra pregunta.

Una clase **movida o renombrada** cuenta como modificada, y el detalle dice a
dónde fue a parar. Cualquier cambio de contenido cuenta, aunque sea un comentario:
re-auditar una clase por un cambio trivial cuesta poco; pasar por alto uno real,
mucho.

#### El guardarraíl: los arreglos no se cuentan como deuda nueva

Cuidado con el bucle: si arreglas un hallazgo con el agente y commiteas, esa
clase «ha cambiado desde su auditoría»… por culpa de la propia auditoría. Sin
freno, cada arreglo realimentaría la lista y el ciclo no convergería nunca.

Atalaya reconoce **sus propios arreglos**: al terminar uno, guarda la huella del
contenido que dejó escrito en cada fichero. Cuando después mira el historial, un
commit cuyo contenido case con esa huella es, con certeza, el que publicó ese
arreglo. Entonces:

- Solo arreglos propios → **arreglada, pendiente de verificar**.
- Al menos un commit ajeno → **cambiada**, como siempre. Esto incluye el caso del
  arreglo que de paso tocó otros ficheros: para la clase de su hallazgo es propio;
  para las demás es ajeno, y es lo correcto — código tocado sin auditoría detrás
  es candidato.
- **Tres arreglos** sobre la misma clase sin volver a auditarla → **cambiada**
  aunque todos sean propios. Tanto retoque junto merece una mirada fresca. El
  umbral cuenta **solo los que están sin verificar**: tres arreglos verificados
  uno a uno no disparan nada; tres sin verificar, sí. Verificar y re-auditar
  ponen el contador a cero, cada uno a su manera.

**El bonus del arreglo con agente.** El flujo no cambia —arreglas, commiteas y
**Verificar** cierra el hallazgo—, pero como Atalaya sí sabe qué escribió, al
verificar en verde la clase **ni siquiera queda marcada** como cambiada: la
huella le dice que ese commit era suyo y ya está comprobado. Si la verificación
falla, el hallazgo sigue vivo y la marca también: no se da por bueno nada sin
evidencia.

Con el **prompt de arreglo** el hallazgo se cierra igual —lo cierra Verificar, con
la misma evidencia—, pero el commit lo escribió otro y Atalaya no tiene nada que
reconocer, así que la clase se queda marcada como **cambiada**. Eso es verdad y
no urge: la unidad tocó código que nadie ha vuelto a barrer entero, y quien la
recoge es la pasada rutinaria del sprint cuando toque. **No hace falta verificar
dos veces ni re-auditar a propósito para quitar la marca.**

> **Nota de migración.** Los arreglos hechos **antes** de que existiera esta
> funcionalidad no dejaron huella registrada, así que sus commits salen como
> ajenos y la clase aparece como «cambiada» aunque en su día se verificara. No es
> un fallo: es que no hay nada que reconocer, y la marca dice la verdad sobre el
> inventario. Se va sola en la siguiente pasada de «Seleccionar cambiadas»;
> ocurre una vez y no pide ninguna acción extra.

> **Si enmiendas, aplastas o rebasas un commit de arreglo antes de publicarlo**,
> su contenido deja de casar y la clase saldrá como «cambiada». Es a propósito:
> el error se comete hacia re-auditar de más, nunca de menos.

#### Sin código: hallazgos de clases que ya no existen

Cuando un fichero desaparece del repositorio, sus hallazgos activos se quedan sin
nada que mirar: no se pueden verificar y no se resuelven solos. El panel del ciclo
los cuenta en **«Sin código»**, con un «Revisar» que abre la lista: cada hallazgo,
dónde vivía y **el commit que borró ese fichero**.

Ahí puedes marcarlos y usar **«Resolver por código eliminado»**: se resuelven con
tu nombre y ese commit como evidencia. Nunca lo hace la aplicación sola — un
fichero que no está donde estaba puede haberse movido, y «no está» no es «ya no
existe». Ojo con el fichero **troceado**: sale como borrado más clases nuevas, y
Atalaya lo presenta tal cual en vez de coserlo a ojo.

#### Lo que este análisis no ve, y lo dice

Arriba del inventario aparecen los avisos que condicionan la lectura:

- La **rama**: la deriva se mide contra la rama en la que esté tu clon, y el panel
  lo dice. Si no es la rama por defecto del repositorio, lo matiza — medir contra
  una rama de trabajo es legítimo, pero hay que saberlo.
- **Clon atrasado**: si tu HEAD va por detrás del remoto, haz pull; lo que veas
  puede estar incompleto.
- **Cambios sin commitear**: no son deriva (no hay commit que comparar), y se
  avisa de cuántos ficheros son.

El cálculo va **fuera del hilo de la interfaz**: el inventario se abre en el acto
y la deriva aparece cuando llega. Sobre un clon de ~900 clases tarda **décimas de
segundo** en el caso normal.

#### Cambiar de ciclo: durante el ciclo la deriva informa, al cambiar de ciclo se cobra

**El ciclo se cierra solo, al completarse. No hay ningún botón de cerrar.** En cuanto
no queda ninguna unidad **pendiente** en el inventario, la sesión que quitó la última
cierra el ciclo, abre el siguiente y escribe su informe. Las unidades **grandes** no
lo bloquean, y una sesión **detenida** no cierra nada: no cubrió lo que decía cubrir.
Si dos personas llegan a la vez, cierra una sola —la otra ve que el ciclo ya avanzó y
desiste—, así que el cierre nunca se hace dos veces.

Cuando pasa, la aplicación **te avisa**: una línea discreta arriba de la ventana —
«Ciclo 1 cerrado · 11/11 auditadas · 1 unidad sembrada como pendiente · Ciclo 2
abierto»— con un enlace al informe del cierre. Se queda hasta que la descartas: es la
única vez que ese resumen pasa por delante, y perderlo por estar mirando otra pantalla
sería perder la foto de con qué cerró el ciclo.

Durante el ciclo la deriva **no reabre nada**. Una clase que ya auditaste sigue
auditada aunque su código haya cambiado: si la deriva devolviera unidades a la
cola, un repositorio vivo no dejaría cerrar un ciclo nunca. Te lo dice, y decides
tú si la recoges con «Seleccionar cambiadas» o la dejas para la siguiente.

Pero esa deuda de mirada no se evapora al cerrar. **Cuando el ciclo se cierra y se
abre el siguiente, la deriva acumulada se cobra**: el inventario nuevo no nace todo
pendiente ni arrastra las auditadas sin mirar, se **siembra** con lo que se puede
demostrar.

| Cómo llega al cierre | Con qué estado nace en el ciclo nuevo | Por qué |
|---|---|---|
| **Auditada y sin deriva** | **Auditada** (conserva su commit de auditoría) | El código es el que se miró. Re-auditarlo sería quemar cuota sin causa. |
| **Cambiada desde su auditoría** | **Pendiente** | Lo que se auditó ya no es lo que hay. Aquí es donde se paga. |
| **Sin historial disponible** | **Pendiente** | No se puede demostrar que no cambió, y sin evidencia no hay estado. |
| **Arreglada — pendiente de verificar** | **Auditada**, y conserva su **Verificar** | Su alcance está acotado por la huella del arreglo: verificar sigue siendo su cierre correcto, y es más barato que re-auditarla entera. |
| **Ya no existe** | La retira el **re-escaneo**, como siempre | Sus hallazgos activos siguen su camino en «Sin código». |

Consecuencias prácticas, para que no sorprendan:

- La clase que nace **pendiente pierde su marca de deriva**, y con ella el ancla
  del ciclo anterior: su próxima auditoría estrenará commit. Por eso el contador
  de **cambiadas queda a cero** al arrancar el ciclo — hasta que el código vuelva
  a moverse, claro.
- El contador de **arregladas sin verificar no se pone a cero**, y es a propósito:
  esas conservan su estado y su acción, así que la anotación sigue siendo verdad
  hasta que la verificación las cierre.
- **Sembrar pendientes no lanza nada.** Nadie re-audita solo: el ciclo nuevo
  simplemente sabe qué le queda por mirar, y lo lanzas tú cuando toque.
- Si esta máquina **no tiene el clon** de la aplicación, no hay historial con el
  que demostrar nada y el ciclo nuevo nace **entero pendiente**. Es el mismo caso
  que «sin historial disponible», y el error se comete hacia re-auditar de más.

**El cierre no maquilla.** En el momento de cerrar, el resumen de la sesión y el
informe consolidado dicen qué queda envejecido — «Cerrado con 4 cambiadas desde su
auditoría y 1 sin verificar»—, que es exactamente lo que el ciclo siguiente hereda.
Si no hay nada envejecido no se dice nada: una frase que informa de que no hay nada
que informar es ruido.

**«Reiniciar ciclo» es otra cosa.** Es el único botón que cambia de ciclo, y **no es
el cierre**: no siembra, sino que abre un ciclo nuevo con **todo pendiente**, a
propósito. Es el gesto de quien quiere volver a mirarlo todo desde cero, y sembrarlo
respetando las auditadas lo dejaría sin efecto justo en la aplicación que está al día.
Sigue sin borrar nada, y no espera a que el ciclo esté completo: se puede pulsar
cuando quieras. Y como ya lo pone todo pendiente, de paso pregunta con qué **lupa** se
va a volver a mirar (ver «Ciclos temáticos»).

**El ciclo nuevo hereda la configuración del que cierra.** Temática y modelo preferido
pasan tal cual al ciclo siguiente —el cierre es automático y no puede quedarse esperando
a que alguien decida—, y quien cerró ve a continuación el diálogo de configurar por si
quiere cambiarla. Con la misma temática la tabla de arriba es la que manda; con otra,
todo cambia: ver «Ciclos temáticos», justo debajo.

### Informes

Todo lo que Atalaya ha dejado escrito: los informes de **sesión**, las **verificaciones**, los
**consolidados** de cierre de ciclo y los de **operaciones**. Solo aparece lo que tiene informe
de verdad en el hub.

Un **informe de verificación** cuenta lo que ninguna otra cosa guarda: qué hallazgo se
verificó, **qué código se le enseñó al instrumento** —el fragmento anclado, el símbolo
re-anclado o la unidad entera—, el veredicto con el razonamiento textual del modelo, y los
tokens con su coste. Sin el «qué se le enseñó», releer un «no concluyente» no permite saber si
al modelo le faltó contexto o le faltó criterio, que son dos cosas con dos remedios distintos.

Cada fila trae fecha, aplicación, tipo, modo, usuario, unidades procesadas, hallazgos
(±) y coste; lo que un informe sin sesión asociada no declare sale como «—».

Filtros, con los mismos patrones que Hallazgos: **aplicación**, **tipo**, **usuario**,
**fechas** (presets de 7/30/90 días, «Todo», o un rango a mano) y **búsqueda de
texto** — que mira **dentro** del informe, así que «ReadCSV» encuentra el informe que
lo menciona aunque no sepas de qué sesión salió. Sin tildes y sin mayúsculas: da igual
cómo lo escribas.

Un clic abre el informe **renderizado dentro de la aplicación**, con sus tablas, sus
listas y su código legibles, y con su scroll propio. Desde ahí:

- **Descargar .md** guarda una copia donde tú elijas, con un nombre que dice qué es
  (`atalaya-{app}-{tipo}-{fecha}.md`).
- **Ver hallazgos de esta sesión** abre Hallazgos filtrado por esa aplicación.
- **Volver** devuelve la lista tal y como la dejaste, con sus filtros y su posición.

Los informes son **inmutables**: esta vista solo lee. Los enlaces que un informe
contenga se abren en tu navegador, nunca dentro de la ventana.

#### Cómo está montado un informe de sesión

Un informe de sesión tiene **dos lectores**: quien tiene que arreglar los hallazgos de su código,
y quien mantiene Atalaya y vigila lo que cuesta. Son dos preguntas distintas, así que el documento
va en dos partes.

**El cuerpo es para actuar**, y no lleva un solo token:

- **La cabecera** dice qué, cuándo, quién, sobre qué, con qué y cuánto: aplicación, fecha **en tu
  hora con la zona puesta**, autor, commit auditado, proveedor y modelo, ciclo, temática, modo, y el
  coste en una línea — *«185,3 AI credits · 92,6 por unidad · 5 min 36 s»*. El coste **por unidad**
  es lo que hace comparables dos sesiones de tamaños distintos.
- **El resumen** contesta primero lo que se pregunta primero: **cuántos hallazgos y de qué
  gravedad** (*«1 Crítica · 10 Altas · 9 Medias · 5 Bajas»*), qué es nuevo y qué se reconcilió, y
  **cuántas unidades y cómo cerraron**. Debajo, solo si los hay, los números que necesitan causa:
  supresiones por patrón, resoluciones degradadas, disputas, unidades incompletas.
- **La cobertura**, una línea por unidad y no una por pasada: estado, cuántas pasadas, **por qué
  dejó de barrerse** y qué revisó el auditor, dicho una sola vez.
- **Los hallazgos**, agrupados por fichero —la ruta va una vez, como título— y ordenados por
  gravedad dentro de cada uno.

**El anexo técnico va al final**, tras un separador, y es diagnóstico del coste de la propia
auditoría: tokens, reparto por conceptos, caché, consumo por unidad y por pasada, cuántos turnos de
conversación tuvo cada unidad —y cuántas veces hubo que empezar otra, con el motivo— y la cobertura
que el auditor declaró pasada a pasada. **No se ha quitado nada del informe**: se ha movido.

#### Por qué dejó de barrerse una unidad

Es la parte de la cobertura que más decide, porque «6 pasadas» no dice si se miró entera:

- **Cerrada por dos pasadas secas** — convergió: el auditor no saca más de ahí. Es lo más parecido
  a «barrida» que existe.
- **Cerrada por tope** — se acabaron las pasadas antes de converger, y el informe dice **si la
  última todavía aportaba**: *«cerrada por tope: seguía encontrando, 1 en la última»*. Esa unidad
  **no** está barrida.
- **Cortada por presupuesto** — saltó un techo de gasto.
- Y si alguna pasada fue **muda** —el auditor no llamó a ninguna herramienta— se dice ahí mismo: es
  gasto sin trabajo.

#### Posibles duplicados

El auditor describe a veces el mismo defecto dos veces con otras palabras, en pasadas distintas. El
informe **no fusiona nada** —decidir que dos descripciones son el mismo defecto es un juicio sobre
el código, y equivocarse borra un hallazgo— pero **marca**: cuando dos hallazgos comparten regla,
miembro y caen a cinco líneas o menos uno de otro, el segundo sale con *«⚠ Posible duplicado de
BUG-xxxx»*. Sirve para que quien no conoce el código no cuente 25 defectos donde hay veinte.

La marca es una **pista, no un veredicto**: está calibrada para equivocarse poco, así que deja
pasar duplicados que un humano sí ve —dos hallazgos anclados a alturas distintas del código, o el
mismo problema bajo dos reglas—. Los que marca, mirados de dos en dos, se resuelven de un vistazo.

#### Lo que no verás

- **La confianza** no se escribe cuando es la que el modo reparte a todo lo que nace en la sesión
  —en un barrido por lotes son todos «Media»—. Se sigue guardando, y aparece cuando dice algo.
- **Nombres de fase ni de hito.** El informe no lleva referencias internas del desarrollo de
  Atalaya en ningún sitio, ni en el anexo: viven en el repositorio, que es donde se pueden buscar.

**En pantalla**, el informe se lee en columna y no a todo lo ancho del monitor: una línea de mil
seiscientos píxeles no se puede seguir con la vista. Y el **anexo técnico —diagnóstico** va **plegado**: lo dice
el propio informe, «no hace falta para actuar sobre los hallazgos». Se abre con un clic, y entonces
sí ocupa el ancho entero, porque lo que lleva dentro son tablas.

### Nueva aplicación

El asistente de alta: repositorio, clon local, escaneo inicial y —si procede—
importación del baseline v4. Publica una sola vez, al final.

**El repositorio se elige, no se escribe.** El desplegable trae los repositorios de tu
organización —la misma cuenta de GitHub que ya tienes conectada— y enseña solo el nombre corto:
`XBLAST`, no la URL entera. Escribe para filtrar cuando haya muchos, y usa **Recargar** si acabas
de crear el repositorio y todavía no sale.

Los que **ya están en el hub** aparecen en la lista marcados con «· ya en el hub». Elegir uno no
da de alta nada: te lleva a **vincular tu clon** de la aplicación que ya existe, que es el gesto
que te faltaba. Dar de alta la misma aplicación dos veces no es posible ni desde aquí ni forzando
el botón.

**El nombre no se teclea.** Es el del repositorio que elijas, y hasta entonces está vacío. No hay
un nombre de la aplicación distinto del nombre de su repositorio.

**La ruta del clon** sigue siendo un cuadro de texto —pega la ruta si la tienes— con un
**«Examinar…»** al lado que abre el selector de carpetas.

**Si la lista no carga** —sin red, o sin permiso para listar los repositorios de la
organización— el propio desplegable lo dice («no se pudo cargar la lista · reintentar») y **el
alta no se bloquea**: escribe la URL del repositorio ahí mismo, en ese mismo control, y sigue como
siempre. La lista es la comodidad, no la única puerta.

### Cuenta

El estado de la conexión: quién eres, si GitHub acepta tus credenciales, si el hub
está clonado y si cada **proveedor de auditoría** responde. Es el sitio al que te manda cualquier
fallo de conexión.

Las tres primeras filas son de **GitHub y no se sustituyen nunca**: sin ellas no hay identidad, ni
autoría de los commits, ni hub donde escribir los hallazgos. Debajo hay **una fila por proveedor de
auditoría**, cada una con su propio piloto y su propia instrucción si falta algo. **Basta con tener
uno listo** para poder auditar.

La fila del **hub** dice cuándo se sincronizó por última vez, y nada más: la ruta del clon en esta
máquina está debajo, en «Hub local», que es la tarjeta que va de eso.

Cada fila dice su estado con **icono, color y palabra**, las tres cosas a la vez: **Disponible**
(verde), **Comprobando** (con su anillo girando), **No comprobado** (gris) y **No disponible**
(rojo, con el motivo en la línea de debajo). Un color solo no se lee si no distingues dos tonos, y
un glifo suelto —«…», «•»— no significa nada para quien no lo escribió.

Un proveedor **opcional** que no tengas instalado —hoy, Claude Code— sale con un `+` gris y la
frase «disponible si lo activas»: te dice que existe y cómo activarlo si te interesa, y ahí acaba.
**No es un fallo**, no se pinta como tal y no impide nada.

### Acerca de

Está en el raíl, en **Sistema**, debajo de Ajustes. Es la ficha del binario que estás ejecutando, y
es la pantalla a la que ir cuando algo va raro y hay que decir con qué se está trabajando:

| Dato | Qué es |
| --- | --- |
| **Versión** | El número, tal cual lo declara el binario |
| **Canal** | «release» si viene del workflow de publicación; «build local» si lo compiló alguien |
| **Commit** | El commit exacto del que salió |
| **Fecha del binario** | Cuándo se construyó este ejecutable |
| **Proveedor** y **Modelo** | Con quién y con qué estás auditando ahora mismo |
| **Organización** | La del hub, con su logotipo |

Y cuatro acciones: **Buscar actualizaciones** —pregunta a GitHub en el momento, sin esperar al
chequeo periódico, y contesta ahí mismo—, **Novedades** (las notas de las versiones publicadas),
**Repositorio** y **Manual**.

*(Hasta la 1.4.4 era una ventana que se abría desde el fondo de Ajustes → Avanzado. No se edita nada
ahí dentro, así que no era un ajuste — y ahí no lo encontraba nadie.)*

### Ajustes

**Ajustes tiene cinco secciones**, en la lista de la izquierda de la propia pantalla. Se navegan
como el menú lateral —la que estás mirando va resaltada— y cada una es una página corta en vez de
un scroll largo:

| Sección | Qué hay dentro |
| --- | --- |
| **Proveedor y modelo** | Con quién auditas y con qué modelo, y el botón de actualizar la lista |
| **Auditoría** | El tope de pasadas del barrido, el **modo exhaustivo** y el interruptor del **arreglo asistido** |
| **Tarifas** | La tabla de precios por modelo de la organización, con los modelos usados que aún no tienen tarifa |
| **Apariencia** | Tema claro / oscuro |
| **Avanzado** | Editor preferido, frescura, sincronización del hub, timeout y la zona peligrosa |

**Guardar está siempre a la vista**, en la barra del pie. Cuando hay algo cambiado sin guardar la
barra lo dice —«Hay cambios sin guardar»— y aparece **Descartar**, que devuelve las cajas a lo que
hay escrito en el fichero. En la sección **Tarifas** esa barra guarda la tabla del hub, que es lo
que se está editando ahí.

Cada ajuste cuenta en **una línea** qué hace y cuándo aplica; lo que necesita más explicación vive
detrás de un **«Más»** en la misma fila, plegado.

**«Acerca de» ya no está aquí**: es una entrada del raíl, en el grupo Sistema, debajo de Ajustes.
Y el **umbral de unidad grande** tampoco tiene fila: no se edita en esta máquina —es política de
cada aplicación— y se gobierna en **Inventario → Gobernanza → Umbrales**, que es donde también se
explica.

**Los ajustes son de esta máquina** —viven en tu `settings.json`, no en el hub—, así que
cambiarlos no le toca nada a tus compañeros. Ésa es justamente la regla que decide qué está aquí:
**lo que escribe algo que el equipo comparte se gobierna en la aplicación, no en tus Ajustes.** Por
eso el **umbral de unidad grande** no está en esta pantalla —clasifica el inventario y crea los
hallazgos de tamaño, que son de todos— y se gobierna en **Inventario → Umbrales**. La **frescura**
sí está aquí: solo colorea tu lista de hallazgos y no le cambia el estado a nadie.

> **Lo que cuesta y lo que da el modo exhaustivo.** La pantalla dice el precio —«aumenta el coste
> de forma drástica (M2: ×3 por unidad) y puede producir hallazgos duplicados»— porque es lo que hay
> que saber antes de encenderlo. La otra mitad de la misma medición está aquí: encuentra, de media,
> **dos defectos de gravedad media más por cada veinte**. Con esas dos cifras delante, la decisión
> es tuya.

Cada control dice bajo su caja **cuándo surte efecto**, porque no todos aplican igual:

| Ajuste | Qué gobierna | Cuándo aplica |
| --- | --- | --- |
| **Pasadas del barrido (tope)** | El presupuesto de pasadas por unidad; de él salen también las dos secas que cierran el barrido (fábrica: 6) | En las auditorías que lances **a partir de ahora** |
| **Modo exhaustivo** | Si cada pasada es una petición nueva con el prompt entero (apagado de fábrica: cada unidad se audita como una conversación) | En las auditorías que lances **a partir de ahora** |
| **Frescura (días)** | Cuándo un hallazgo confirmado se marca por revisar | Al guardar; la lista lo aplica al dibujarse |
| **Proveedor de auditoría** | Con quién auditas y verificas tú | En las sesiones que lances a partir de ahora |
| **Modelo del auditor** | Con qué modelo del proveedor elegido | En las sesiones que lances a partir de ahora |
| **Arreglo asistido** | Si aparece «Arreglar con agente» | Al guardar |
| **Tarifas** | Lo que cuesta un millón de tokens de cada modelo, y con ello el coste de toda sesión | Al guardar la tabla; los costes ya enseñados se recalculan |
| **Sincronización del hub (s)** | Cada cuánto se buscan cambios de tus compañeros | Al guardar, sin reiniciar |
| **Timeout de Copilot (min)** | Espera máxima por una respuesta del modelo, y por cada compilación del arreglo | Al guardar, en el siguiente turno |
| **Editor preferido** | Con qué editor se abre el código | Al guardar |
| **Tema claro** | El aspecto de la aplicación | Al guardar |

**Si escribes un número imposible, se te dice.** Cada campo numérico tiene un mínimo —15 s la
sincronización, 1 el resto— y al guardar, si hubo que aplicarlo, el aviso dice cuál era y la caja
enseña lo que de verdad quedó guardado. Ningún valor se descarta en silencio.

**Cambiar el umbral de tamaño y ver el efecto**: se hace en **Inventario → Umbrales · Gestionar**
de la aplicación, y luego se **re-escanea**. Las unidades que pasen a ser grandes salen de la cola
de pendientes con su hallazgo de tamaño; las que dejen de serlo vuelven a la cola y ese hallazgo
**se resuelve por medida**, con el número escrito en su historial («1117 LOC < umbral 1500»).

**Si venías de una versión anterior** y tenías un umbral propio en esta pantalla, la primera vez
que abras el Inventario de cada aplicación se te ofrece llevarlo a su política —«tenías 30 en esta
máquina, ¿lo aplico a la aplicación?»— y se te pregunta **una sola vez** por aplicación. Nada se
tira sin preguntar.

#### Modo exhaustivo: qué cuesta y qué compra

Normalmente **cada unidad se audita como una sola conversación** con el modelo: la primera pasada le
manda las reglas, el código y los hallazgos que ya existen, y las siguientes son turnos de esa misma
conversación, así que no hay que volver a mandarle nada de eso. Es lo que hace que un barrido cueste
lo que cuesta hoy.

**Encendido, cada pasada vuelve a ser una petición nueva** con el prompt recompuesto entero, que es
como se barría antes. No es un modo aparte con reglas propias: es el mismo camino que la aplicación
usa de respaldo cuando una conversación no puede continuar, puesto a mano.

Al lado del interruptor hay un **icono de aviso** con el precio, que no es una impresión sino una
medida:

> Aumenta el coste de forma drástica (M2: ×3 por unidad) y puede producir hallazgos duplicados.
> Encuentra, de media, dos defectos de gravedad media más por cada veinte.

Los números, con más detalle, salen de comparar las dos formas sobre el mismo caso de referencia:

| | conversación (lo normal) | exhaustivo |
| --- | ---: | ---: |
| Coste por unidad (credits, tarifa Opus) | **64,2** | 204,7 |
| Escritura de caché por pasada, de la 2ª en adelante | **3.375** | 16.122 |
| Hallazgos marcados como posible duplicado (3 tandas) | **0** | 7 |
| Defectos encontrados, de los 20 del caso de referencia | 17,7 | **20,0** |
| Pasadas por unidad | **4,0** | 6,0 |
| Segundos por unidad | **199** | 610 |

**Cuándo tiene sentido encenderlo**: una auditoría puntual sobre código crítico, en la que dos
defectos de gravedad media valen tres veces la factura. **Cuándo no**: el trabajo de todos los días.
Los dos defectos que se ganan son, medidos, de los que aparecen tarde — el barrido normal converge
antes, y ése es exactamente el intercambio.

**Queda dicho con qué se auditó**: mientras la sesión corre, el pie pone la palabra *exhaustivo*
junto a la unidad; la cabecera de su informe dice **«Modo: Lotes · exhaustivo»**; y la sesión lo
guarda, así que Métricas puede separar el gasto de las dos formas. Cambiar el interruptor **no toca
la sesión en curso**: la que esté corriendo termina como empezó.

#### Tarifas: de dónde salen y cómo se corrigen

**No hay nada que activar.** Atalaya trae las tarifas puestas —verificadas contra la tabla de
precios publicada de Copilot— y las escribe en el hub la primera vez que se conecta a él, sin
preguntar. Un precio publicado es un dato, no una decisión tuya: por eso la primera sesión de una
instalación limpia ya sale con su coste.

**Lo que edites manda sobre lo que traiga la versión, siempre.** La siembra solo rellena lo que
falta: si corriges un precio, ninguna versión futura te lo pisa; y si aparece un modelo nuevo, su
tarifa se añade sola sin tocar las tuyas. Cuando venza un promocional o no te cuadre una cifra con
la factura de tu organización, la corriges aquí y **todos los costes ya enseñados se recalculan** —
también los del histórico.

**Siguen viviendo en el hub, no en tu máquina.** Un precio es del contrato de tu organización con su
proveedor, así que la tabla la ve todo el equipo y el commit del hub dice quién cambió qué y cuándo.
Ajustes es **dónde se editan**, no dónde se guardan.

El detalle de qué contiene la tabla, qué significa cada columna y por qué los modelos de Claude Code
no están, en **[Las tarifas se editan, y viven en el hub](#las-tarifas-se-editan-y-viven-en-el-hub)**.

---

## Ciclos temáticos

Hasta ahora todo ciclo auditaba «en general». Desde esta versión **cada ciclo se configura**:
una **temática** —General por defecto, que es lo de siempre— y un **modelo preferido**. Con una
temática concreta el auditor busca **solo** hallazgos de esa familia. Es la lupa del ciclo, y
cambia lo que «auditada» significa: auditada bajo Rendimiento es «no hay más defectos de
rendimiento que sacar de aquí», no «esta unidad está limpia».

### Las seis lupas

Es un catálogo cerrado, de la casa, versionado junto a la rúbrica de severidad:

- **General** (recomendada) — el criterio completo de siempre. Es la única que mantiene TODO el
  catálogo de hallazgos, y el ciclo de referencia.
- **Seguridad** — secretos y credenciales, validación de entradas, inyección, transporte
  inseguro, permisos, criptografía casera.
- **Rendimiento** — algoritmia innecesariamente cara, asignaciones y colecciones ineficientes,
  E/S y llamadas redundantes, recursos que no se reutilizan.
- **Fiabilidad** — nulos, índices, excepciones tragadas o sin manejar, casos límite, contratos
  incumplidos, gestión de recursos (using/dispose).
- **Concurrencia y asincronía** — carreras, bloqueos, `.Result`/`.Wait`, estado compartido
  mutable, deadlocks.
- **Mantenibilidad** — duplicación, nomenclatura, documentación que miente, complejidad y tamaño,
  código muerto.

Cada una lleva escrito, en el mismo sitio, qué busca y qué **no** debe reportar; las dos listas
viajan en el prompt del auditor. Temáticas personalizadas no hay: está en el backlog.

### La regla dura

**Fuera de la temática no se reporta nada.** Ni «también he visto esto», ni la crítica de otra
familia que el auditor tenía delante. Un enfoque que además mira otras cosas no es un enfoque, y
para la mirada completa existe el ciclo General. Por eso **un ciclo temático no sustituye a uno
General**: es una pasada acotada, y una aplicación que solo haya tenido ciclos de Rendimiento no
ha sido auditada de seguridad ni de fiabilidad.

Lo que la temática **no** cambia: ni la rúbrica de severidad —un secreto en claro es crítico en
un ciclo de Seguridad exactamente igual que en uno General—, ni la gobernanza: silencios,
patrones silenciados, directivas y la guarda de evidencia aplican por aplicación, sin mirar la
lupa.

### Qué se reconcilia, y qué envejece

Cada hallazgo lleva la **temática del ciclo que lo detectó** (los anteriores a esta versión son
General). Se ve en su ficha y se filtra en Hallazgos.

Un ciclo temático encontrará unidades con hallazgos previos de otras temáticas. La regla es
simple y hay que conocerla:

- El auditor temático **reconcilia solo los hallazgos de su temática** (y añade ubicaciones solo
  a esos). Los de otras temáticas se le enseñan aparte, para que no los re-reporte como nuevos,
  pero **no los juzga**: ni presente, ni arreglado, ni no-es-defecto. Si lo intenta, Atalaya
  rechaza el veredicto y lo anota en la sesión.
- Consecuencia honesta: **los hallazgos de otras temáticas envejecen durante un ciclo temático**.
  Nadie los está mirando, y su frescura los llevará a «por revisar» por el camino normal. Es la
  verdad, y ocultarla haría que un ciclo de Rendimiento pareciera haber vuelto a confirmar la
  credencial en claro que nadie miró.
- Un ciclo **General** reconcilia todo, sea de la temática que sea. Exactamente como antes.

### Configurar el ciclo

El diálogo aparece en tres momentos:

- **Al dar de alta una aplicación**, tras el escaneo y antes de abrir el ciclo 1. General viene
  preseleccionada y marcada como recomendada.
- **Al cerrarse un ciclo.** El siguiente se abre **heredando** la configuración del anterior —el
  cierre es automático y no espera a nadie— y quien lo cerró ve el diálogo para cambiarla si
  quiere. Mientras el ciclo nuevo no tenga trabajo hecho bajo su lupa, cambiarla no cuesta nada.
- **Desde el panel del ciclo**, con «Configurar ciclo», en cualquier momento. «Reiniciar ciclo»
  lo enseña también.

**Cambiar de temática con trabajo hecho** es siempre la misma operación, se haga cuando se haga:
un aviso —«N unidades auditadas pasarán a pendientes; los hallazgos existentes no se tocan»— y la
re-siembra. **Y queda registrado**: el ciclo guarda su historial de temáticas —cuál, desde cuándo,
hasta cuándo y quién la cambió—, el panel del ciclo enseña las anteriores debajo del distintivo, el
informe de cierre lista con qué lupas se trabajó, y la secuencia de ciclos de Métricas pinta el
bloque partido.
El trabajo hecho con la lupa anterior no se borra de ninguna parte. Es lo que dice la tabla de
siembra, extendida:

| | Misma temática | Temática distinta |
|---|---|---|
| Auditada y sin deriva | Auditada | **Pendiente** (auditada bajo otra lupa no es auditada bajo esta) |
| Cambiada desde su auditoría | Pendiente | Pendiente |
| Grande | Grande | Grande |
| Arreglada — pendiente de verificar | Auditada, conserva su Verificar | **Pendiente**, y su hallazgo **conserva** la acción Verificar: el arreglo sigue necesitando su cierre, sea cual sea la lupa |

Cambiar solo el modelo preferido no re-siembra nada: no cambia la lupa.

### El modelo preferido

El diálogo ofrece los modelos disponibles para **tu** proveedor preferente, y lo elegido se guarda
con el ciclo, en el hub, para todo el equipo. Es **preferencia, no imposición**: no todo el mundo
tiene las dos casas. Al lanzar una sesión con otro proveedor u otro modelo, el diálogo de lanzar
lo avisa en una línea —«Este ciclo prefiere opus (Claude Code); auditar con otro juez puede
producir disputas»— y deja continuar. La sesión registra, como siempre, el proveedor y el modelo
reales.

### Dónde se ve

El **distintivo** de temática va en la tarjeta del Portafolio y en el panel del ciclo; los
informes de sesión y de cierre registran la temática del ciclo; Hallazgos filtra por ella y la
ficha la enseña; y la secuencia **Ciclos y temáticas** de Métricas pinta la historia entera.

## Proveedores de auditoría

Atalaya sabe auditar con **dos casas distintas**, y tú eliges con cuál. Antes solo había una, y
cuando la organización agotaba su cuota de Copilot todo el mundo se quedaba parado; ahora hay una
segunda bolsa, independiente. De propina te da algo que no se podía tener con una sola: **una
segunda opinión de verdad**.

> **Claude Code es opcional. Siempre.**
> **Copilot** es el proveedor por defecto y el único requisito del equipo. Si no tienes Claude Code
> instalado —que es la situación normal— **no verás ningún aviso, ninguna exigencia ni ninguna
> merma**: la aplicación se comporta exactamente igual que antes de que esto existiera. El piloto
> de Claude Code en **Cuenta** te informa de que hay un extra disponible; no te reclama nada, no se
> pinta en rojo y no bloquea nada. Y si no está instalado, **Ajustes no te ofrece elegirlo**: con
> una sola opción no hay selector que enseñar.

| | **GitHub Copilot** | **Claude Code** |
| --- | --- | --- |
| Qué necesitas | Tu cuenta de GitHub conectada, con asiento de Copilot | El CLI de Claude Code instalado y con sesión iniciada |
| Quién paga | El asiento de tu organización (AI credits) | Tu suscripción de Claude |
| Cómo se mide el gasto | En AI credits, derivados de los tokens con la tarifa del modelo | En llamadas y tokens: **no se tarifa** |
| Cómo se instala | Nada: viaja dentro de Atalaya | `npm install -g @anthropic-ai/claude-code`, y `claude` una vez en tu terminal |
| Auditar y verificar | Sí | Sí |
| Arreglo asistido | Sí | Sí |

**Atalaya no guarda credenciales de Anthropic.** Usa la sesión que el CLI ya tiene en tu máquina,
exactamente igual que con Copilot usa tu login de GitHub. Si no has iniciado sesión, Atalaya te lo
dice en **Cuenta** y te manda a hacerlo en tu terminal; no hay ningún sitio en la aplicación donde
pegar una clave, y es a propósito.

**GitHub sigue haciendo falta.** Elegir Claude Code cambia quién juzga el código y nada más: la
identidad, la autoría de los commits y el hub donde viven los hallazgos siguen siendo de GitHub. No
es un sustituto, es un segundo auditor.

### Cómo se elige

En **Ajustes → Proveedor y modelo**. Es una preferencia **tuya y de esta máquina** —cada uno
audita con la cuenta que tiene— y se aplica a la **siguiente** sesión: a mitad de un barrido no se
cambia de juez. Cada proveedor recuerda **su propio modelo**, así que ir y volver no te deshace la
elección.

Antes de gastar, **el diálogo de lanzamiento dice con quién vas a auditar**: «Vas a auditar 2
unidades de XBLAST con Claude Code (modelo opus)». El juez de una sesión no debería descubrirse
leyendo el informe.

### Qué NO cambia

Nada de lo que Atalaya hace con lo que el auditor le cuenta. Los dos proveedores reciben **el mismo
prompt** y **las mismas herramientas**, con los mismos nombres; y por encima de ellos, la
reconciliación, los veredictos, la evidencia de cambio, la huella, los silencios, los ciclos y los
informes funcionan **exactamente igual**. Cambiar de proveedor no cambia las reglas: cambia quién
las aplica.

Cada **sesión**, cada **hallazgo** y cada **informe** deja escrito **con qué proveedor y con qué
modelo** se hizo. Así, meses después, se puede leer quién dijo qué.

### Cuando las dos casas discrepan

Ya existía la mecánica: un auditor que sostiene que un hallazgo **nunca fue un defecto** no lo
resuelve, deja una **disputa (⚖)** colgada con su razonamiento, y las disputas se **acumulan**.

Con dos proveedores esa marca vale más que antes. Tres modelos de la misma casa discrepando pueden
estar compartiendo el mismo punto ciego; **dos casas distintas coincidiendo** en que algo no es un
defecto es lo más parecido a una segunda opinión que hay. Por eso la disputa guarda también **de
qué casa** venía. Sigue decidiendo una persona: la disputa informa, no cierra nada.

### Verificar entre casas

**Un hallazgo detectado por Copilot lo puede verificar Claude, y al revés.** La regla de la casa
—«cada hallazgo se verifica con el instrumento que lo detectó»— distingue **auditor de medida**,
no un modelo de otro: lo que dice es que un hallazgo que la aplicación MIDE (el tamaño de una
unidad) se vuelve a medir y no se le pregunta a un modelo, porque preguntarle a un LLM cuántas
líneas tiene un fichero es usar el instrumento equivocado. Entre auditores no hay tal regla.

Así que verificar usa **el proveedor que tengas activo ahora**, sin más. El evento y el informe
registran con qué casa y qué modelo se hizo, y si la respuesta contradice a la de la otra casa,
eso sigue el cauce de siempre: una **disputa (⚖)** con su razonamiento, o un **«no concluyente»**
con el paso siguiente escrito. Nunca se cierra nada por mayoría.

### El coste, dicho como es

**La regla, en una frase: Copilot gasta la bolsa de la organización y se mide en AI credits;
Claude Code va contra la suscripción de cada uno y no se tarifa.** Todo lo que sigue desarrolla
esa frase.

Desde el **1 de junio de 2026**, GitHub Copilot factura en **AI credits**. Atalaya habla esa
lengua: es la misma unidad que grafica el panel de tu organización, que es con lo que vas a querer
cuadrar.

**Qué es un credit.** Vale **0,01 $**. Se consume **por tokens** —entrada, salida y caché— a las
tarifas de API publicadas de cada modelo. El sistema anterior, las «peticiones premium» (llamadas ×
un multiplicador), **está retirado**, y con él la vieja cifra de «unidades SDK» que Atalaya
enseñaba: era correcta mientras aquello se facturaba así.

**Cómo se calcula.** De los tokens que la sesión guardó, con la tarifa **del modelo de esa sesión**:

```
(entrada no cacheada × tarifa de entrada)
  + (caché leída      × tarifa de caché)
  + (caché escrita    × la suya, si ese modelo la cobra aparte)
  + (salida           × tarifa de salida)   →  dólares  →  × 100 = credits
```

El modelo **se lee del registro de cada sesión y nunca se supone**. Dos sesiones del mismo día con
modelos distintos van cada una con su tarifa. Si una sesión no registró modelo, o su modelo no
tiene tarifa configurada, su coste sale como **no aplicable** y el total que la contenga se marca
**parcial** — con cuántas faltan. Nunca se le aplica la tarifa de otro modelo «parecido».

**Por qué la caché ahorra tanto.** La caché leída cuesta alrededor de **una décima parte** de la
entrada normal. Auditar la misma aplicación de seguido reutiliza el contexto, así que la segunda
unidad y las siguientes se cobran a esa décima parte. Es la razón de que una sesión larga cueste
mucho menos que la suma de sus unidades por separado.

**Y la palanca de ahorro número uno sigue siendo el modelo.** Ahora vía sus tarifas, que están a la
vista: entre el más caro y el más barato de la lista hay un factor de **veinte o más** en el mismo
trabajo. Antes de optimizar nada, mira con qué estás auditando.

**Los tokens son el hecho; los credits, un derivado.** En el hub se guardan los tokens, y el coste
se recalcula al leerlo. Eso tiene dos consecuencias buenas: **tu historial entero se reexpresa en
credits** sin tocar un solo fichero, y si mañana cambia una tarifa, los números viejos se corrigen
solos. Donde no haya tokens guardados —sesiones muy antiguas—, verás **«—»**, nunca un número
inventado.

#### Las tarifas se editan, y viven en el hub

**Ajustes → Tarifas.** Es la tabla de **GitHub Copilot**, y así se titula: los precios son los de
su tabla pública, Atalaya los siembra y los completa sola, y aquí solo se corrige lo que no cuadre.
La tabla es de la **organización**: está en el hub, la ve todo el equipo y el
historial de git dice quién cambió qué y cuándo. Se edita desde la aplicación porque las tarifas
cambian, aparecen modelos nuevos y **hay promocionales con fecha de caducidad**: corregir un precio
no puede exigir esperar a una versión nueva de Atalaya.

**Y se aplican solas.** Atalaya las siembra en el hub la primera vez que se conecta, sin que nadie
tenga que abrir esta pantalla; la siembra **rellena lo que falta y nunca pisa** lo que alguien haya
corregido. En Métricas queda el aviso de que a un total le falta gasto por contar, con su recuento,
y el enlace hasta aquí.

*(Hasta la 1.4.1 esta pantalla estaba en Métricas y era, además, lo único que llegaba a escribir la
tabla: quien no la visitaba nunca veía un coste. Ya no.)*

**Es la tabla de lo que FACTURA**, o sea de los modelos de Copilot — incluidos los de Anthropic que
Copilot revende, que ésos sí los paga tu organización. Los de Claude Code no están y no se admiten:
ahí no hay factura que calcular, y una tarifa que no gobierna nada solo consigue que alguien la
mantenga para siempre creyendo que sirve. Si tu hub venía de una versión anterior con esas cuatro
tarifas escritas, desaparecen la primera vez que guardes.

La pantalla te señala **los modelos que estás usando y no tienen tarifa**, con cuántas sesiones
esperan por ellos. Eso es lo que convierte un «parcial» en algo que puedes arreglar. Solo aparecen
modelos de las casas que facturan: a un modelo usado con Claude Code no le falta ninguna tarifa.

Un detalle que importa al editarla:

- **Caché escrita en blanco ≠ 0.** En blanco significa «este modelo no la cobra aparte» y esos
  tokens son entrada normal; un 0 afirmaría que escribir en caché es gratis, que es otra cosa.

#### Con dos proveedores

**Copilot gasta la bolsa de la organización y se mide en AI credits; Claude Code va contra la
suscripción de cada uno y no se tarifa.**

Hubo una versión intermedia en la que lo de Claude Code se valoraba igual y se etiquetaba
«equivalente API»: lo que habrían costado esos tokens pagando la API. El número salía —reproducía
al sexto decimal el que calcula el propio CLI—, pero para tenerlo había que mantener a mano una
copia de la lista de precios de Anthropic. Un precio copiado a mano es ruido el día que se escribe
y **desinformación** el día que cambia sin avisar, y encima el número no era un cobro. Se retiró
entero.

Así que hoy:

- **Copilot**: coste en **AI credits**, que es lo que tu organización paga. Entra en el azulejo, en
  la gráfica, en el ratio por unidad y en los informes.
- **Claude Code**: **llamadas y tokens** —entrada, salida y caché—, y donde iría el coste,
  **«incluido en tu suscripción de Claude»**. Ni credits, ni equivalentes, ni «tarifa no
  configurada»: ese aviso existe para mandarte a arreglar la tabla de tarifas, y aquí no hay tabla
  que arreglar.

Los tokens de esas sesiones **no desaparecen**: son dato primario, se guardan enteros y salen en el
informe y en la actividad de Métricas. Lo único que ya no existe es ponerles precio.

Si el CLI declara su propio coste, el informe lo recoge en una línea aparte —«Lo que declaró el
CLI: 0,021274 USD (tarifa de lista)»— diciendo que es un dato del proveedor y **no** el coste de la
sesión. Viene gratis, es una medida real y no obliga a mantener nada; por eso se guarda y por eso
no se presenta como otra cosa.

Y antes de lanzar con Claude Code, la estimación no promete dinero ni lo desmiente con una nota al
pie: dice que ese consumo no factura a la organización.

### Si algo falta

| Lo que ves en **Cuenta** | Qué pasa | Qué hacer |
| --- | --- | --- |
| «Claude Code · opcional» (en gris) | No lo tienes, y no hace falta | **Nada.** Sigues auditando con Copilot. Instálalo solo si quieres el extra |
| «no has iniciado sesión» | El CLI está, pero sin cuenta | Abre una terminal, ejecuta `claude` y completa el login |
| «tu suscripción ha agotado su cuota» | Se acabaron las peticiones por ahora | Espera al reset, o audita mientras tanto con Copilot desde Ajustes |
| «el modelo no está disponible» | El modelo configurado no sirve | Elige otro en Ajustes |

Un proveedor que no está listo **se ve y se explica**: nunca falla en silencio, y nunca deja una
sesión colgada. Si el proveedor elegido no puede auditar, la sesión **se para antes de gastar** y
te dice por qué.

---

## Directivas del proyecto

Los proyectos desarrollados con IA traen sus propias **convenciones escritas**:
`AGENTS.md`, `CLAUDE.md`, instrucciones de Copilot o de Cursor, ADRs, specs, PRDs,
colecciones de skills. Eso es lo que el equipo ha decidido a conciencia, y sin leerlo
una auditoría reporta como defecto lo que era una decisión, y un arreglo sale correcto
pero escrito con un estilo que no es el de la casa.

**Las directivas viven en el repositorio de cada aplicación**, versionadas con su
código, que es su sitio. Atalaya solo registra **cuáles son**; su contenido se lee de
tu clon local cada vez que se usa, así que siempre viaja la versión vigente. No hay
copia en el hub, y por tanto no hay nada que sincronizar ni nada que se quede viejo.

### Cómo marcarlas

En **Inventario**, panel del ciclo: **Directivas · Gestionar**.

- Al escanear o re-escanear se buscan los sitios donde estos ficheros suelen vivir y
  se **proponen** como candidatos. **Nunca se activan solos**: los marcas tú. Un
  re-escaneo que encuentra ficheros nuevos te lo dice en un aviso, y ahí se queda.
- Cada directiva activa tiene un **ámbito**:
  - **Auditoría** — informa el criterio del auditor y del verificador.
  - **Arreglo** — informa el estilo del arreglo (el prompt y la sesión con agente).
  - **Ambos** — un ADR de arquitectura suele ser esto.
  - Una skill de «cómo escribir specs» quizá no sea ninguno: por eso se decide a mano.
- **Vista previa** enseña el fichero y lo que ocupa en tokens.
- **Añadir a mano** registra cualquier fichero del repositorio por su ruta, aunque
  Atalaya no conozca ese formato.
- Si el fichero desaparece del repositorio, la entrada queda **«no encontrada»**: no
  rompe nada y no viaja. Decide tú si actualizar la ruta o **Retirar** la entrada.

### Qué efecto tienen

- **Auditando**: un patrón que las directivas *mandan* deja de ser un hallazgo aunque
  el checklist lo sugiera. Y el código que **contradice** una directiva sí se reporta,
  con la regla `criterio.directivas` y citando cuál incumple.
- **Arreglando**: el arreglo respeta el estilo. Si el arreglo correcto contradijera una
  convención, el agente **no la atropella**: te lo pregunta en la sesión interactiva, o
  lo declara como riesgo en el prompt que copias al portapapeles.
- **Nunca cambian las reglas de Atalaya.** Informan el criterio; no amplían el ámbito
  de un arreglo, ni las herramientas del agente, ni la guarda de evidencia. Las
  instrucciones de un repositorio dirigidas al modelo no son el encargo, y el prompt se
  lo dice con todas las letras.
- **Atalaya no ejecuta nada.** Una skill es texto que se le enseña al modelo, jamás
  código que se corre.
- El **informe** de cada sesión lista qué directivas viajaron, con el hash de su
  contenido: así se puede saber, meses después, con qué criterio se auditó aquello.

### El presupuesto

Una colección de skills puede pesar más que el código que se está auditando, así que
hay un techo. En el mismo panel: **Presupuesto (tokens)**, por defecto 8.000 y **por
aplicación**.

- El panel enseña lo que consume lo activado, separado por auditoría y por arreglo
  (son prompts distintos).
- Si te pasas, entran por **prioridad** —el número de orden, menor primero— y el
  prompt **declara** las que quedaron fuera. Nada se incluye a medias en silencio.
- Un fichero enorme viaja por su principio, y el prompt dice que está recortado. Lo
  sano es recortarlo en el repositorio.
- **0 apaga las directivas** en esa aplicación.

---

## El coste: qué compone una llamada, y qué puedes hacer

Una auditoría se paga **por tokens**, y casi todos son de **entrada**: en una sesión medida sobre
el banco de pruebas, 628.170 tokens de entrada contra 24.506 de salida. Lo que decide la factura,
por tanto, no es lo que el modelo escribe: es lo que se le manda, y cuántas veces.

### De qué está hecha una llamada

Cada vez que Atalaya habla con el modelo le manda el prompt **entero**, en este orden:

| Bloque | Cambia | Cuánto pesa (orden de magnitud) |
| --- | --- | --- |
| Reglas del auditor y modo | Nunca | ~1.400 tokens |
| Rúbrica de severidad | Nunca | ~700 |
| Catálogo de pilares y áreas | Nunca | ~700 |
| Temática del ciclo | Por ciclo | ~300 (0 en un ciclo General) |
| Directivas del proyecto | Por aplicación | hasta el presupuesto que fijes |
| Tipos de problema silenciados | Por aplicación | unas decenas |
| Hallazgos ya conocidos de la unidad | **Por unidad y por pasada** | decenas o cientos |
| **El código de la unidad** | **Por unidad** | **es lo único que se está auditando** |

A eso se le suma lo que pone el proveedor por su cuenta —sus herramientas, su propio mensaje de
sistema— y la conversación que se va acumulando dentro de la unidad. Para una clase de cuarenta
líneas, **el código auditado es alrededor del 3 % de lo que viaja**. El resto es andamiaje.

El orden no es casual: lo que **nunca cambia** va delante y lo que cambia con la unidad va detrás,
que es la única forma de que la caché del proveedor pueda reutilizar el principio. Hay un test que
impide que algo variable —una ruta, un identificador, el número de pasada— se cuele en ese tramo:
un prefijo contaminado no falla, gasta.

### Dónde se lee

No hace falta calcularlo a mano. El número está en tres sitios:

- **Mientras la sesión corre**, en el pie: «código 2,1 % · 11 llamadas/unidad». Es donde se nota
  que algo se ha disparado; en el informe se lee cuando ya está pagado.
- **En el informe de la sesión**, en una línea bajo los tokens:
  *«andamiaje ≈ 27.900 tokens/llamada · código auditado ≈ 600 (2,1 %) · 11 llamadas por unidad»*,
  más el desglose **pasada a pasada** con lo que aportó cada bloque y cuánto tardó.
- **En Métricas**, bajo el coste del periodo: el reparto por **fase** —descubrimiento,
  verificación y arreglo—, con sus sesiones, sus llamadas, sus tokens y su coste. Es la respuesta a
  «¿en qué se me va el dinero?» sin abrir informes uno a uno.

El informe añade además el diagnóstico de la **caché**: cuánto se escribió, cuánto era inevitable
—una vez el prefijo estable, una vez la parte variable de cada prompt— y cuánto son
**re-escrituras**. Escribir en caché cuesta más que la entrada normal; leerla cuesta una décima
parte. Una cifra de re-escrituras parecida al prefijo multiplicado por el número de prompts
significa que el prefijo se está reescribiendo entero cada vez.

> **Los tokens son el hecho; el coste, un derivado.** Los informes guardan los tokens enteros, así
> que dentro de un año se puede recalcular el coste con otra tarifa a partir de los mismos números.

### Lo que de verdad se paga: escribir caché, no leerla

Un token no cuesta lo mismo según de dónde venga. Con las tarifas de Opus, por millón de tokens:

| | $/M | Frente a la entrada |
| --- | ---: | --- |
| Entrada fresca | 5,00 | × 1 |
| **Escribir en caché** | **6,25** | **× 1,25** |
| Leer de caché | 0,50 | × 0,1 |
| Salida | 25,00 | × 5 |

**Escribir en caché cuesta doce veces leerla.** Por eso los tokens, a secas, engañan: en una sesión
real se leyeron 119.583 y se escribieron 126.904 —números casi iguales— y lo escrito fue el **60 %**
de la factura contra el **5 %** de lo leído.

Por eso el informe y el pie no dicen solo cuánto costó, sino **de qué**:

> **Reparto del coste**: escritura de caché 79,3 (61 %) · salida 45,3 (35 %) · lectura de caché
> 6,0 (4,6 %) · entrada fresca < 0,1

Cómo se lee:

- **Manda la escritura de caché.** Es lo normal cuando cada pasada empieza de cero: todo lo que se
  manda es contenido nuevo para el proveedor, y se paga a 1,25 ×. Es el frente de ahorro. Lo que más
  lo movió fue quitar la llamada de cortesía: **lo que una llamada escribe en caché es, casi todo,
  la respuesta entera de la anterior** —su razonamiento incluido—, así que una vuelta que no aporta
  nada resulta ser la más cara de la pasada.
- **Manda la salida.** Está bien: es donde está el valor, y no se toca.
- **Manda la lectura.** Es la mejor noticia posible: significa que el proveedor está reutilizando
  lo que ya le mandaste, a una décima parte del precio.
- **Manda la entrada fresca.** Es que no hay caché en juego — sesiones muy cortas, o un proveedor
  que no la usa.

### El barrido

Una unidad no se audita de una vez: se **barre**, en varias pasadas, hasta que dos seguidas no
aportan nada. Esas pasadas son ahora **turnos de una misma conversación** con el proveedor: la
primera le manda el prompt entero —las reglas, el catálogo, el código de la unidad y los hallazgos
que ya conocía— y las siguientes le dicen poco más que «sigue, la unidad no está cerrada». No hace
falta repetírselo: lo tiene delante, en la misma conversación. Antes cada pasada era una petición
nueva y el prompt entero volvía a viajar tantas veces como pasadas tuviera la unidad. Medido sobre
el caso de referencia, la conversación cuesta **un 69 % menos por unidad**, tarda tres veces menos y
reporta cuatro veces y media menos hallazgos repetidos con otro nombre.

**Y lo que cuesta se dice igual de claro, porque es una decisión y no un efecto secundario.** Con
el barrido de antes, la unidad de referencia llegaba a **20 de 20** defectos conocidos pagando seis
pasadas; con la conversación se queda en **17-18 de 20**. Un modelo que tiene delante su propia
respuesta anterior se da por terminado antes, y al converger se deja los dos defectos que más tarde
aparecían. Se eligió el ahorro sabiendo eso: un tercio del coste y cero variantes valen esas dos
medias por unidad, y la decisión es del responsable de Atalaya, con las dos cifras delante. Si la
conversación se rompe a mitad —el proveedor no puede continuarla, o crece tanto que no cabe— la
unidad **no se pierde**: la pasada siguiente empieza otra desde cero, con el prompt entero, y el
anexo técnico del informe dice cuántas veces pasó y por qué.

### Cuántas llamadas hace falta, y por qué

El gasto no escala con el tamaño de la unidad: escala con las **llamadas**. Cada una reenvía el
prompt entero —el sistema del proveedor, el brief, los hallazgos conocidos y el código—, así que
una llamada de más cuesta casi lo mismo que auditar la unidad otra vez.

Una pasada sana es **una llamada**: el auditor manda de una vez sus veredictos sobre lo conocido,
los hallazgos nuevos, las ubicaciones que añade y el cierre de la unidad. Se convierte en **dos**
cuando además pide las firmas de una dependencia (`read_signatures`), que es su única lectura extra.

**Antes eran dos y tres.** Había una segunda llamada **de cortesía**: después de recibir la
respuesta de una herramienta, el modelo tenía que contestar algo, y aunque se le pedía que no dijera
nada, gastaba la vuelta igual — con el prompt entero dentro. No era un capricho del modelo: era el
CLI, que no manda el turno siguiente hasta tener contestadas todas las herramientas del turno.

Ahora Atalaya **cierra la pasada en `unit_done`**, que es el momento en que el auditor ya ha
entregado todo. Y lo hace sin perder una sola cifra de consumo, que era lo que lo impedía: el CLI
publica el gasto de cada llamada según ocurre y se le pide que se interrumpa —no se le mata—, así
que sigue emitiendo su cuenta final completa. **Si por lo que sea las cuentas no estuvieran, la
pasada NO se corta**: termina como siempre, paga su llamada y el informe lo dice con esas palabras
(«no se pudo cerrar la pasada en unit_done —motivo—, así que costó una llamada de cortesía más»).
Un ahorro pagado con un número falso no sería un ahorro.

Con Copilot esto no cambia nada, porque allí nunca existió: su SDK admite declarar una herramienta
como **terminal**, y `unit_done` lo es desde el primer día, así que el turno acaba ahí sin vuelta de
cortesía.

Si en el desglose por pasada del informe ves muchas más, algo va mal: o el modelo está dando
vueltas, o está soltando los hallazgos de uno en uno en vez de agruparlos. Para eso está el techo.

Y si ves **«el auditor no llamó a ninguna herramienta»**, esa pasada se gastó sin entregar nada: ni
hallazgos, ni veredictos, ni cierre. No cuenta como pasada seca —así que el barrido sigue en vez de
darse por terminado—, pero es gasto sin trabajo y por eso aparece nombrada.

### El techo de llamadas

`maxCallsPerPass` en el `app.json` de la aplicación, junto a `maxTokensPerUnit`. **Por defecto 12**,
que con una pasada sana en una o dos llamadas es holgura de sobra: no molesta a quien trabaja bien y
corta un bucle a tiempo.

Si salta, la unidad se cierra como **presupuesto superado** y el informe dice **cuál** de los dos
techos fue —«5/4 llamadas en una pasada» o «500000/300000 tokens»—, porque los dos tienen remedios
distintos: uno es un agente dando vueltas, el otro es una unidad demasiado grande. **0 lo
desactiva** y deja el de tokens como única red.

### Las palancas que tienes

En orden de cuánto mueven la aguja:

1. **Cuántas unidades auditas.** El coste escala con las unidades, no con su tamaño: una clase de
   cuarenta líneas cuesta casi lo mismo que una de cuatrocientas, porque el andamiaje es el mismo.
   Auditar por lotes lo que de verdad ha cambiado —y usar la **deriva** en vez de re-auditar todo—
   es lo que más ahorra.
2. **El tope de pasadas del barrido** (Ajustes). Sigue siendo un multiplicador del gasto por
   unidad, aunque desde que las pasadas son turnos de una conversación mueve mucho menos que antes:
   lo que una pasada de más añade ya no es el prompt entero. **Bajarlo ahorra poco y cuesta
   mañana**: está comprobado que hay hallazgos reales en la 4.ª y la 5.ª pasada, así que recortarlo
   no elimina trabajo, lo aplaza. Tócalo sabiendo eso.
3. **El presupuesto de directivas** (Inventario → Directivas). Va en el prefijo estable de *todas*
   las llamadas de la sesión: 8.000 tokens de directivas son 8.000 tokens en cada una. Ponerlo a 0
   las apaga.
4. **El modelo** (Ajustes). La tarifa por token cambia mucho de un modelo a otro, y el coste de
   cada sesión se calcula con la tarifa **del suyo**.
5. **Excluir del inventario** lo que no aporta: generado, migraciones, ficheros de recursos. Cada
   unidad del inventario es una factura potencial.

Lo que **no** tienes que hacer es apretar las llamadas a mano: el prompt ya le pide al auditor que
entregue todo en un solo turno, y el techo de arriba está para cuando no obedezca.

### Lo que NO es una palanca

- **Pedirle brevedad al modelo.** La salida es el 4 % del gasto y es donde está todo el valor.
- **Mandar menos código.** El código auditado es el 3 %: recortarlo no ahorra nada apreciable y
  degrada la auditoría.
- **Colocar la caché tú.** El orden ya está puesto y probado; dónde corta su caché lo decide el
  proveedor, y se comprobó midiendo que con el CLI de Claude Code no hay forma de pedírselo.
- **Bajar el techo de llamadas para ahorrar.** No es un presupuesto, es una alarma. Puesto por
  debajo de lo que necesita una pasada sana, lo que hace no es gastar menos: es cortar auditorías
  a medias y dejarlas marcadas como superadas.

---

## Si una sesión falla: qué significa cada error

Cuando Copilot rechaza una sesión, Atalaya la deja en un estado terminal **visible** —con
su causa, el reloj parado y sin nada corriendo por detrás— y enseña un aviso en su propia
franja, encima del cuerpo de la sesión y sin taparlo. El texto se puede **seleccionar y copiar**, y
**Copiar error** se lleva al portapapeles el mensaje junto con el error crudo del
proveedor, que es lo que hay que pegar en un correo a quien administre la organización.

| Si ves esto | Significa | Se arregla así |
|---|---|---|
| **La organización ha agotado sus AI credits de Copilot** | Vuestro plan se ha quedado sin credits. **No es tu asiento ni tus credenciales**: los dos siguen bien. | Nada que tocar en Atalaya: esperar a que se renueve la cuota, o auditar con un modelo de tarifa menor. Atalaya **no reintenta sola** — reintentar contra una cuota agotada gasta los credits del reset siguiente. |
| **Tu cuenta no tiene asiento de Copilot asignado** | La licencia no está: nadie te la ha dado, o te la han quitado. | Pedírsela a quien administre la organización (github.com/settings/copilot). |
| **GitHub ha rechazado tus credenciales** | El token está revocado, caducado o su SSO expiró. | **Cuenta → Conectar con GitHub**. El mismo login habilita el hub y tu asiento. |
| **El modelo «X» no está disponible para tu cuenta** | GitHub retiró ese modelo, o tu plan no lo sirve. | **Elegir modelo en Ajustes**, que es el botón del propio aviso. |
| **No hay conexión con GitHub** | Red, proxy o servicio caído. Es lo único **transitorio** de esta tabla. | Comprobar la red y reintentar. |
| **Atalaya no reconoce el motivo** | El proveedor ha devuelto algo que Atalaya no sabe clasificar. | El aviso trae el **error crudo íntegro** con su *Request ID*: **Ver detalle** lo despliega y **Copiar error** lo copia. Ante la duda se enseña el dato, nunca una causa inventada. |

> **Por qué la primera fila existe.** Hasta el 2026-08-31, agotar la cuota de la
> organización se leía en rojo como «tu cuenta no tiene asiento en GitHub». Las dos cosas
> impiden auditar, pero **el remedio es opuesto**: una se resuelve esperando y la otra
> hablando con quien administra. Un diagnóstico equivocado no es medio diagnóstico: manda
> a reclamar algo que ya se tiene.

**Si el corte llega a mitad de un barrido**, lo auditado hasta ahí **no se tira**: los
hallazgos ya remitidos siguen en el hub, las unidades cubiertas quedan marcadas como
auditadas, y la sesión se registra con su informe y una nota que dice quién la cortó y por
qué. El resumen de cierre lo cuenta —«Se auditaron 1 de 3 unidad(es) antes del corte»— y
las que no se llegaron a mirar siguen pendientes para la próxima. Una sesión así **no
cierra ciclo**: no cubrió lo que decía cubrir.

Y no se prueba ni una unidad más contra un grifo cerrado: al primer corte por cuota el
barrido para.

### Cerrar una pantalla terminada

Una sesión o un arreglo que ya ha terminado —bien o mal— lleva **«Cerrar»** en su cabecera.
Archiva la pantalla: su entrada desaparece del menú lateral y vuelves al Portafolio (en el
arreglo, a la ficha del hallazgo del que saliste).

- **Cerrar no borra nada.** La sesión, sus hallazgos y su informe siguen en el hub; el
  informe se lee en **Informes**, como todos. Lo único que se va es la pantalla.
- **Cerrar no es descartar.** Si el agente había dejado ficheros modificados en tu clon,
  se te pregunta: **conservarlos** (lo normal — son tuyos y tu árbol es tuyo; a partir de
  ahí Atalaya deja de ofrecerse a revertirlos), **descartarlos** primero, o **cancelar**.
  Si no se tocó nada, cierra directo y sin preguntas.
- **Mientras la sesión corre no hay «Cerrar»**: ahí lo que hay es **«Detener»**, que es
  otra cosa. Archivar una sesión viva la dejaría corriendo sin ninguna pantalla que la
  enseñe.

### «Auditando ahora», y cuándo deja de decirse

La tarjeta del Portafolio dice «auditando ahora» mientras haya **claims vivos** sobre esa
aplicación. Los claims son lo que evita que dos personas auditen la misma clase a la vez, y
son lo único que ve el equipo entero.

Se sueltan **en cuanto la sesión termina**, sea como sea: completada, detenida o fallida.
Si la aplicación se cierra de golpe a mitad, se sueltan **al volver a abrirla**, y la sesión
queda registrada como interrumpida con su informe.

Si el claim es de **otra máquina**, Atalaya no lo toca —no sabe si esa persona sigue
auditando—, pero tampoco se lo cree para siempre: pasados **30 minutos** sin refrescarse, la
tarjeta deja de anunciar actividad. A alguien se le cierra el portátil a mitad de sesión y su
tarjeta no puede estar mintiéndole al equipo el resto del día. Mejor no decir nada que mentir.


---

## Cosas que conviene saber

**Si Atalaya no arranca.** Ya no se muere en silencio: dice qué ha fallado, en una ventana, y lo
deja escrito en `%LOCALAPPDATA%\Atalaya\logs`. Eso es lo que hay que pegar al contarlo. Y si
quieres comprobarlo tú antes de reportar nada, desde una consola en la carpeta de Atalaya:

```
Atalaya.exe --selfcheck
```

hace el arranque entero **sin abrir ventana** y enumera lo que comprueba, con «Arranca» o «NO
ARRANCA» al final. Es el mismo chequeo que corre la fábrica sobre cada paquete antes de publicarlo,
así que una versión publicada no debería poder fallarlo — si lo falla, es un defecto y ese parte lo
describe entero.

**Actualizar Atalaya.** Al arrancar, Atalaya pregunta a GitHub si hay una versión más reciente
que la tuya —sin retrasar nada y con el token de la cuenta que ya tienes conectada—. Publicada una
versión nueva, **reiniciar Atalaya basta para verla**. Si abres y cierras varias veces seguidas no
vuelve a preguntar: hay un mínimo de **15 minutos** entre consultas. Y si la dejas abierta días,
mira otra vez cada 24 h. Si la hay, aparece un **banner discreto** encima de la página que dice
las dos versiones —«**Tienes la 1.0.3 · disponible la 1.0.4**»— y ofrece tres cosas:

- **Actualizar a X.Y.Z** — descarga la versión nueva, comprueba que llegó entera, sustituye la
  carpeta y vuelve a abrir Atalaya ya actualizada. Ves la descarga avanzar mientras pasa.
- **Ver novedades** abre la página de la versión en el navegador, con sus notas y su zip.
- **Descartar** lo quita. No vuelve a avisar de esa versión; de la siguiente sí.

**Nunca se actualiza sola.** No descarga nada por su cuenta, no instala al arrancar y no «se
actualizará al cerrar». Pasa cuando pulsas el botón, y no antes.

**Qué pasa exactamente al pulsar.** Se descarga el zip de la Release, se comprueba su
**checksum SHA-256** —el que publica el propio workflow junto al paquete—, se descomprime aparte,
y solo entonces Atalaya se cierra para que un programa auxiliar sustituya la carpeta y la vuelva
a abrir. Verás una ventana de consola durante unos segundos: es ese relevo, y va diciendo lo que
hace. **Hasta que el paquete no está descargado y verificado no se toca nada** de tu instalación.

**Tus datos no se tocan, nunca.** Los ajustes, tu cuenta, los clones vinculados y el hub viven en
`%LOCALAPPDATA%\Atalaya`, fuera de la carpeta de la aplicación. Lo que sí vive dentro y también
se conserva es el `appsettings.deploy.json` de tu instalación, si alguien lo editó: la
actualización lo devuelve a su sitio en vez de pisarlo con el de fábrica. Si una versión nueva
trae ajustes nuevos en ese fichero, quien preparó la instalación tendrá que añadirlos a mano.

**La versión anterior se guarda** en `.atalaya-anterior`, dentro de la carpeta, y **no se borra
hasta que la nueva arranca bien**. Si la sustitución falla a mitad, se deshace sola y sigues con
la de antes, entera, y se te dice qué pasó. Y si la nueva se instalara pero no llegara a arrancar,
esa carpeta es la vuelta atrás: devuelve su contenido a la carpeta principal.

**Si Atalaya vive dentro de OneDrive** —o de Dropbox, o de Google Drive— el banner te lo dice
antes de que pulses nada. No impide actualizar, y la mayoría de los días saldrá bien: el aviso
está porque, cuando falla, el motivo es siempre el mismo. El cliente de sincronización mantiene
abiertos los ficheros mientras los sube, y la actualización mueve la carpeta entera. Atalaya
**insiste unos segundos** en cada movimiento —esos bloqueos se sueltan solos— y solo entonces se
rinde. Si se rinde, el mensaje te dice qué hacer: **pausa la sincronización y reintenta**, o mueve
Atalaya a una carpeta que no se sincronice (`C:\Apps\Atalaya`), que es la solución definitiva.
Verás también nombres como `.atalaya-anterior-2`: son copias de un intento anterior que el cliente
de sincronización no dejó borrar; Atalaya las esquiva para no bloquear la actualización y las
retira sola en cuanto pueda, en algún arranque posterior.

**Cuándo NO aparece el botón** —y el banner dice cuál de éstas es—:

- **Hay una sesión en curso**: auditoría, verificación o arreglo asistido. Actualizar la cortaría,
  y eso tira trabajo ya pagado a Copilot. Termina o detén la sesión y el botón vuelve.
- **Es un build local**, de los que en «Acerca de» aparecen como `· build local`. Ésos se
  actualizan recompilando. El aviso **sí sale** —es útil saber que hubo release— pero es
  informativo: enseña las dos versiones y dice por qué no hay botón.
- **No hay cuenta conectada**, o el despliegue no declara `appRepoUrl`.
- **Falta `AtalayaUpdater.exe`** en tu carpeta: un paquete incompleto no puede sustituirse solo.

Si tienes un **arreglo abierto**, el botón sí aparece, con un aviso: sus cambios están en el clon,
fuera de la carpeta de la aplicación, y la actualización no los toca.

**Cuando algo falla** se dice qué fue, y te queda el camino de siempre (**Ver novedades** →
descargar el zip a mano):

| Qué pasa | Qué verás |
|---|---|
| Sin red, o con el proxy cortando | «No hay conexión con github.com…» |
| Sin permiso de escritura en la carpeta (típico bajo `Archivos de programa`) | «No se puede escribir en la carpeta de Atalaya…», con la ruta |
| La descarga llegó corrupta o a medias | «El paquete descargado no coincide con su checksum… No se ha modificado nada.» |
| La Release no publica checksum (las anteriores a esta versión) | «…no se puede verificar lo descargado. Descárgala a mano si te fías de ella.» |
| Un antivirus retiene el ejecutable | «El ejecutable de Atalaya sigue bloqueado por otro programa…» |
| **Atalaya está dentro de OneDrive/Dropbox/Google Drive y la sincronización retiene ficheros** | «…no se ha modificado nada. Atalaya está dentro de OneDrive…: **pausa la sincronización y reintenta**, o mueve Atalaya a una carpeta no sincronizada (por ejemplo `C:\Apps\Atalaya`)» |

Cada intento queda anotado en `%LOCALAPPDATA%\Atalaya\updates.jsonl` —de qué versión, a cuál,
cómo acabó y por qué si falló—, que es lo que hay que mirar cuando alguien pregunta por qué sigue
en la versión de antes.

Si el **chequeo** de versión falla (sin red, token caducado, GitHub caído), no pasa nada: queda
anotado en el log y no se enseña nada. Un chequeo de cortesía no puede molestar por fallar.

La versión que tienes está en **Ajustes → Acerca de Atalaya**.

**Arreglar con agente.** Desde la ficha de un hallazgo, junto al generador de prompt.
El agente arregla el hallazgo directamente sobre tu clon local, y ahí se acaba su
alcance: **no tiene consola, ni git, ni red**. Solo puede leer ficheros del clon,
modificarlos por una herramienta que la aplicación controla, y pedir que se compile —
lo ejecuta Atalaya, no él.

Funciona igual **con cualquiera de los dos proveedores**: las mismas cuatro herramientas, las
mismas preguntas, el mismo diff y los mismos frenos. Con Claude Code, además, el CLI arranca
**sin ninguna de sus herramientas propias** y sin poder conceder permisos por su cuenta: el
único permiso que existe —tocar un fichero que no es del hallazgo— lo decides tú, y te lo
pregunta Atalaya.

Antes de arrancar se comprueban cuatro cosas, y si falla alguna se dice cuál:

1. Que tengas el **clon vinculado** de esa aplicación.
2. Que tu **árbol de trabajo esté limpio** — sin cambios sin commitear. Sin excepciones:
   es lo único que permite después distinguir lo que tocó el agente de lo tuyo, y por
   tanto lo que hace que «Descartar todo» sea seguro.
3. Que no haya **otra sesión del agente** corriendo (auditoría o arreglo), sea de la casa que sea.
4. Que el **arreglo asistido** esté activado en Ajustes.

Durante la sesión:

- El agente **explica cada paso antes de darlo**.
- Sobre los ficheros del hallazgo (y sus tests) edita directamente. Para tocar
  **cualquier otro** —un llamador que hay que adaptar— te pide permiso, fichero a
  fichero, diciendo por qué. Puedes negarte: el agente tendrá que replantearlo.
- Si el arreglo exige **cambiar el contrato** del código, no decide: te presenta las
  opciones con su consecuencia sobre los llamadores y espera tu elección.
- Cada fichero tocado se guarda antes de tocarlo, fuera del clon. Eso es lo que
  «Descartar todo» restaura.
- **Los tests los busca Atalaya, no el agente.** Antes de arrancar, la aplicación mira si el
  proyecto afectado tiene proyecto de tests y se lo dice al agente en una línea: dónde están, o
  que no hay y que no los busque ni los escriba. Muchos proyectos de la casa no tienen tests, y
  que no los haya es un hecho del repositorio —se dice una vez, en el informe— y no una carencia
  de tu arreglo.
- **Compilar mide tu cambio, no la solución entera.** Cuando el agente pide compilar se
  compila el **proyecto** de los ficheros tocados y sus tests: es más rápido y, sobre
  todo, el veredicto pertenece al cambio. Si marcas **«Compilar solución completa»** se
  compila todo, y entonces los errores se cuentan contra una **línea base** del mismo
  commit sin tocar: el resultado se lee como «0 errores nuevos · 18 preexistentes», con
  los heredados listados aparte. Los proyectos que `dotnet` no puede compilar —C++ y
  compañía— se nombran y quedan fuera del veredicto, en vez de contarse como fallo. En
  una solución legacy que ya no compilaba, esto es la diferencia entre un rojo prestado y
  un veredicto que se puede creer.

Al terminar: el resumen de qué cambió y por qué, los ficheros con su diff, el resultado
del build —con qué se compiló y cuántos errores son nuevos—, y una **sugerencia de
commit** —título y descripción, editables, con botón de copiar—. **Atalaya no
commitea**: solo te ahorra redactarlo.

Cuando el agente cierra, la pantalla de cierre **aterriza sola**: no hay que buscarla con la
rueda, y la tarjeta de sugerencia de commit se alcanza bajando por el panel sin que ningún
recuadro interior te robe el scroll.

Y hay **camino de vuelta al hallazgo**: desde la propia sesión («Volver al hallazgo»),
desde el informe del arreglo en Informes, y al revés — en el historial de la ficha, el
evento «arreglo propuesto» abre el informe de ese arreglo.

**Arreglar no resuelve el hallazgo.** Al terminar sigue activo. La aplicación te sugiere
**Verificar ahora** cuando des el cambio por bueno, y la resolución llega por la vía de
siempre: con evidencia.

**La sincronización es automática.** Un indicador permanente abajo dice cómo va:
verde al día, ámbar sin publicar (trabajas sin conexión y no se pierde nada), rojo con
un problema que hay que mirar. Lo que publiquen tus compañeros aparece solo.

**Nada se borra.** Los hallazgos se mueven entre estados —activo, resuelto,
silenciado— y su historial conserva todo lo que les pasó, incluidas las reaperturas.

**En el hub solo hay datos primarios.** Los paneles y las gráficas se calculan en cada
carga; no hay ningún fichero de agregados que pueda quedarse desfasado.

**Cada dato dice de dónde sale.** Si una fecha es la de una sesión, la de la cabecera
de un fichero o la del propio fichero en tu clon, el tooltip lo dice. Cuando algo no
se sabe, se declara en vez de rellenarse.
