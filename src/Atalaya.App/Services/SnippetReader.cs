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
/// Lo que el panel de código enseña y lo que avisa encima. Las líneas son del <b>fichero</b>.
/// </summary>
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

    public bool HasCode => Text.Length > 0;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>
    /// El aviso lleva un botón «Verificar ahora» solo cuando verificar arregla algo: re-anclar la
    /// ubicación o pedir veredicto. Sin clon, verificar no puede hacer nada desde esta máquina.
    /// </summary>
    public bool CanVerify => OffersVerify(State);

    /// <summary>
    /// La misma regla, sin panel delante: la ficha la necesita para decidir si pinta el botón, y
    /// tenerla escrita dos veces era lo que dejaba los estados nuevos de F5.6 sin su «Verificar».
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
/// </summary>
public static class SnippetReader
{
    public static SnippetPanel Read(string? clonePath, Location? loc, string? anchoredCommit)
        => Read(clonePath, loc, anchoredCommit, Array.Empty<string>());

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
                return SnippetPanel.Empty(
                    SnippetState.FicheroNoEncontrado,
                    $"El fichero ya no está en el clon: {loc.Path}. Verifica para re-anclar el hallazgo.");
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

        (SnippetState state, int line, string notice) = Locate(lines, loc, symbols, anchoredCommit);

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

        return new SnippetPanel(state, text, span.StartLine, highlight, span.Member, notice);
    }

    /// <summary>Dónde está ahora el código del hallazgo, y qué hay que avisar si no está donde estaba.</summary>
    private static (SnippetState State, int Line, string Notice) Locate(
        string[] lines, Location loc, IReadOnlyList<string> symbols, string? anchoredCommit)
    {
        bool inRange = loc.Line >= 1 && loc.Line <= lines.Length;

        if (!string.IsNullOrEmpty(loc.SnippetHash))
        {
            if (inRange && CodeAnchor.ComputeSnippetHash(lines[loc.Line - 1]) == loc.SnippetHash)
            {
                return (SnippetState.Anclado, loc.Line, string.Empty);
            }

            int moved = LocationAnchor.FindByHash(lines, loc.SnippetHash, loc.Line);
            if (moved > 0)
            {
                return (SnippetState.Movido, moved,
                    $"El hallazgo se anotó en la línea {loc.Line} y su código está en la {moved}. "
                    + "Se muestra la posición actual.");
            }
        }
        else if (inRange)
        {
            // Sin ancla no hay con qué contrastar: vale la línea guardada, pero nunca un comentario.
            int code = SymbolAnchor.FirstCodeLine(lines, loc.Path, loc.Line);
            return code == loc.Line
                ? (SnippetState.Anclado, loc.Line, string.Empty)
                : (SnippetState.Reanclado, code,
                    $"La línea {loc.Line} no es código ejecutable. Se resalta la primera línea de "
                    + "código del miembro que la contiene.");
        }

        // El código exacto no aparece: queda el símbolo.
        SymbolHit hit = SymbolAnchor.FindMember(lines, loc.Path, symbols);
        if (hit.Found)
        {
            return (SnippetState.Reanclado, hit.Line,
                $"El código de la línea {loc.Line} ya no es el que se auditó (commit "
                + $"{ShortSha(anchoredCommit)}). El hallazgo se ha re-anclado a «{hit.Member}», que "
                + "es el miembro que nombra. Verifica para confirmarlo.");
        }

        return (SnippetState.NoLocalizado, inRange ? loc.Line : lines.Length,
            $"No localizado: ni el código anclado en la línea {loc.Line} ni el símbolo del hallazgo "
            + $"aparecen ya en {loc.Path} (commit anclado {ShortSha(anchoredCommit)}). No se resalta "
            + "ninguna línea. Verifica para re-anclarlo o cerrarlo.");
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "desconocido" : (sha!.Length <= 8 ? sha : sha[..8]);
}
