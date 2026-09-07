# El banco de concurrencia

La prueba de carga que el hub nunca tuvo (F31 §1). **N personas auditando a la vez contra el mismo
hub**, durante un tiempo fijo, con el agente falso y sobre un `--bare` temporal.

Es el hermano del [banco de capturas](../Banco/README.md) y está aquí por lo mismo: no es producto,
pero tampoco es de una fase. `scripts/` ya alberga `Banco`, `PromptBench` e `IconGen`.

## Por qué existe

El hub prometía desde el primer día «pull → rebase → push con resolución de conflictos», y hasta
BUGFIX-PUSH **nadie lo había ejercitado con dos personas a la vez**: todos los tests de dos clones
publicaban por turnos, que es el caso fácil. El día que dos personas auditaron a la vez aplicaciones
distintas, una sesión se quedó colgada dentro de `git_remote_push`, sin timeout y sin cancelación.

Un hub que se cuelga con dos personas no es colaborativo. Esto es lo que mide si lo es.

## Qué mide

| Cifra | Qué contesta |
|---|---|
| Sesiones terminadas | ¿Termina todo el mundo, o alguien se queda dentro de un push? |
| Hallazgos que llegan al hub | ¿Se pierde trabajo por el camino? |
| Reintentos | ¿Cuánto se están pisando de verdad? |
| Publicación más larga | La cifra por la que existe la fase: cuánto llegó a quedarse bloqueada una publicación, contra el tope de 30 s |
| Conflictos y cómo se resolvieron | Qué reglas de `HubMergePolicy` se ejercitaron, y a favor de quién |
| Roturas | Lo que reventó, con su tipo y su mensaje |

Las cifras del hub **no se le preguntan a los participantes**: se leen de un **clon nuevo** al
terminar. Preguntarle a quien publicó si publicó es preguntarle al sospechoso — que es exactamente
el defecto que esta fase cerró.

## Los ritmos

Tres personas distintas, porque tres personas iguales no se pisan de forma interesante:

| Ritmo | Qué hace |
|---|---|
| `Rapido` | Sesiones cortas y pausas de 2 s: el que más veces choca |
| `Lento` | Sesiones de cuatro unidades y pausas de 12 s: el que llega tarde y se encuentra el hub movido |
| `QueSeCae` | Se cae a mitad, sin cerrar la sesión y sin soltar sus reclamaciones — lo que deja un cierre forzado |

Con `-N` mayor que 3 los ritmos se reparten en round-robin.

## Cómo se corre

```powershell
# La tanda de release: tres personas, diez minutos, parte a fichero
dotnet run --project scripts\BancoCarga\Atalaya.Carga -- -N 3 -Minutos 10 -Salida C:\tmp\tanda.md

# Una pasada corta para ver que sigue vivo
dotnet run --project scripts\BancoCarga\Atalaya.Carga -- -N 2 -Minutos 0.5

# Con lo que dice el agente falso, incluido POR QUÉ se rechaza un hallazgo
dotnet run --project scripts\BancoCarga\Atalaya.Carga -- -N 2 -Minutos 0.5 -Verboso
```

| Parámetro | Por defecto | Qué es |
|---|---|---|
| `-N` | 3 | Cuántas personas a la vez |
| `-Minutos` | 10 | Cuánto dura la tanda |
| `-Salida` | — | Dónde escribir el parte en Markdown |
| `-Verboso` | apagado | Lo que dice el agente falso, llamada a llamada |

## Cuándo se corre

**En cada release, no en cada build.** Una tanda de diez minutos dura veinte veces lo que la suite
entera, y lo que mide no cambia con un cambio de vista. Por eso no está en `Atalaya.sln`: meterlo en
la solución haría que cada build de la aplicación arrastrara una tanda de carga.

Lo que sí corre en cada build es `ConcurrentClaimsTests`, que es la misma pregunta en pequeño y en
segundos.

## Lo que NO toca

**Nunca el hub real** (N-1). El `--bare` se crea en `%TEMP%\atalaya-carga\<fecha>` y ahí se queda;
cada persona tiene su propio clon y sus propios ajustes, y el hub del banco entra por
`HubUrlOverride`, que es la misma puerta por la que un usuario apuntaría a otro hub — no hay una
segunda ruta de configuración que pudiera divergir de la de verdad.

Tampoco gasta créditos: el agente es `FakeCopilotAgent`, y no hay una sola llamada a un modelo.
