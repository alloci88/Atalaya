# PromptBench — el banco de medida de F18

Contesta dos preguntas con números, no con impresiones:

1. **¿De qué está hecho un prompt de auditoría?** Bloque a bloque, y qué fracción es el código que
   se está auditando.
2. **¿Qué le pasa a la caché del proveedor** cuando el prefijo estable se manda de una forma o de
   otra?

No forma parte del producto y **no está en `Atalaya.sln`** —como `IconGen`—: meterlo en la solución
haría que cada build de la aplicación arrastrara un ejecutable que solo se usa a mano. Pero
**reutiliza el código de producción** (`PromptComposer`, `ClaudeCodeProvider`), así que lo que mide
es lo que pasa de verdad, no una maqueta que se queda vieja a la primera.

## Cómo se usa

```powershell
dotnet build scripts/PromptBench/PromptBench.csproj
$bench = "scripts/PromptBench/bin/Debug/net8.0/PromptBench.exe"

# Offline y gratis: la composición del prompt, y si el prefijo estable lo es de verdad.
& $bench composicion

# Una pasada REAL por unidad, con el CLI y el servidor MCP de verdad. GASTA cuota.
& $bench claude --whole --model sonnet
& $bench claude --split --model sonnet
```

Opciones: `--model <alias|id>` (por defecto `sonnet`), `--tema <General|Seguridad|…>`,
`--existentes N` y las unidades a medir como argumentos sueltos (rutas relativas a la raíz del
repositorio).

`--pasadas N` simula el barrido: N pasadas sobre la misma unidad, cada una viendo como conocido lo
que reportaron las anteriores. **Una sola pasada mide el caso barato**; el gasto de F20 estaba en
las siguientes, donde el prefijo se vuelve a escribir entero.

`--existentes N` siembra N hallazgos conocidos en la unidad. **Sin él todas las medidas son de una
PRIMERA pasada**, que es el caso barato: en una segunda el auditor además tiene que reconciliar, y
es ahí donde se ve si agrupa sus herramientas en un turno o gasta una vuelta por cada cosa (F19).

**El escenario por defecto** son dos unidades pequeñas de este mismo repositorio
(`Hashing.cs` y `AxisScale.cs`), del tamaño de las del banco de pruebas de la línea base. Se pueden
dar otras; lo que **no** se puede es comparar dos ejecuciones con unidades distintas.

## Cómo se lee lo que imprime

- La tabla de **composición** es de estimaciones (~4 caracteres por token, la misma regla que
  gobierna el presupuesto de directivas). No hace falta exactitud: lo que se decide con ella —dónde
  está el peso— no cambia porque la cuenta se desvíe.
- En el modo `claude`, la fila que **decide** es la de la **primera llamada de cada unidad**. Es la
  única medida determinista de la serie: su prompt lo fijan nuestros bytes y nada más. De la segunda
  llamada en adelante el prompt lleva dentro lo que contestó el modelo, que cambia en cada
  ejecución; comparar totales mide sobre todo esa varianza (fue el primer error de F18, ver D-851).
- La columna **Tools** dice si el agente llegó a llamar a las herramientas. Una medición sobre una
  sesión en la que el modelo nunca llamó a nada mide otra cosa —un modelo confundido gasta
  distinto— y se leería como comparable.
- **La caché del proveedor dura una hora.** Repetir el mismo escenario mide una caché caliente, no
  una fría. Para una medida limpia, unidades que no se hayan enviado antes.

## El mapa de llamadas (F19)

En el modo `claude`, después de la tabla sale **qué pidió cada llamada**:

```
llamada 1: fresca 2 · leída 11.322 · ESCRITA 13.681 · salida 2 → submit_findings × 5 + unit_done
llamada 2: fresca 2 · leída 25.003 · ESCRITA 29.786 · salida 2 → (sin herramienta: solo texto)
```

**Lectura y escritura van separadas, y no es cosmético** (F20): escribir en caché cuesta doce veces
leerla, así que dos llamadas con la misma «entrada» pueden costar trece veces distinto. La columna
que decide es **ESCRITA**.

Es el diagnóstico que ordenó F19. Una llamada sin herramienta detrás es texto, y en una auditoría
el texto no entra en ningún dato: los hallazgos viajan por herramienta. Si una tanda enseña muchas
llamadas «solo texto», o una herramienta por llamada en vez de agrupadas, ahí está el gasto.

## Qué se midió con esto

Ver `DECISIONS.md` § F18 (D-850…D-858), § F19 (D-861…D-869) y § F20 (D-871…D-876). El resumen: `--split` salió **neutro** —las dos formas
producen la misma clave de caché y ninguna reutiliza el prefijo entre unidades—, así que no entró en
producción. La palanca sigue aquí, desarmada, para poder repetir la medida el día que el CLI cambie
sus cortes de caché.
