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

   > **La primera vez, Windows avisa.** «Windows protegió su PC»: el ejecutable no está firmado
   > todavía. Pulsa **Más información** → **Ejecutar de todas formas**. Solo pasa la primera vez.

   Para **actualizar**: cierra Atalaya, descarga el zip nuevo y reemplaza la carpeta. Tus datos no
   están ahí —viven en `%LOCALAPPDATA%\Atalaya` y en el hub—, así que no se pierde nada.

2. **Cuenta.** La primera vez aterrizas aquí. Pulsa **Conectar con GitHub**, escribe
   el código que te muestra en `github.com/login/device` y autoriza. Ese login sirve
   para las tres cosas: acceso git al hub, autenticación de Copilot y autoría de los
   commits. No hay PAT que pegar ni URL que escribir.
3. **Nueva aplicación.** Da de alta el repositorio que vas a auditar. El asistente
   escanea el clon, arma el inventario y, si encuentra un baseline v4 (`CodeAudit/`),
   te ofrece importarlo.
4. **Inventario → Auditar selección.** Elige unidades y lanza. El progreso se sigue
   en **Sesión en vivo**.

---

## Las vistas

### Portafolio

La portada: una tarjeta por aplicación con su progreso del ciclo, sus hallazgos
activos por severidad y el estado de su clon local. Un clic entra al inventario.

El piloto de vinculación dice si el clon de esa app está donde debería: verde
vinculado, ámbar con avisos, rojo sin clon. Sin clon no se puede auditar ni medir.

### Inventario

Las unidades de la aplicación en el ciclo vigente, por módulos, con su estado
—**pendiente**, **auditada** o **grande**— y el panel lateral del ciclo.

- Cada unidad lleva a su izquierda una **franja de densidad de deuda**, con la misma
  rampa que el **Mapa de calor**: aquí es donde se decide qué auditar, así que se ve de
  un vistazo cuánto arde ya cada fichero. Gris = nadie la ha auditado todavía.
- **Grande** significa que la unidad supera el umbral de tamaño (LOC o caracteres):
  queda excluida del ciclo y genera su propio hallazgo.
- **Auditar selección** lanza una sesión sobre lo marcado.
- **Re-escanear** vuelve a medir el clon: actualiza el inventario **y** los hallazgos
  medidos en el mismo gesto, y cuenta en un aviso qué cambió.
- **Reiniciar ciclo** abre uno nuevo sin borrar nada.
- El panel del ciclo lleva **Patrones silenciados** y **Directivas** con su
  «Gestionar» al lado: las dos cosas que condicionan qué se reporta en esta
  aplicación (ver «Directivas del proyecto», más abajo).

### Hallazgos

La lista de todo lo detectado, agrupada por fichero. **La lista encuentra; la ficha
actúa**: aquí no se silencia, ni se asigna, ni se resuelve.

Filtros: búsqueda de texto, aplicación, severidad, estado (activos / resueltos /
silenciados / todos) y dos interruptores — **Por revisar** y **Disputados**. Todos los
combos arrancan en «Todas/Todos» y **Limpiar filtros** los devuelve ahí.

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
- **Verificar ahora** vuelve a preguntar al auditor si el defecto sigue ahí.
- Un arreglo se confirma **juzgando el código que hay ahora**, no buscando el código
  viejo: que el fragmento auditado haya desaparecido es lo que pasa cuando algo se
  arregla, así que si el método sigue ahí se le enseña al auditor tal y como está hoy.
  Solo se dice «no localizado» cuando no queda nada que juzgar — ni el fragmento, ni el
  método, ni una unidad que haya cambiado.
- Resolver sigue exigiendo **evidencia de cambio**: si la unidad es la misma que la
  última vez que se vio el hallazgo, un «arreglado» se degrada a «presente».
- El aviso dice siempre **qué ha pasado** — el veredicto, o la causa concreta si no se
  pudo verificar.
- La franja de encima del código **depende del estado**: en los activos avisa en ámbar
  de la deriva sin verificar y trae la acción; en los resueltos dice que el arreglo está;
  en los silenciados no dice nada.
- En los hallazgos de tamaño el botón dice **Medir ahora**: los cuenta la aplicación
  leyendo el fichero, sin consultar al modelo y sin gastar tokens.

### Sesión en vivo · Última sesión

