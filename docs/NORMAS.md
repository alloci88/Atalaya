# NORMAS.md — Atalaya

Las normas de la casa de Atalaya. Se citan por su número a lo largo de `DECISIONS.md`, y un
agente las lee de aquí al arrancar (N-9).

## Normas de la casa (N-1…N-10)

Se citan por su número a lo largo de `DECISIONS.md`. Las tres primeras vienen de los prompts de
construcción; la cuarta se establece en F6.10, la quinta en R3, las tres siguientes —N-6, N-7 y
N-8— en el cierre de F27, que es donde se vio lo que cuesta no tenerlas, y las dos últimas —N-9
y N-10— en A2.

- **N-1 — Lo que toca el sync se prueba de verdad.** Cambio en la sincronización con el hub →
  tests de integración contra un remoto local `--bare`, sin red.
- **N-2 — Diagnóstico con evidencia, o incertidumbre declarada.** Nunca se adivina una causa: se
  mide, se enseña lo medido, y lo que no se ha comprobado se dice que no se ha comprobado.
- **N-3 — Nada se da por cerrado con commits sin publicar.** En esta máquina el `git push` es
  **exclusivamente del usuario**: el agente commitea y, al cerrar, lista los commits locales
  pendientes con sus hashes para que el usuario los publique. Un agente que pushea aquí se salta
  la única revisión que hay.
- **N-4 — El backlog es del equipo, y vive en el repo.** `BACKLOG.md` se mantiene al día igual que
  `MANUAL.md` y `DECISIONS.md`: cada fase mueve lo que entrega a «Cerrado» y apunta lo que deja
  pendiente. Un backlog que solo ve una persona no es un backlog del equipo, es una nota suya —y
  desaparece con ella.

- **N-5 — Un test por comportamiento que pueda romperse, no por control que se toca.** Antes de
  escribir uno: ¿qué regla protege, y qué se rompería **en silencio** si no existiera? Si la
  respuesta es «nada que un usuario notara al primer clic», no se escribe. Los tests de forma —que
  un XAML tenga un control— no valen; los de regla —que un duplicado se detecte, que el nombre
  salga del repo elegido— sí. El parte dice cuántos añade y por qué cada uno, en una línea. Se
  establece en R3 (**D-935**).

- **N-6 — Todo cambio visible se declara antes de hacerse.** En una fase de interfaz, el parte lleva
  una **lista de cambios visibles por vista** —una línea y su captura cada uno— y **nada cambia de
  disposición sin que el usuario lo haya pedido por escrito**. Un prompt que diga «ajusta» no
  autoriza a mover. Se establece en el cierre de F27, después de que siete arreglos de un informe de
  auditoría se llevaran por delante el raíl, las tarjetas del portafolio y el centrado de dos
  formularios sin que nadie los hubiera pedido: la auditoría dice qué está mal, no autoriza a
  rehacer. Un hallazgo es una propuesta hasta que el usuario la acepta.

- **N-7 — Pruebas y documentación proporcionales al cambio.** En una fase de presentación: **una
  entrada de DECISIONS por fase**, no por raíz ni por hallazgo; **tests solo de regla y solo si hay
  regla nueva**; **capturas solo de lo que cambia**, y en dos combinaciones. Verificar siete cambios
  cosméticos no puede costar más que hacerlos. Se establece en el cierre de F27, donde diez entradas
  de DECISIONS, quince tests y cuatro recorridos completos del banco documentaron un trabajo de
  presentación con el aparato de uno de arquitectura. La norma se aplica a sí misma: las diez
  entradas de F27 se refunden en **D-1001**, una.

- **N-8 — El agente no se revisa a sí mismo la interfaz.** En fases de presentación el ciclo es
  **cambio → build → tests → `dist` → parar**. Nada de banco de capturas, nada de recorridos, nada
  de mirar y volver a tocar: el usuario abre el `dist` y revisa en tres minutos lo que al agente le
  cuesta horas — y además el recorrido **conduce la aplicación con el ratón de verdad**, así que
  secuestra la máquina de quien está delante. Lo único automático que se conserva es
  **`--selfcheck`** (segundos): el arranque completo sin ventana y, desde F27, **pintando la primera
  vista**, para que un `dist` que no arranca no llegue al usuario. El banco de capturas queda como
  herramienta **a demanda para auditorías**, no como parte del desarrollo.

- **N-9 — Un agente arranca leyendo NORMAS, ESTADO y su spec. DECISIONS no se lee: se consulta.**
  Un prompt que diga «lee DECISIONS» está mal escrito. Una sección de DECISIONS se abre por su
  título cuando ESTADO remite a ella y hace falta el porqué. Se establece en A2, que midió el
  coste: DECISIONS son ~354.000 tokens estimados y ninguna fase reciente necesitó más del 11 %
  del fichero.

- **N-10 — Reparto en paralelo por defecto.** Toda spec lleva su reparto en agentes, y lo
  secuencial se justifica con la dependencia concreta: mismo fichero, escribe DECISIONS, build
  antes de test. Lo que no tenga dependencia declarada va en paralelo. Se establece en A2.
