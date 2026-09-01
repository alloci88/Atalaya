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

   Para **actualizar** no hace falta nada de esto: cuando salga una versión nueva, Atalaya te
   avisa y se actualiza sola con un botón (ver «Actualizar Atalaya», más abajo). El reemplazo a
   mano sigue funcionando, y es la salida cuando el botón no puede.

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

- **Grande** significa que la unidad supera el umbral de tamaño (LOC o caracteres):
  queda excluida del ciclo y genera su propio hallazgo.
- **Auditar selección** lanza una sesión sobre lo marcado.
- **Seleccionar cambiadas** marca las unidades cuyo código ha cambiado desde que
  se auditaron. Es el gesto de cada sprint; a partir de ahí, el flujo es el de
  siempre. Al lado, el **filtro de deriva** recorta la lista por lo que le ha
  pasado al código, que es una dimensión aparte del estado de auditoría.
- **Re-escanear** vuelve a medir el clon: actualiza el inventario **y** los hallazgos
  medidos en el mismo gesto, y cuenta en un aviso qué cambió.
- **Reiniciar ciclo** abre uno nuevo con todo pendiente, sin borrar nada. Es
  distinto del cierre normal, que **siembra** el ciclo siguiente con la deriva
  del que termina (ver «Cambiar de ciclo», más abajo).
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
- Resolver sigue exigiendo **evidencia de cambio**: si la unidad es la misma que la
  última vez que se vio el hallazgo, un «arreglado» se degrada a «presente».
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

> Y una unidad barrida **no es** una unidad sin defectos: es una unidad de la que el
> auditor no saca más con este criterio.

El resumen de cierre agrupa sus hallazgos **por clase**, con el recuento por severidad al
lado, igual que la vista de Hallazgos. Cada línea se despliega de un clic.

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

**Los porcentajes no redondean hacia una mentira.** Si hay una sola unidad auditada, la
cobertura nunca se enseña como 0 % —3 de 1.335 son «0,2 %», no «0 %»—, y si queda una sola
sin auditar nunca se enseña como 100 %: eso se lee como «aquí ya no hay nada que mirar» y
cierra la pregunta. Cuando el número es tan pequeño (o tan grande) que ni un decimal lo
salva, se dice **«< 0,1 %»** o **«> 99,9 %»**. El 0 % y el 100 % exactos sí aparecen: son
verdad y significan algo. Los decimales solo salen cuando hacen falta — «42 %» se lee de un
vistazo y «42,0 %» no dice nada más.

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
3. **Cobertura por aplicación** — un rosco por app; un clic abre su inventario. Un tramo
   diminuto pero real se dibuja igualmente: un rosco con 3 de 1.335 no puede parecerse a uno
   vacío, que significa lo contrario.
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
cuando quieras.

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
está clonado y si cada **proveedor de auditoría** responde. Es el sitio al que te manda cualquier
fallo de conexión.

Las tres primeras filas son de **GitHub y no se sustituyen nunca**: sin ellas no hay identidad, ni
autoría de los commits, ni hub donde escribir los hallazgos. Debajo hay **una fila por proveedor de
auditoría**, cada una con su propio piloto y su propia instrucción si falta algo. **Basta con tener
uno listo** para poder auditar.

Un proveedor **opcional** que no tengas instalado —hoy, Claude Code— sale con un `+` gris y la
palabra «opcional»: te dice que existe y cómo activarlo si te interesa, y ahí acaba. **No es un
fallo**, no se pinta como tal y no impide nada.

### Ajustes

Frescura, **proveedor de auditoría** y su modelo, el interruptor del
**arreglo asistido** (encendido por defecto), tema **claro/oscuro**,
intervalo de sincronización y las acciones destructivas, con su confirmación. Al final,
**Acerca de Atalaya**: versión, organización y los enlaces al repositorio y a este manual.

**Los ajustes son de esta máquina** —viven en tu `settings.json`, no en el hub—, así que
cambiarlos no le toca nada a tus compañeros. Ésa es justamente la regla que decide qué está aquí:
**lo que escribe algo que el equipo comparte se gobierna en la aplicación, no en tus Ajustes.** Por
eso el **umbral de unidad grande** no está en esta pantalla —clasifica el inventario y crea los
hallazgos de tamaño, que son de todos— y se gobierna en **Inventario → Umbrales**. La **frescura**
sí está aquí: solo colorea tu lista de hallazgos y no le cambia el estado a nadie.