Aparece en el menú solo cuando hay una sesión (en curso o recién terminada), y el
punto late mientras corre. Enseña el progreso unidad a unidad, los hallazgos según
van llegando, el coste y los tokens consumidos, y al terminar el resumen de cierre.

Si la sesión falló, lo dice con el motivo y el atajo para arreglarlo. **Ver informe de
sesión** abre el informe en la vista **Informes**.

### Arreglo asistido

Aparece en el menú solo cuando hay un arreglo (en curso, terminado, o con cambios que
todavía puedes descartar), y el punto late mientras el agente escribe.

Dos paneles: la **conversación** —lo que el agente va explicando, y las preguntas
como tarjetas con sus opciones— y el **diff**, una pestaña por fichero tocado, que
compara con lo que había antes de empezar. Abajo: tiempo, coste, ficheros tocados y
el resultado del último build.

- **Pausar** no congela al agente —eso no se puede prometer— sino lo que importa: no
  cae ni un cambio más en tu clon ni se compila nada hasta que continúes.
- **Descartar todo** devuelve cada fichero tocado a como estaba, byte a byte, con
  confirmación previa.
- **Detener** para al agente; lo que ya haya aplicado se queda.
- El campo de entrada de abajo está **siempre** disponible: escribe y pulsa Enter para
  dirigirle («no toques ese fichero», «prefiero TryParse»). Si está a mitad de un paso,
  el mensaje se le entrega al empezar el siguiente, y la aplicación te lo dice.

Si cierras Atalaya con un arreglo en curso, se detiene ordenadamente y **los cambios
se quedan** en tu clon: la próxima vez que abras, esta pantalla te ofrece descartarlos.

### Métricas

El panel de mando, filtrable por **aplicación** y **periodo** — y los dos filtros
afectan a todo lo de abajo.

Cuatro cifras arriba: hallazgos activos por severidad, resueltos en el periodo (con su
delta), coste del periodo y cobertura del ciclo.

El **coste del periodo** incluye **todas** las sesiones que gastaron: auditorías,
arreglos asistidos y verificaciones. El «por unidad auditada» que va debajo divide solo
lo que costó **auditar** entre las unidades auditadas — un arreglo no audita ninguna
unidad, así que repartir su gasto entre ellas daría un número que no significa nada.

Debajo, seis gráficas:

1. **Coste en el tiempo** — una línea por aplicación, con toggle *Acumulado*.
2. **Resoluciones en el tiempo** — cuántos hallazgos se dieron por resueltos en cada
   tramo, por aplicación, contando todas las vías (veredicto del auditor, resolución
   manual y medida). Misma forma y **mismo color por aplicación** que la de coste, con
   su propio toggle *Acumulado*.
3. **Cobertura por aplicación** — un rosco por app; un clic abre su inventario.
4. **Severidad por aplicación** — un rosco por app con el reparto de su deuda **viva**
   (hallazgos activos a día de hoy: el periodo no la recorta). Un clic en un tramo abre
   Hallazgos con esa app y esa severidad; en el centro, esa app entera.
5. **Flujo de hallazgos** — lo que entra, lo que se cierra y cuántos quedan vivos.
6. **Actividad de sesiones** — el registro del periodo, con el **tipo** de cada sesión
   (auditoría, arreglo asistido, verificación, cierre…) y su coste. **Un clic en una
   línea abre su informe en la vista Informes.**

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

### Mapa de calor

Dos niveles. El de arriba contesta **¿por dónde miro ahora?**; el de dentro, **qué
tiene ese módulo**.

**El termómetro.** Sobre todo lo demás, una barra con la aplicación entera repartida:
cada tramo es un paso de la escala de densidad y el último, gris, es lo que **nadie ha
auditado todavía**. Al lado, las cifras («925 unidades · 2 auditadas (0 %) · 107 de
deuda conocida»). Cuenta siempre el total, tengas puesto el filtro o no.

#### Nivel 1 · las tarjetas de módulo

Una tarjeta por módulo. Cada una lleva:

- El **nombre**, sin el nombre de la aplicación (`XBLASTCore` se rotula `Core`; los que
  no lo llevan salen enteros).
- El **tamaño**: unidades y miles de líneas.
- Una **barra de cobertura** con su porcentaje: cuánto se ha auditado y cuánto no. Aquí
  es donde se ve lo que falta por mirar, con un número, sin que invada el resto.
