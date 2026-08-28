# MANUAL — Atalaya

Manual de uso. Qué hace cada pantalla, en qué orden se usan y qué significa lo que
enseñan. Para montar el entorno o compilar, ver `README.md`; para saber **por qué**
algo está hecho como está, `DECISIONS.md`.

> Atalaya no tiene servidor. El «backend» es un repositorio git compartido —el
> **hub**— que la aplicación clona en tu máquina y sincroniza sola. Todo lo que
> ves sale de ficheros; nada se calcula en otro sitio.

---

## Empezar

1. **Cuenta.** La primera vez aterrizas aquí. Pulsa **Conectar con GitHub**, escribe
   el código que te muestra en `github.com/login/device` y autoriza. Ese login sirve
   para las tres cosas: acceso git al hub, autenticación de Copilot y autoría de los
   commits. No hay PAT que pegar ni URL que escribir.
2. **Nueva aplicación.** Da de alta el repositorio que vas a auditar. El asistente
   escanea el clon, arma el inventario y, si encuentra un baseline v4 (`CodeAudit/`),
   te ofrece importarlo.
3. **Inventario → Auditar selección.** Elige unidades y lanza. El progreso se sigue
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

- **Grande** significa que la unidad supera el umbral de tamaño (LOC o caracteres):
  queda excluida del ciclo y genera su propio hallazgo.
- **Auditar selección** lanza una sesión sobre lo marcado.
- **Re-escanear** vuelve a medir el clon: actualiza el inventario **y** los hallazgos
  medidos en el mismo gesto, y cuenta en un aviso qué cambió.
- **Reiniciar ciclo** abre uno nuevo sin borrar nada.

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
delta), coste del periodo y cobertura del ciclo. Debajo, seis gráficas:

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
6. **Actividad de sesiones** — el registro del periodo. **Un clic en una línea abre su
   informe en la vista Informes.**

Donde no hay medida se escribe «—» y qué haría falta para que aparezca. Un cero con
formato sería una medida que nadie ha tomado.

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

## Cosas que conviene saber

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
