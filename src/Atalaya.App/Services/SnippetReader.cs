using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>En qué relación está el código del clon con lo que el auditor vio (F5.5 §3, F5.6 §2).</summary>
public enum SnippetState
{
    /// <summary>La línea anclada sigue diciendo exactamente lo mismo. Lo que se ve es lo que hay.</summary>
    Anclado,

    /// <summary>El código es el mismo pero se ha movido de sitio: se enseña dónde está ahora.</summary>
    Movido,

    /// <summary>La línea anclada ya no coincide y no aparece en el fichero: el código cambió.</summary>
    Cambiado,

    /// <summary>
    /// El código exacto ya no está, pero el <b>miembro</b> que nombra el hallazgo sí: se resalta su
    /// primera línea de código en vez de un número de línea que ya no significa nada (F5.6, D-222).
    /// </summary>
    Reanclado,

    /// <summary>
    /// Ni el código anclado ni el símbolo aparecen: no se resalta nada y se dice (F5.6, D-225).
    /// </summary>
    NoLocalizado,

    /// <summary>El fichero ya no existe en el clon.</summary>
    FicheroNoEncontrado,

    /// <summary>No hay clon en esta máquina: no hay nada actual que enseñar.</summary>
    SinClon,

    /// <summary>El hallazgo no tiene ubicación en el código.</summary>
    SinUbicacion,
}

/// <summary>
/// Cómo se lee la franja que va encima del código (F6.7). No es decoración: dice si lo que hay
/// escrito ahí pide algo o solo informa.
/// </summary>
public enum SnippetTone
{
    /// <summary>Deriva sin verificar sobre un hallazgo vivo: ámbar, y con la acción que la cierra.</summary>
    Aviso,

    /// <summary>Información sobre un hallazgo que ya no pide nada. Neutra, y sin botón.</summary>
    Nota,
}

/// <summary>
/// Lo que el panel de código enseña y lo que dice encima. Las líneas son del <b>fichero</b>.
/// </summary>
/// <param name="Notice">
/// El texto tal y como se pinta, ya resuelto para el estado del hallazgo. Ver
/// <see cref="SnippetReader.ForFinding"/>.
/// </param>
public sealed record SnippetPanel(
    SnippetState State,
    string Text,
    int FirstLine,
    int HighlightLine,
    string? Member,
    string Notice)
{
    public static SnippetPanel Empty(SnippetState state, string notice)
        => new(state, string.Empty, 1, 0, null, notice);

    /// <summary>
    /// El aviso SIN su llamada a la acción: el hecho a secas. Es lo que queda cuando el hallazgo
    /// ya no está activo y por tanto no hay nada que pedirle a nadie (F6.7).
    /// </summary>
    public string Fact { get; init; } = Notice;

    /// <summary>El tono con el que se pinta la franja. Por defecto, el de un hallazgo vivo.</summary>
    public SnippetTone Tone { get; init; } = SnippetTone.Aviso;

    public bool HasCode => Text.Length > 0;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>
    /// El aviso lleva «Verificar ahora» solo cuando verificar arregla algo: re-anclar la ubicación
    /// o pedir veredicto. Sin clon, verificar no puede hacer nada desde esta máquina — y sobre un
    /// hallazgo resuelto o silenciado tampoco, que es lo que el estado decide en
    /// <see cref="SnippetReader.ForFinding"/>.
    /// </summary>
    public bool CanVerify { get; init; } = OffersVerify(State);

    /// <summary>
    /// La regla del ANCLAJE, sin panel delante: qué estados del código se arreglan verificando. El
    /// estado del hallazgo puede retirarla después, nunca añadirla.
    /// </summary>
    public static bool OffersVerify(SnippetState state)
        => state is SnippetState.Cambiado or SnippetState.Movido or SnippetState.Reanclado
            or SnippetState.NoLocalizado or SnippetState.FicheroNoEncontrado;

    /// <summary>Título del panel: el miembro cuando se supo derivar, si no la ruta y la línea.</summary>
    public string Caption(string path, int line)
    {
        // Sin línea que resaltar (no localizado) no se escribe un «:0» que no significa nada.
        string where = line > 0 ? $"{path}:{line}" : path;
        return Member is not null ? $"{Member} · {where}" : where;
    }
}

