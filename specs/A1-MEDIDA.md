# A1 — Cuánto cuesta hoy una sesión de agente en Atalaya

> Archivada en A2 (N-4: lo que dirige el trabajo vive en el repositorio). Es el prompt tal cual se
> ejecutó, anterior al formato de `specs/PLANTILLA.md`, así que no lleva sus secciones. Se archiva
> sin retocar: reescribirlo para que encaje en la plantilla sería inventar una historia que no pasó.
> Lo que produjo está en `docs/MEDIDA-AGILIDAD.md` y en la entrada **A1** de `DECISIONS.md`.

---

PROMPT-A1-MEDIDA — Cuánto cuesta hoy una sesión de agente en Atalaya

Eres el equipo de agentes que mantiene Atalaya. Sesión nueva. Esta vez NO leas DECISIONS.md entero: lee solo las «Normas de la casa» (N-1…N-8, al principio del fichero) y la última entrada (F38). Consola disponible: build + tests. El push lo hace EXCLUSIVAMENTE el usuario (commitea y lista hashes; jamás git push). Trabaja sobre `main`.

Esta es una entrega de MEDIDA (N-2). No se cambia ni una línea de producto, de tests ni de documentación, salvo el informe que se pide y su entrada en DECISIONS. Una sola entrega.

Por qué

El desarrollo de Atalaya se ha vuelto lento para los agentes: DECISIONS.md tiene ~19.700 líneas, hay ~2.770 tests que corren enteros por cada cambio, y el cierre de cada fase exige DECISIONS + MANUAL + BACKLOG + dist + `--selfcheck` aunque el cambio sean treinta líneas. Antes de optimizar nada hay que saber dónde se va el tiempo. Esta entrega produce las cifras; la siguiente decide qué se toca.

Lo que se mide

Tres momentos de una sesión, cada uno con sus números. Todo se mide con comando y se deja el comando junto al resultado, para que se pueda repetir.

1. El arranque: cuánto hay que leer para empezar
- Tamaño de `DECISIONS.md`, `MANUAL.md`, `BACKLOG.md` y `README.md`: líneas, bytes, y tokens estimados (bytes/4; dilo como estimación).
- Tamaño de las «Normas de la casa» solas.
- Para los últimos cinco prompts ejecutados (los encuentras en los ficheros `PROMPT-*.md` más recientes del repo, o si no están en el repo, en las entradas F33 a F38 de DECISIONS: qué secciones citan en su primera línea): tamaño sumado de las secciones que cada uno manda leer, frente al tamaño de DECISIONS entero. Una tabla: prompt · secciones citadas · líneas citadas · % del fichero.
- Cuántas entradas D-xxxx hay en total, y cuántas están marcadas como revisadas, sustituidas o retiradas por otra posterior (busca «revisa», «sustituye», «se retira», «deroga», «ya no»). Es la primera estimación de cuánto de DECISIONS es historia y cuánto es vigente. Dilo como estimación con el método.

2. La suite: cuánto tarda y qué es lento
- `dotnet build` de la solución, limpio (`dotnet clean` antes): tiempo de reloj.
- `dotnet test --no-build --logger "trx;LogFileName=medida.trx"` de la solución entera: tiempo de reloj total y, del `.trx`, número de tests, tiempo por proyecto de tests, y los 30 tests más lentos con su tiempo y su clase.
- Clasificación de los tests por zona, contando por proyecto y por namespace o carpeta: unitarios puros; integración con git (`--bare`, `TestFactory` con repos); bancos (PromptBench, concurrencia del hub, geometría, capturas); los que arrancan WPF o el contenedor completo. Para cada zona: cuántos y cuánto tiempo suman. El objetivo es saber qué parte de la suite es rápida por naturaleza y qué parte no.
- Cuántos tests de forma frente a de regla (N-5) hay: aproxima con el nombre y la clase, no los leas uno a uno; di el criterio.
- `scripts/publish.ps1` (o el comando de dist que se use) y `Atalaya.exe --selfcheck`: tiempo de reloj de cada uno.

3. El cierre: cuánto se escribe por entrega
- Para las mismas cinco fases del punto 1: líneas añadidas a DECISIONS, a MANUAL y a BACKLOG, tests añadidos y retirados, y ficheros de producto tocados. Sale de `git log --stat` entre los commits de cada fase. Una tabla: fase · líneas de producto · líneas de tests · líneas de documentación · ratio documentación/producto.

Lo que se entrega

- `docs/MEDIDA-AGILIDAD.md` (crea `docs/` si no existe): las tres secciones con sus tablas, cada cifra con el comando que la produjo, y al final un apartado «Lo que la medida dice» de como mucho diez líneas: dónde se va el tiempo, en orden, sin proponer todavía la solución.
- Una entrada en DECISIONS, **A1**, de un párrafo, con las cinco o seis cifras que importan y la referencia al informe. Nada más en DECISIONS.
- Commits listados con sus hashes para el push del usuario.

Lo que NO se toca

- Ningún fichero de `src/`, `tests/`, `scripts/`, `.github/`.
- MANUAL.md y BACKLOG.md.
- No se arregla nada de lo que se vea por el camino (un test lento, un literal, lo que sea): se apunta en el informe y se sigue. Es una medida, no una limpieza.
- No se ejecutan el banco de capturas ni recorridos con ratón (N-8). Los bancos que consuman créditos de IA (PromptBench, M1/M2) no se ejecutan: se cuentan y se clasifican por su código.

Definición de hecho

- `docs/MEDIDA-AGILIDAD.md` con las tres secciones, todas las cifras con su comando, y `medida.trx` referenciado (el `.trx` no se commitea; va en `.gitignore` si no está ya).
- DECISIONS: la entrada A1, un párrafo.
- `git status` limpio salvo los dos ficheros; commits listados.

Anti-objetivos

- No optimices nada. Si una cifra te tienta, la apuntas.
- No leas DECISIONS entero para «entender el contexto»: el coste de hacerlo es justo lo que se está midiendo. Si necesitas una sección concreta, la buscas por su título.
- No inventes cifras ni las redondees a ojo: si algo no se ha podido medir, se dice que no se ha medido y por qué (N-2).
- Jamás git push.
