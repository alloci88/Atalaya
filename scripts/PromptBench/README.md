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

Opciones: `--model <alias|id>` (por defecto `sonnet`), `--tema <General|Seguridad|…>`, y las
unidades a medir como argumentos sueltos (rutas relativas a la raíz del repositorio).

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

## Qué se midió con esto

Ver `DECISIONS.md` § F18, D-850 a D-858. El resumen: `--split` salió **neutro** —las dos formas
producen la misma clave de caché y ninguna reutiliza el prefijo entre unidades—, así que no entró en
producción. La palanca sigue aquí, desarmada, para poder repetir la medida el día que el CLI cambie
sus cortes de caché.