- La **densidad de lo auditado**, en la franja de color del borde izquierdo. Gris si de
  ese módulo no se ha auditado nada: **desconocida no es cero**.
- Una **tira de severidades** C/A/M/B con sus colores de siempre y el total.

Un clic entra al módulo. El tooltip lo cuenta todo, incluido **por qué está donde está**
en el orden.

**El orden — «Atención», que es el de por defecto.** Junta lo que se ha medido que arde
con lo que nadie ha mirado:

> `atención = 0,6 × riesgo + 0,4 × ignorancia`, donde el **riesgo** es la densidad medida
> (saturada en 100 por KLOC) rebajada por la **confianza** —que va de la mitad, si solo
> se ha mirado una esquina, al total, si el módulo está auditado entero— y la
> **ignorancia** es la parte del código de la aplicación que ese módulo esconde sin
> auditar.

En corto: **un módulo grande que nadie ha abierto sube**, porque no saber es un riesgo; y
**un módulo pequeño y comprobadamente podrido también**, porque eso es un hecho. Un
módulo auditado del todo y limpio se va al fondo, que es donde tiene que estar.

Los otros órdenes están para cuando ya tienes una pregunta: **Deuda**, **Densidad** (los
módulos sin auditar van al final: no tienen densidad), **Tamaño**, **Cobertura** y
**Nombre**.

#### Nivel 2 · el treemap del módulo

Dentro de un módulo, sus unidades como treemap: el **área** es el tamaño en líneas y el
**color**, la densidad de deuda. Las migas de arriba devuelven a las tarjetas.

- Las unidades sin auditar van en **gris liso** — la trama se queda en la leyenda, que es
  donde distingue algo; novecientas celdas rayadas son textura, no información.
- **Las etiquetas caben o no están.** El nombre se escribe entero; si no cabe, acortado
  por el medio (`Controller…ration.cs`); y si tampoco, no se escribe y lo dice el
  tooltip. Nunca verás media palabra.
- **«+N unidades»**: las que no llegarían a verse se funden en una celda con su cuenta.
  Toma el color de **la peor** que contiene, y si le queda alguna sin auditar va en
  **gris**: un grupo del que falta por mirar la mayoría no se pinta de limpio.
- **Un clic** en una unidad abre sus hallazgos; un **doble clic** (o el botón «Auditar»
  de la tabla) abre el Inventario con esa unidad marcada. El mapa dice **dónde**; lanzar
  y confirmar el gasto sigue siendo del Inventario.

#### Lo demás

- **Solo auditadas** esconde lo que nadie ha mirado y deja ver el mapa de lo que se sabe.
  Con poca cobertura es la única forma de que la densidad cuente algo. El termómetro
  sigue contando la aplicación entera.
- **Color por: Densidad / Deuda absoluta** son dos preguntas distintas —«dónde están más
  concentrados los problemas» y «dónde hay más»—. Por defecto, densidad.
- **Ver como tabla** enseña lo mismo en columnas ordenables (módulo, unidad, LOC,
  C/A/M/B, deuda, densidad, estado). El color **nunca** es el único canal. Cada fila lleva
  a su izquierda la misma franja que el mapa, los números van a la derecha y lo que no
  cabe se acorta por el medio con su tooltip. La tabla desplaza ella sola, con la cabecera
  fija.
- **Exportar imagen** guarda un PNG de lo que estés viendo: las tarjetas si estás en el
  nivel 1, el treemap del módulo si has entrado en uno. Con su termómetro, su leyenda, el
  título («{App} · mapa de calor · {fecha}») y el pie «Atalaya · {organización}». En la
  lámina los módulos salen con su **nombre completo**: quien la reciba no ha visto la
  nota de la cabecera.

**Cómo se lee la escala.** Cinco pasos con sus umbrales en la leyenda: **< 5**, **5–15**,
**15–40**, **40–100** y **≥ 100** por KLOC. Son **fijos**, no salen de los datos de cada
app: así «paso 4» significa lo mismo en todas partes y también dentro de tres meses.

