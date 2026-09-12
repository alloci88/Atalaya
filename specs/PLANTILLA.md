# PLANTILLA.md — el formato de spec de Atalaya

Toda spec de Atalaya lleva estas siete secciones, en este orden, y ninguna más. Al final hay una
plantilla en blanco lista para copiar.

## Por qué

El problema, con la cifra o la evidencia que lo sostiene (N-2). Si no hay medida, se dice que no la
hay; una spec que arranca con una intuición sin medir no arranca.

## Comportamiento

El QUÉ, numerado. Lo que hay que poder hacer cuando la entrega esté hecha, no cómo se implementa.
Cada punto se puede comprobar mirándolo.

## Reglas que protege

Por cada test: qué regla protege y **qué se rompería en silencio** si no existiera (N-5). Un test que
no conteste a esas dos preguntas no se escribe.

## Lo que NO se toca

La lista explícita de ficheros, vistas y comportamientos que quedan fuera. Lo que no esté aquí ni en
Comportamiento no se mueve (N-6).

## Definición de hecho

La lista de comprobación del cierre: qué tiene que estar verde, construido y escrito para poder
decir que la entrega está hecha.

## Reparto

Los agentes y qué hace cada uno. **Paralelo por defecto**; lo secuencial se justifica con la
dependencia concreta —mismo fichero, escribe DECISIONS, build antes de test— y se dice cuál es
(N-10).

## Cierre

El tamaño de la entrega y la documentación que exige, proporcional al cambio (N-7):

- **S** — entrada de `DECISIONS.md` de un párrafo, y `docs/ESTADO.md` si cambia una regla.
- **M** — lo de S, más `MANUAL.md` si cambia lo que ve el usuario.
- **L** — lo de M, más `BACKLOG.md`.

El parte de cierre lleva la **hora de inicio y la hora de fin de la sesión**, y se revisa contra la
spec **sección por sección**: se recorren todas, incluidas las que no llevan número, y cada una se
confirma o se dice por qué no.

---

## Plantilla en blanco

```markdown
# <Código> — <Título>

## Por qué

## Comportamiento

1.

## Reglas que protege

## Lo que NO se toca

## Definición de hecho

- [ ]

## Reparto

## Cierre

Tamaño: S / M / L
```
