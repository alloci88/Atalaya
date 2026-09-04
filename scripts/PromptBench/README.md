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
$bench = "scripts/PromptBench/bin/Debug/net8.0-windows/PromptBench.exe"

# Offline y gratis: la composición del prompt, y si el prefijo estable lo es de verdad.
& $bench composicion

# Una pasada REAL por unidad, con el CLI y el servidor MCP de verdad. GASTA cuota.
& $bench claude --whole --model sonnet
& $bench claude --split --model sonnet

# La comparacion de F21: la misma tanda con el corte apagado.
& $bench claude --sin-corte --model sonnet --pasadas 3
```

Opciones: `--model <alias|id>` (por defecto `sonnet`), `--tema <General|Seguridad|…>`,
`--existentes N` y las unidades a medir como argumentos sueltos (rutas relativas a la raíz del
repositorio).

`--sin-corte` apaga el corte en `unit_done` (F21), que en producción va encendido. **Es la línea
contra la que se compara**: sin él no hay forma de enseñar que la escritura de caché baja y que los
hallazgos tardíos no se mueven. Al final de la tanda el banco dice cuántas pasadas se cortaron de
verdad y, de las que no, por qué — una pasada que paga su llamada de cortesía tiene que verse, no
esconderse en la media.

## `barrido` — el barrido de verdad, con la aplicación delante

`composicion` y `claude` miden un PROMPT. `barrido` mide un BARRIDO: monta un hub vacío en el
temporal y conduce el `SessionCoordinator` de producción sobre un clon que se le pase, con su regla
de parada, su tope y su reconciliación. Lo único fingido es el hub.

```powershell
& $bench barrido --clon C:\ruta\al\clon --tope 6 --tandas 3 --sin-corte `
    src/Servicios/CalculadoraCarga.cs src/Servicios/ClienteRemoto.cs
```

- `--tope N` (6 por defecto) es `MaxPassesPerUnit`; `--tandas N` son N muestras independientes, cada
  una con un hub nuevo — con el de la anterior, la tanda 2 vería sus hallazgos como existentes.
- `--estructurado` (M1) sustituye el bloque `MÉTODO DE BARRIDO` por el recorrido miembro × familia.
  Se midió y **no se hizo fase** (D-908); la palanca sigue aquí para poder repetirla.
- **`--hilo` (M2)** corre la unidad como una **conversación**: una sola sesión del proveedor para
  toda la unidad, la pasada 1 con el prompt de producción entero —byte a byte— y las pasadas 2..N
  con un texto de continuación corto y fijo (`PromptComposer.ContinuationTurn`), sin reenviar
  reglas, código ni la lista de existentes. Es incompatible con el corte de F21 por construcción —la
  invocación tiene que sobrevivir a la pasada—, así que la comparación se hace con `--sin-corte` en
  **los dos** brazos.

Después de la tabla, el barrido imprime lo que decide una medida de coste: el **consumo por
pasada** (fresca / leída / ESCRITA / salida y una valoración en credits a tarifa Opus), la
comparación **pasada 1 contra pasadas 2..N** —que es donde vive el ahorro del hilo—, la **cobertura
acumulada por tope** y en qué pasada nace cada hallazgo.

**La valoración en credits no es el coste de la sesión.** Claude Code no factura a la organización
y la aplicación, con razón, no le inventa un coste. Lo que imprime el banco es una valoración con la
tarifa de Opus publicada —la misma con la que F20 reprodujo al credit la factura de la sesión de
referencia (D-871)— para poder decir «este brazo cuesta la mitad que el otro» sin comparar cuatro
columnas de tokens a ojo. Vive en `BenchCredits` y lo dice ahí.

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

**Y la salida es la de verdad desde F21.** Antes se leía del evento `assistant`, que trae un consumo
PARCIAL: decía 5 donde la llamada acabó gastando 23.569. Ahora se lee del evento que cierra el
mensaje, así que las cifras de esta tabla anteriores a F21 subestiman la salida.

La fila **`ajuste`** no es una llamada: es el cuadre del final, y lo que trae es sobre todo lo que el
CLI gastó por su cuenta con **su modelo auxiliar** (~10.000 tokens de entrada fresca por pasada), que
no es de ninguna llamada del auditor y no aparece en ningún otro sitio del flujo.

Es el diagnóstico que ordenó F19. Una llamada sin herramienta detrás es texto, y en una auditoría
el texto no entra en ningún dato: los hallazgos viajan por herramienta. Si una tanda enseña muchas
llamadas «solo texto», o una herramienta por llamada en vez de agrupadas, ahí está el gasto.

## Qué se midió con esto

Ver `DECISIONS.md` § F18 (D-850…D-858), § F19 (D-861…D-869) y § F20 (D-871…D-876). El resumen: `--split` salió **neutro** —las dos formas
producen la misma clave de caché y ninguna reutiliza el prefijo entre unidades—, así que no entró en
producción. La palanca sigue aquí, desarmada, para poder repetir la medida el día que el CLI cambie
sus cortes de caché.
