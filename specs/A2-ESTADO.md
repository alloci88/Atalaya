# A2 — Lo que se lee deja de ser la historia

> Archivada por la propia A2 (N-4). Es la parte **SPEC** del prompt, tal cual: el QUÉ. El CÓMO —el
> reparto de agentes que la ejecutó— sí está aquí porque la spec lo lleva dentro (N-10). Lo que
> produjo está en `docs/ESTADO.md`, `docs/NORMAS.md`, `specs/PLANTILLA.md` y la entrada **A2** de
> `DECISIONS.md`. Primera spec del repositorio escrita con este formato; `specs/PLANTILLA.md` sale
> de ella.

---

## SPEC A2 — Lo que se lee deja de ser la historia

### Por qué

A1 midió que una sesión no se va en ejecutar (suite 80 s, build 7 s, selfcheck 1 s) sino en leer y escribir: DECISIONS.md son ~354.000 tokens estimados y cada prompt hasta hoy empezaba con «lee DECISIONS»; cuando el agente busca por título usa entre el 2 % y el 11 %. En cinco fases DECISIONS creció 545 líneas y no perdió ninguna. Las normas dicen «N-1…N-5» y son ocho. Los prompts que dirigen cada fase no están en el repo. Esta entrega cambia lo que un agente lee al arrancar y lo que escribe al cerrar; no toca la suite (con 80 s no es el cuello de botella; si algún día lo es, será otra spec).

### Comportamiento

1. `docs/ESTADO.md` — el estado vigente de Atalaya, para leer entero al arrancar.
   - Una sección por área. Las áreas son las de DECISIONS agrupadas: hub y sincronización; inventario y alta; barrido y pasadas (modos, hilo, topes, coste por unidad); hallazgos, verificación y anclaje; arreglo con agente; gobernanza (veredictos, silencios, directivas, reset); ciclos, deriva e informes; métricas; proveedores de IA y tarifas; interfaz y sistema visual; setup, conexión, despliegue y marca; actualización y release; tests, bancos y CI. Si al hacerlo una área sobra o falta, se ajusta y se dice en el parte.
   - Cada línea es una regla o decisión QUE SIGUE EN PIE, en una frase, terminada con el D- (o la fase) donde se tomó. Nada de historia: ni «antes era», ni por qué se descartó lo otro, ni fechas. Quien quiera el porqué sigue el D- a DECISIONS.
   - Lo derogado no aparece. Una decisión que otra posterior revisa (A1 contó 15 explícitas; habrá más implícitas) aparece una sola vez, en su forma vigente, con el D- de la revisión.
   - Tope: 1.500 líneas y 120 kB en total. Si un área no cabe, se condensa; no se amplía el tope.
   - Cabecera de tres líneas que diga qué es, que se regenera de DECISIONS y que ante contradicción manda DECISIONS.

2. `docs/NORMAS.md` — las normas de la casa, fuera de DECISIONS.
   - N-1…N-8 se mueven tal cual (texto íntegro). En DECISIONS queda, en su lugar, un párrafo que apunta a `docs/NORMAS.md`.
   - Se añaden dos:
     - **N-9 — Un agente arranca leyendo NORMAS, ESTADO y su spec. DECISIONS no se lee: se consulta.** Un prompt que diga «lee DECISIONS» está mal escrito. Una sección de DECISIONS se abre por su título cuando ESTADO remite a ella y hace falta el porqué.
     - **N-10 — Reparto en paralelo por defecto.** Toda spec lleva su reparto en agentes; lo secuencial se justifica con la dependencia concreta (mismo fichero, escribe DECISIONS, build antes de test). Lo que no tenga dependencia declarada va en paralelo.
   - Se corrige el título: «Normas de la casa (N-1…N-10)».

