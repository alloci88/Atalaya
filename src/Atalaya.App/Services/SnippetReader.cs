using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>En qué relación está el código del clon con lo que el auditor vio (F5.5 §3).</summary>
public enum SnippetState
{
    /// <summary>La línea anclada sigue diciendo exactamente lo mismo. Lo que se ve es lo que hay.</summary>
    Anclado,

    /// <summary>El código es el mismo pero se ha movido de sitio: se enseña dónde está ahora.</summary>
    Movido,

    /// <summary>La línea anclada ya no coincide y no aparece en el fichero: el código cambió.</summary>
    Cambiado,

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
    public bool CanVerify => State is SnippetState.Cambiado or SnippetState.Movido or SnippetState.FicheroNoEncontrado;

    /// <summary>Título del panel: el miembro cuando se supo derivar, si no la ruta y la línea.</summary>
    public string Caption(string path, int line)
        => Member is not null ? $"{Member} · {path}:{line}" : $"{path}:{line}";
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
/// Tres desenlaces distintos, y se distinguen a propósito: la línea sigue igual
/// (<see cref="SnippetState.Anclado"/>), el mismo código apareció en otro sitio del fichero
/// (<see cref="SnippetState.Movido"/> — se enseña la posición nueva, no la vieja) o no aparece
/// (<see cref="SnippetState.Cambiado"/>). Meterlos en el mismo saco convertía un simple
/// desplazamiento de líneas en una alarma, y un cambio real en un silencio.
/// </para>
/// </summary>
public static class SnippetReader
{
    public static SnippetPanel Read(string? clonePath, Location? loc, string? anchoredCommit)
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

        (SnippetState state, int line, string notice) = Locate(lines, loc, anchoredCommit);

        CodeSpanLines span = MethodBoundary.ForLine(lines, line, loc.Path);
        string text = string.Join("\n", lines[(span.StartLine - 1)..span.EndLine]);

        return new SnippetPanel(state, text, span.StartLine, line, span.Member, notice);
    }

    /// <summary>Dónde está ahora la línea anclada, y qué hay que avisar si no está donde estaba.</summary>
    private static (SnippetState State, int Line, string Notice) Locate(
        string[] lines, Location loc, string? anchoredCommit)
    {
        bool inRange = loc.Line >= 1 && loc.Line <= lines.Length;

        if (string.IsNullOrEmpty(loc.SnippetHash))
        {
            // Sin ancla no hay nada que comparar: se muestra la línea guardada y punto.
            return inRange
                ? (SnippetState.Anclado, loc.Line, string.Empty)
                : (SnippetState.Cambiado, lines.Length,
                    $"La línea {loc.Line} ya no existe: el fichero tiene {lines.Length}. "
                    + "El código ha cambiado desde la última confirmación.");
        }

        if (inRange && CodeAnchor.ComputeSnippetHash(lines[loc.Line - 1]) == loc.SnippetHash)
        {
            return (SnippetState.Anclado, loc.Line, string.Empty);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (CodeAnchor.ComputeSnippetHash(lines[i]) == loc.SnippetHash)
            {
                return (SnippetState.Movido, i + 1,
                    $"El código se ha movido: estaba en la línea {loc.Line} y ahora está en la {i + 1}. "
                    + "Se muestra la posición actual.");
            }
        }

        return (SnippetState.Cambiado, inRange ? loc.Line : lines.Length,
            $"El código ha cambiado desde la última confirmación (commit {ShortSha(anchoredCommit)}): "
            + "lo que se ve debajo ya no es lo que se auditó.");
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "desconocido" : (sha!.Length <= 8 ? sha : sha[..8]);
}