/// <summary>
/// Lee de la copia de trabajo el <b>código que hay ahora</b> alrededor de un hallazgo (F5.5 §3).
/// <para>
/// <b>La regla.</b> Nunca enseñar código viejo como si fuera actual. El snippet sale siempre del
/// clon local; lo que el auditor vio no se guarda —de la ubicación solo se conserva el
/// <see cref="Location.SnippetHash"/>, que es un ancla, no una copia— así que cuando el hash deja
/// de casar la única salida honesta es decirlo y ofrecer verificar, no pintar algo plausible.
/// </para>
/// <para>
/// <b>La cadena de anclaje</b> (F5.6, D-222). Por orden de fiabilidad: el hash en la línea
/// guardada, el hash en cualquier otra línea, el <b>símbolo</b> del hallazgo vía Roslyn y, si nada
/// aparece, «no localizado» sin resaltar nada. El número de línea guardado nunca manda por sí
/// solo: lo emite el LLM al reportar y es aproximado —en el hub real se desviaba hasta 25 líneas—,
/// de modo que anclarse a él a ciegas era lo que hacía resaltar comentarios de documentación.
/// </para>
/// <para>
/// <b>Y el aviso depende del ESTADO, no solo del hash</b> (F6.7). El anclaje dice qué relación hay
/// entre el clon y lo que se auditó; el estado del hallazgo dice si eso es un problema. «El código
/// de la línea X ya no es el que se auditó» es un aviso legítimo sobre un hallazgo activo —hay
/// deriva sin verificar— y un sinsentido sobre uno resuelto, donde ese cambio es justamente el
/// arreglo. Ver <see cref="ForFinding"/>.
/// </para>
/// </summary>
public static class SnippetReader
{
    public static SnippetPanel Read(string? clonePath, Location? loc, string? anchoredCommit)
        => Read(clonePath, loc, anchoredCommit, Array.Empty<string>());

    /// <summary>
    /// El panel de un hallazgo concreto: el anclaje, y encima la lectura que corresponde a su
    /// ESTADO (F6.7). Es la entrada que usa la ficha; las sobrecargas de <see cref="Read"/> son la
    /// capa de anclaje a secas y no saben nada del hallazgo.
    /// </summary>
    public static SnippetPanel ForFinding(
        string? clonePath, Finding finding, IReadOnlyList<FixRecord>? fixes = null)
    {
        Location? loc = finding.Locations.FirstOrDefault();
        SnippetPanel panel = Read(
            clonePath, loc, finding.LastConfirmed.Commit,
            SymbolAnchor.Candidates(finding.Symbol, finding.Title));

        // F12 §H.5 — LA APLICACIÓN NO SE EXTRAÑA DE SU PROPIO TRABAJO. Si el código que hay ahora
        // es exactamente el que dejó un arreglo de Atalaya sobre ESTE hallazgo, decir «ya no es el
        // que se auditó» es técnicamente cierto y desorientador: suena a que alguien de fuera tocó
        // el código, cuando lo tocó ella misma y hace media hora. La huella del arreglo lo sabe.
        if (OwnFixOn(clonePath, loc, finding, fixes) is { } own)
        {
            panel = panel with
            {
                Notice = own,
                Fact = own,
                Tone = SnippetTone.Aviso,
            };
        }

        return finding.Status switch
        {
            // RESUELTO. Que el código de la línea ya no sea el que se auditó es exactamente lo que
            // se esperaba: es el arreglo. El aviso de deriva se sustituye por la nota del estado, y
            // no queda nada que verificar desde esta franja.
            FindingStatus.Resuelto => panel with
            {
                Notice = Resolved(panel, finding, loc),
                Tone = SnippetTone.Nota,
                CanVerify = false,
            },

            // SILENCIADO. Se decidió no arreglarlo: la deriva del código no le pide nada a nadie.
            // Lo único que sobrevive es la explicación de un panel VACÍO — sin ella la ficha
            // enseñaría un hueco sin decir por qué.
            FindingStatus.Silenciado => panel with
            {
                Notice = panel.HasCode ? string.Empty : panel.Fact,
                Tone = SnippetTone.Nota,
                CanVerify = false,
            },

            _ => panel,
        };
    }