3. `specs/` — las specs viven en el repo (N-4 aplicado a ellas).
   - `specs/PLANTILLA.md`: el formato de spec, con estas secciones y ninguna más: Por qué · Comportamiento · Reglas que protege (qué test, y qué se rompería en silencio sin él, N-5) · Lo que NO se toca · Definición de hecho · Reparto (agentes, paralelo salvo dependencia dicha, N-10) · Cierre (qué documentación exige el tamaño: S = entrada de DECISIONS de un párrafo y ESTADO si cambia una regla; M = lo anterior + MANUAL si cambia lo que ve el usuario; L = lo anterior + BACKLOG). Y la hora de inicio y fin en el parte.
   - `specs/A1-MEDIDA.md` y `specs/A2-ESTADO.md`: el prompt de A1 (te lo pega el usuario si no lo tienes; si no lo tienes, dilo y déjalo pendiente) y la parte SPEC de este documento, archivados tal cual.
   - Las specs anteriores a A1 no se recuperan: no están en el repo y reconstruirlas es historia.

4. DECISIONS.md, de aquí en adelante, en su propia entrada A2:
   - Una entrada por fase, un párrafo, como ya dice N-7; y cuando una entrada revise una decisión anterior, lo dice con la palabra «revisa D-xxx» en la primera línea, para que ESTADO se pueda regenerar buscándola.
   - ESTADO se actualiza en la misma entrega que cambia una regla; la spec lo lista en «Cierre».

### Reglas que protege (tests)

Un solo fichero nuevo de tests, `tests/Atalaya.App.Tests/DocsTests.cs` (o donde vivan `AboutVersionTests`, que ya prueban documentos), con tres tests de regla:
- Todo `D-nnn` citado en `docs/ESTADO.md` existe como bloque en DECISIONS.md. Lo que se rompería en silencio: una referencia a una decisión que nadie escribió.
- `docs/ESTADO.md` respeta el tope (1.500 líneas, 120 kB). Lo que se rompería: que ESTADO vuelva a ser DECISIONS.
- `docs/NORMAS.md` contiene N-1…N-10, cada una una vez, y DECISIONS.md no contiene ya el texto de ninguna (solo el puntero). Lo que se rompería: dos copias de una norma que divergen.

### Lo que NO se toca

- `src/`, `scripts/`, `.github/`, MANUAL.md, BACKLOG.md.
- Ningún test existente. Ningún bloque D- existente de DECISIONS: la única edición ahí es sustituir el bloque de normas por el puntero y añadir la entrada A2.
- Ni un literal, ni un test lento, ni nada de lo que A1 dejó en «Lo que se ve y no se toca».

### Definición de hecho

- `docs/ESTADO.md`, `docs/NORMAS.md`, `specs/PLANTILLA.md`, `specs/A1-MEDIDA.md`, `specs/A2-ESTADO.md`.
- DECISIONS: normas sustituidas por el puntero; entrada A2 de un párrafo con las cifras de ESTADO (líneas, kB, tokens estimados, número de reglas, número de D- citados) y la hora de inicio y fin de la sesión.
- `DocsTests` en verde; suite entera en verde; build en verde. No hace falta dist ni `--selfcheck`: no cambia el producto.
- Commits listados con sus hashes para el push del usuario.

### Reparto (N-10)

- **Fase 1, en paralelo, un agente por área de ESTADO** (unas doce). Cada uno recibe el nombre de su área, la lista de secciones de DECISIONS que le tocan (el orquestador la saca del índice de títulos `grep -n "^## \|^### "`, no leyendo el contenido) y escribe SOLO su sección en un fichero propio `docs/estado/<area>.md` (temporal). Cada agente lee solo sus secciones. Un área cuyas secciones pasen de 3.000 líneas se reparte entre dos agentes por rango de fases.
- **En paralelo con la fase 1, un agente** hace NORMAS.md, el puntero en DECISIONS, `specs/PLANTILLA.md` y los dos specs archivados. No toca `docs/estado/`.
- **En paralelo con la fase 1, otro agente** escribe `DocsTests` contra ficheros que aún no existen (los tests fallan hasta la fase 2; eso es correcto).
- **Fase 2, un agente integrador**: concatena las áreas en `docs/ESTADO.md` en el orden del comportamiento 1, elimina los temporales, resuelve duplicados entre áreas (una regla vive en una sola sección), comprueba el tope, corre la suite, escribe la entrada A2 y el parte. Es el único que escribe DECISIONS.
- Dependencia declarada: fase 2 espera a fase 1. Todo lo demás, paralelo.

### Cierre

Tamaño M por la regla de la plantilla: entrada de DECISIONS + ESTADO (nace aquí). MANUAL no cambia: el usuario no ve nada distinto. BACKLOG no cambia.