Cada control dice bajo su caja **cuándo surte efecto**, porque no todos aplican igual:

| Ajuste | Qué gobierna | Cuándo aplica |
| --- | --- | --- |
| **Pasadas del barrido (tope)** | Cuántas veces se repasa cada unidad | En las auditorías que lances **a partir de ahora** |
| **Frescura (días)** | Cuándo un hallazgo confirmado se marca por revisar | Al guardar; la lista lo aplica al dibujarse |
| **Proveedor de auditoría** | Con quién auditas y verificas tú | En las sesiones que lances a partir de ahora |
| **Modelo del auditor** | Con qué modelo del proveedor elegido | En las sesiones que lances a partir de ahora |
| **Arreglo asistido** | Si aparece «Arreglar con agente» | Al guardar |
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

---

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
| Quién paga | El asiento de tu organización (peticiones premium) | Tu suscripción de Claude |
| Cómo se instala | Nada: viaja dentro de Atalaya | `npm install -g @anthropic-ai/claude-code`, y `claude` una vez en tu terminal |
| Auditar y verificar | Sí | Sí |
| Arreglo asistido | Sí | Todavía no |

**Atalaya no guarda credenciales de Anthropic.** Usa la sesión que el CLI ya tiene en tu máquina,
exactamente igual que con Copilot usa tu login de GitHub. Si no has iniciado sesión, Atalaya te lo
dice en **Cuenta** y te manda a hacerlo en tu terminal; no hay ningún sitio en la aplicación donde
pegar una clave, y es a propósito.

**GitHub sigue haciendo falta.** Elegir Claude Code cambia quién juzga el código y nada más: la
identidad, la autoría de los commits y el hub donde viven los hallazgos siguen siendo de GitHub. No
es un sustituto, es un segundo auditor.

### Cómo se elige

En **Ajustes → Proveedor de auditoría**. Es una preferencia **tuya y de esta máquina** —cada uno
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

### El coste, dicho como es

**Los dos no cuentan en la misma moneda, y Atalaya no los mezcla.**

- **Copilot** factura **peticiones premium**, con su multiplicador. Es lo que siempre se ha visto.
- **Claude Code** informa un coste en **dólares de tarifa de lista** — lo que habrían costado esos
  tokens pagando la API. **Tu suscripción no cobra por llamada**, así que ese número **no es una
  factura**: sirve para comparar el peso de dos auditorías, no para cuadrar gastos. Donde aparece,
  aparece con esa etiqueta puesta.

En **Métricas**, cuando en el periodo han auditado las dos casas, **no verás un total**: verás una
línea por proveedor. Un total sería la suma de dos magnitudes distintas, y no significaría nada.
Con una sola casa, el número de siempre.

Y antes de lanzar con Claude Code, la estimación **dice lo que sabe y no promete dinero**: «~2
llamadas estimadas · coste según tu suscripción». Sin tarifa por llamada no hay nada que estimar,
y Atalaya prefiere decirlo a inventarse una equivalencia.

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

## Si una sesión falla: qué significa cada error

Cuando Copilot rechaza una sesión, Atalaya la deja en un estado terminal **visible** —con
su causa, el reloj parado y sin nada corriendo por detrás— y enseña un aviso en su propia
franja, encima del cuerpo de la sesión y sin taparlo. El texto se puede **seleccionar y copiar**, y
**Copiar error** se lleva al portapapeles el mensaje junto con el error crudo del
proveedor, que es lo que hay que pegar en un correo a quien administre la organización.

| Si ves esto | Significa | Se arregla así |
|---|---|---|
| **La organización ha agotado sus peticiones premium de Copilot** | Vuestro plan se ha quedado sin peticiones. **No es tu asiento ni tus credenciales**: los dos siguen bien. | Nada que tocar en Atalaya: esperar a que se renueve la cuota, o auditar con un modelo de multiplicador menor si vuestro plan lo permite. Atalaya **no reintenta sola** — reintentar contra una cuota agotada gasta las peticiones del reset siguiente. |
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