    /// <summary>
    /// El aviso de un hallazgo cuyo código lo cambió un arreglo de la propia aplicación (F12 §H.5),
    /// o <c>null</c> si no fue así.
    /// <para>
    /// <b>Es una prueba, no una suposición</b>, y usa la misma huella que la deriva (F9 §2): el
    /// contenido actual del fichero tiene que casar, byte a byte y normalizado, con el que el
    /// arreglo dejó escrito. Si el usuario enmendó, aplastó o rehizo el cambio, ya no casa y el
    /// aviso vuelve a ser el genérico — que es la dirección segura.
    /// </para>
    /// <para>
    /// Solo se aplica a hallazgos ACTIVOS: sobre uno resuelto el aviso ya lo escribe
    /// <see cref="Resolved"/>, y sobre uno silenciado no hay nada que pedir.
    /// </para>
    /// </summary>
    private static string? OwnFixOn(
        string? clonePath, Location? loc, Finding finding, IReadOnlyList<FixRecord>? fixes)
    {
        if (loc is null || fixes is null || fixes.Count == 0
            || finding.Status != FindingStatus.Activo
            || string.IsNullOrWhiteSpace(clonePath))
        {
            return null;
        }

        string id = finding.Id.ToString();
        string path = CodeAnchor.NormalizePath(loc.Path);
        string? current = TryContentHash(clonePath, loc.Path);
        if (current is null)
        {
            return null;
        }

        FixRecord? mine = fixes
            .Where(r => string.Equals(r.FindingId, id, StringComparison.Ordinal))
            .OrderByDescending(r => r.Utc)
            .FirstOrDefault(r => r.Files.Any(f =>
                CodeAnchor.NormalizePath(f.Path) == path
                && string.Equals(f.ContentHash, current, StringComparison.OrdinalIgnoreCase)));

        return mine is null
            ? null
            : $"Este código lo cambió el arreglo de Atalaya el "
              + $"{mine.Utc.ToLocalTime():dd/MM/yyyy}; pendiente de verificar.";
    }

