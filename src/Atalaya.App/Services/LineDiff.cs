namespace Atalaya.App.Services;

/// <summary>Qué es una línea del diff.</summary>
public enum DiffKind
{
    /// <summary>Igual en las dos versiones: contexto.</summary>
    Contexto,

    /// <summary>Solo está en el después.</summary>
    Anadida,

    /// <summary>Solo estaba en el antes.</summary>
    Quitada,

    /// <summary>Un tramo de contexto que se ha plegado. <see cref="DiffLine.Text"/> lo dice.</summary>
    Salto,
}

/// <summary>
/// Una línea del diff, con su número en cada versión (null donde no existe).
/// </summary>
public sealed record DiffLine(DiffKind Kind, int? OldLine, int? NewLine, string Text)
{
    public string Marker => Kind switch
    {
        DiffKind.Anadida => "+",
        DiffKind.Quitada => "−",
        DiffKind.Salto => "⋯",
        _ => " ",
    };

    public string OldNumber => OldLine?.ToString() ?? string.Empty;

    public string NewNumber => NewLine?.ToString() ?? string.Empty;
}

/// <summary>
/// Diff de líneas, hecho en casa (F6.9 §4).
/// <para>
/// <b>Por qué no DiffPlex.</b> Atalaya se despliega como una carpeta de DLLs sueltas (
/// <c>dist/</c>) sobre una red corporativa; cada paquete nuevo es un DLL más que desplegar y un
/// <c>restore</c> más que tiene que salir bien en esa red. Lo que el panel necesita es diff de
/// LÍNEAS entre dos versiones de un fichero de texto — no diff de palabras, ni de caracteres, ni
/// formato unificado, ni merge a tres bandas—, y eso son las cien líneas de aquí abajo, con
/// tests propios y sin nada que restaurar. AvalonEdit, que sí está, se sigue usando para pintar
/// código; esto es lo único que faltaba.
/// </para>
/// <para>
/// <b>Cómo.</b> Se recorta primero el prefijo y el sufijo comunes —que en un arreglo quirúrgico es
/// casi todo el fichero— y se resuelve por LCS solo el trozo del medio. Con eso, un cambio de tres
/// líneas en un fichero de 5.000 cuesta lo que cuesta comparar tres líneas. Si aun así el trozo
/// central es enorme, se dice y se enseña como reemplazo completo en vez de quedarse pensando.
/// </para>
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// Tope del trozo central que se resuelve por LCS. 2.000 × 2.000 es una tabla de 4 M de
    /// enteros (~16 MB): mucho para un fichero de código y poco para el ordenador. Por encima de
    /// eso el diff deja de ser útil de todos modos —nadie revisa dos mil líneas cambiadas— y lo
    /// honrado es decirlo.
    /// </summary>
    public const int MaxMiddleLines = 2000;

    /// <summary>Líneas de contexto alrededor de cada cambio cuando se pliega.</summary>
    public const int ContextLines = 3;

    /// <summary>El diff completo, línea a línea, sin plegar.</summary>
    public static IReadOnlyList<DiffLine> Compute(string? before, string? after)
    {
        string[] a = SplitLines(before);
        string[] b = SplitLines(after);

        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
               && string.Equals(a[^(suffix + 1)], b[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var lines = new List<DiffLine>();
        for (int i = 0; i < prefix; i++)
        {
            lines.Add(new DiffLine(DiffKind.Contexto, i + 1, i + 1, a[i]));
        }

        int aMid = a.Length - suffix - prefix;
        int bMid = b.Length - suffix - prefix;

        if (aMid > MaxMiddleLines || bMid > MaxMiddleLines)
        {
            // Demasiado para casar línea a línea: se dice, y se enseña como reemplazo entero del
            // trozo. Fingir un diff fino sobre esto sería inventarse correspondencias.
            lines.Add(new DiffLine(DiffKind.Salto, null, null,
                $"cambio demasiado grande para casar línea a línea ({aMid} → {bMid} líneas)"));
            for (int i = 0; i < aMid; i++)
            {
                lines.Add(new DiffLine(DiffKind.Quitada, prefix + i + 1, null, a[prefix + i]));
            }

            for (int i = 0; i < bMid; i++)
            {
                lines.Add(new DiffLine(DiffKind.Anadida, null, prefix + i + 1, b[prefix + i]));
            }
        }
        else
        {
            AppendLcs(lines, a, b, prefix, aMid, bMid);
        }

        for (int i = 0; i < suffix; i++)
        {
            lines.Add(new DiffLine(
                DiffKind.Contexto, a.Length - suffix + i + 1, b.Length - suffix + i + 1, a[a.Length - suffix + i]));
        }

        return lines;
    }

    /// <summary>
    /// El diff plegado: solo los cambios con <see cref="ContextLines"/> líneas alrededor, y una
    /// marca donde se han saltado líneas iguales. Es lo que se pinta.
    /// </summary>
    public static IReadOnlyList<DiffLine> Collapsed(string? before, string? after)
        => Collapse(Compute(before, after));

    /// <inheritdoc cref="Collapsed"/>
    public static IReadOnlyList<DiffLine> Collapse(IReadOnlyList<DiffLine> full)
    {
        var keep = new bool[full.Count];
        for (int i = 0; i < full.Count; i++)
        {
            if (full[i].Kind == DiffKind.Contexto)
            {
                continue;
            }

            for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(full.Count - 1, i + ContextLines); j++)
            {
                keep[j] = true;
            }
        }

        var output = new List<DiffLine>();
        int skipped = 0;
        for (int i = 0; i < full.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0)
                {
                    output.Add(new DiffLine(DiffKind.Salto, null, null, $"{skipped} línea(s) sin cambios"));
                    skipped = 0;
                }

                output.Add(full[i]);
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0 && output.Count > 0)
        {
            output.Add(new DiffLine(DiffKind.Salto, null, null, $"{skipped} línea(s) sin cambios"));
        }

        return output;
    }

    /// <summary>Cuántas líneas se añaden y cuántas se quitan. Es el rótulo de la pestaña.</summary>
    public static (int Added, int Removed) Tally(IReadOnlyList<DiffLine> lines)
        => (lines.Count(l => l.Kind == DiffKind.Anadida), lines.Count(l => l.Kind == DiffKind.Quitada));

    /// <summary>Resuelve el trozo central por subsecuencia común más larga.</summary>
    private static void AppendLcs(List<DiffLine> output, string[] a, string[] b, int offset, int aMid, int bMid)
    {
        var table = new int[aMid + 1, bMid + 1];
        for (int i = aMid - 1; i >= 0; i--)
        {
            for (int j = bMid - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(a[offset + i], b[offset + j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < aMid && y < bMid)
        {
            if (string.Equals(a[offset + x], b[offset + y], StringComparison.Ordinal))
            {
                output.Add(new DiffLine(DiffKind.Contexto, offset + x + 1, offset + y + 1, a[offset + x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                output.Add(new DiffLine(DiffKind.Quitada, offset + x + 1, null, a[offset + x]));
                x++;
            }
            else
            {
                output.Add(new DiffLine(DiffKind.Anadida, null, offset + y + 1, b[offset + y]));
                y++;
            }
        }

        while (x < aMid)
        {
            output.Add(new DiffLine(DiffKind.Quitada, offset + x + 1, null, a[offset + x]));
            x++;
        }

        while (y < bMid)
        {
            output.Add(new DiffLine(DiffKind.Anadida, null, offset + y + 1, b[offset + y]));
            y++;
        }
    }

    /// <summary>
    /// Parte en líneas sin inventarse una al final. Un fichero vacío son CERO líneas, no una vacía:
    /// crear un fichero nuevo no puede leerse como «se cambió una línea en blanco».
    /// </summary>
    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        string normalized = text!.Replace("\r\n", "\n").Replace('\r', '\n');

        // Se quita UN salto final —el de cierre de fichero—, no todos: un fichero que termina en
        // tres líneas en blanco y otro que termina en una no son el mismo fichero.
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
    }
}