La rampa va de **violeta a ámbar** pasando por magenta y coral. No es un arcoíris: lo que
ordena una escala es que la **claridad** crezca sin volver atrás, y aquí lo hace paso a
paso — por eso se distingue de un vistazo y sigue funcionando en una fotocopia en blanco
y negro. Cada tema tiene su rampa; en los dos, **cuanto más lejos del fondo, más deuda**.
Los colores de **severidad** siguen siendo suyos: se escriben en píldoras con texto, y la
rampa es solo relleno.

> **Gris = NO auditado, no limpio.** Una unidad que nadie ha barrido **no tiene densidad
> 0: tiene densidad desconocida**. Confundir «no lo he mirado» con «está limpio»
> convertiría este mapa en una mentira tranquilizadora — y es justo lo que más se va a
> mirar en una aplicación recién dada de alta. «Excluida por tamaño» también es gris:
> estar excluida es un motivo para **no** auditarla, no una forma de haberla auditado.

**Qué mide.** Cada hallazgo **activo** pesa según su severidad —**Crítica 10 · Alta 5 ·
Media 2 · Baja 1**— y la **deuda** de una unidad es la suma de esos pesos. Los resueltos
y los silenciados no cuentan. La **densidad** es esa deuda por cada **mil líneas**: sin
normalizar, la clase de 5.000 líneas saldría siempre la peor por ser grande, y eso ya lo
dice el área. Un módulo divide **solo lo auditado entre lo auditado** —meter lo que nadie
ha mirado en el denominador dejaría «casi limpio» a un módulo con una unidad podrida y
noventa sin tocar—, y por eso la cobertura va siempre pegada al número.

El mapa es de **una** aplicación cada vez.

### Informes

Todo lo que las auditorías han dejado escrito: los informes de sesión, los
consolidados de cierre de ciclo y los de operaciones. Solo aparece lo que tiene
informe de verdad en el hub.

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

### Nueva aplicación

El asistente de alta: repositorio, clon local, escaneo inicial y —si procede—
importación del baseline v4. Publica una sola vez, al final.

### Cuenta

El estado de la conexión: quién eres, si GitHub acepta tus credenciales, si el hub
está clonado y si Copilot responde. Es el sitio al que te manda cualquier fallo de
conexión.

### Ajustes

Umbrales (tamaño de unidad, frescura), modelo de Copilot, el interruptor del
**arreglo asistido** (encendido por defecto), tema **claro/oscuro**,
intervalo de sincronización y las acciones destructivas, con su confirmación. Al final,
**Acerca de Atalaya**: versión, organización y los enlaces al repositorio y a este manual.

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

## Cosas que conviene saber

**Aviso de versión nueva.** Al arrancar, Atalaya pregunta a GitHub si hay una versión más
reciente que la tuya —como mucho una vez al día, sin retrasar nada y con el token de la cuenta
que ya tienes conectada—. Si la hay, aparece un **banner discreto** encima de la página:

- **Ver novedades** abre la página de la versión en el navegador, con sus notas y su zip.
- **Descartar** lo quita. No vuelve a avisar de esa versión; de la siguiente sí.

**Atalaya no se actualiza sola** y no descarga nada por su cuenta: cierras, descargas el zip y
reemplazas la carpeta. Tus datos no están ahí, así que no se pierde nada.

Si no hay red, si el token ya no vale o si GitHub no contesta, no pasa nada: queda anotado en el
log y no se enseña nada. Un chequeo de cortesía no puede molestar por fallar.

La versión que tienes está en **Ajustes → Acerca de Atalaya**.

**Arreglar con agente.** Desde la ficha de un hallazgo, junto al generador de prompt.
El agente arregla el hallazgo directamente sobre tu clon local, y ahí se acaba su
alcance: **no tiene consola, ni git, ni red**. Solo puede leer ficheros del clon,
modificarlos por una herramienta que la aplicación controla, y pedir que se compile —
lo ejecuta Atalaya, no él.

Antes de arrancar se comprueban cuatro cosas, y si falla alguna se dice cuál:

1. Que tengas el **clon vinculado** de esa aplicación.
2. Que tu **árbol de trabajo esté limpio** — sin cambios sin commitear. Sin excepciones:
   es lo único que permite después distinguir lo que tocó el agente de lo tuyo, y por
   tanto lo que hace que «Descartar todo» sea seguro.
3. Que no haya **otra sesión de Copilot** corriendo (auditoría o arreglo).
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