    /// <summary>La huella del fichero tal y como está AHORA en el clon, o null si no se puede leer.</summary>
    private static string? TryContentHash(string clonePath, string path)
    {
        try
        {
            string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(abs)
                ? Atalaya.Domain.Hashing.HashUtil.NormalizedContentHash(File.ReadAllBytes(abs))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lo que se lee encima del código de un hallazgo resuelto. Con código delante, la nota del
    /// arreglo; sin él, el hecho a secas precedido del sello de la resolución — nunca una llamada a
    /// verificar, que es lo que este estado ya no necesita.
    /// </summary>
    private static string Resolved(SnippetPanel panel, Finding finding, Location? loc)
    {
        ResolutionStamp? stamp = finding.Resolved;
        string sha = ShortSha(stamp?.Commit ?? finding.LastConfirmed.Commit);
        string when = (stamp?.Utc ?? finding.LastConfirmed.Utc).ToLocalTime().ToString("dd/MM/yyyy");

        if (panel.HasCode)
        {
            return $"Resuelto — el código actual incluye el arreglo (verificado en {sha}, {when}).";
        }

        // Sin código que enseñar la nota positiva sería una afirmación sin respaldo: se dice qué
        // pasó y por qué el panel está vacío, y ahí se acaba.
        return panel.Fact.Length == 0
            ? $"Resuelto en {sha} el {when}."
            : $"Resuelto en {sha} el {when}. {panel.Fact}";
    }

    /// <inheritdoc cref="Read(string?,Location?,string?)"/>
    /// <param name="symbols">
    /// Nombres de miembro que el hallazgo conoce (<see cref="SymbolAnchor.Candidates"/>), para
    /// re-anclar cuando el hash ya no casa.
    /// </param>
    public static SnippetPanel Read(
        string? clonePath, Location? loc, string? anchoredCommit, IReadOnlyList<string> symbols)
    {
        if (loc is null)
        {
            return SnippetPanel.Empty(
                SnippetState.SinUbicacion,
                "Este hallazgo no tiene ninguna ubicación en el código.");
        }

        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return SnippetPanel.Empty(
                SnippetState.SinClon,
                $"Sin clon local en esta máquina: no hay código que mostrar. "
                + $"El hallazgo quedó anclado a {loc.Path}:{loc.Line} en el commit {ShortSha(anchoredCommit)}.");
        }

        string abs = Path.Combine(clonePath, loc.Path.Replace('/', Path.DirectorySeparatorChar));
        string[] lines;
        try
        {
            if (!File.Exists(abs))
            {
                return Panel(
                    SnippetPanel.Empty(SnippetState.FicheroNoEncontrado, string.Empty),
                    $"El fichero ya no está en el clon: {loc.Path}.",
                    "Verifica para re-anclar el hallazgo.");
            }

            lines = File.ReadAllLines(abs);
        }
        catch (Exception ex)
        {
            return SnippetPanel.Empty(
                SnippetState.FicheroNoEncontrado,
                $"No se pudo leer {loc.Path}: {ex.Message}");
        }

        if (lines.Length == 0)
        {
            return SnippetPanel.Empty(
                SnippetState.Cambiado,
                "El fichero está vacío en el clon: el código ha cambiado desde la última confirmación.");
        }

        (SnippetState state, int line, string fact, string action) = Locate(lines, loc, symbols, anchoredCommit);

        // Aunque el ancla case letra por letra, si apunta a documentación se baja al código del
        // miembro (D-224): el auditor a veces señala el `/// <param>` que describe el defecto, y
        // resaltar ese comentario es lo que hacía dudar del hallazgo entero. No es motivo de aviso
        // —no ha cambiado nada—, solo de resaltar donde toca.
        if (state is SnippetState.Anclado or SnippetState.Movido)
        {
            line = SymbolAnchor.FirstCodeLine(lines, loc.Path, line);
        }

        CodeSpanLines span = MethodBoundary.ForLine(lines, line, loc.Path);
        string text = string.Join("\n", lines[(span.StartLine - 1)..span.EndLine]);

        // «No localizado» enseña el contexto pero NO señala ninguna línea: resaltar una al azar es
        // peor que admitir que se perdió el rastro (D-225).
        int highlight = state == SnippetState.NoLocalizado ? 0 : line;

        return Panel(new SnippetPanel(state, text, span.StartLine, highlight, span.Member, string.Empty), fact, action);
    }

    /// <summary>
    /// Junta el hecho y su llamada a la acción en el texto que se pinta, y guarda el hecho aparte
    /// para quien no pueda pedir nada (F6.7).
    /// </summary>
    private static SnippetPanel Panel(SnippetPanel panel, string fact, string action)
        => panel with
        {
            Notice = action.Length == 0 ? fact : $"{fact} {action}",
            Fact = fact,
        };

    /// <summary>
    /// Dónde está ahora el código del hallazgo, qué hay que decir si no está donde estaba, y qué
    /// se le pide al usuario — separado, porque no a todo hallazgo se le puede pedir algo.
    /// </summary>
    private static (SnippetState State, int Line, string Fact, string Action) Locate(
        string[] lines, Location loc, IReadOnlyList<string> symbols, string? anchoredCommit)
    {
        bool inRange = loc.Line >= 1 && loc.Line <= lines.Length;

        if (!string.IsNullOrEmpty(loc.SnippetHash))
        {
            if (inRange && CodeAnchor.ComputeSnippetHash(lines[loc.Line - 1]) == loc.SnippetHash)
            {
                return (SnippetState.Anclado, loc.Line, string.Empty, string.Empty);
            }

            int moved = LocationAnchor.FindByHash(lines, loc.SnippetHash, loc.Line);
            if (moved > 0)
            {
                return (SnippetState.Movido, moved,
                    $"El hallazgo se anotó en la línea {loc.Line} y su código está en la {moved}. "
                    + "Se muestra la posición actual.", string.Empty);
            }
        }
        else if (inRange)
        {
            // Sin ancla no hay con qué contrastar: vale la línea guardada, pero nunca un comentario.
            int code = SymbolAnchor.FirstCodeLine(lines, loc.Path, loc.Line);
            return code == loc.Line
                ? (SnippetState.Anclado, loc.Line, string.Empty, string.Empty)
                : (SnippetState.Reanclado, code,
                    $"La línea {loc.Line} no es código ejecutable. Se resalta la primera línea de "
                    + "código del miembro que la contiene.", string.Empty);
        }

        // El código exacto no aparece: queda el símbolo. Y CON EL SÍMBOLO A LA VISTA NUNCA SE
        // DICE «NO LOCALIZADO» (BUGFIX-ANCLA): el miembro está en el fichero, se resalta su
        // primera línea ejecutable y se dice la verdad —el ancla se perdió, el método está—.
        // Esto NO re-ancla en disco: D-226 sigue prohibiéndolo desde la ficha; aquí se pinta
        // bien y se pide una verificación, que es quien sí puede escribirlo.
        SymbolHit hit = SymbolAnchor.FindMember(lines, loc.Path, symbols);
        if (hit.Found)
        {
            return (SnippetState.Reanclado, hit.Line,
                $"El código anclado ya no está en la línea {loc.Line} (commit "
                + $"{ShortSha(anchoredCommit)}); se enseña «{hit.Member}» actual.",
                "Verifica para confirmarlo o cerrarlo.");
        }

        return (SnippetState.NoLocalizado, inRange ? loc.Line : lines.Length,
            $"No localizado: ni el código anclado en la línea {loc.Line} ni el símbolo del hallazgo "
            + $"aparecen ya en {loc.Path} (commit anclado {ShortSha(anchoredCommit)}). No se resalta "
            + "ninguna línea.", "Verifica para re-anclarlo o cerrarlo.");
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "desconocido" : (sha!.Length <= 8 ? sha : sha[..8]);
}
