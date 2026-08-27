using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>De dónde sale la fecha de inicio de un ciclo. Se declara, como todo dato (N-2).</summary>
public enum CycleStartSource
{
    /// <summary>No hay traza de cuándo se abrió. Pasa siempre en el ciclo 1.</summary>
    Unknown,

    /// <summary>Inferida de la primera sesión registrada en él: existía al menos desde entonces.</summary>
    FirstSession,

    /// <summary>Exacta: el cierre o el reset que abrió este ciclo lo dejó fechado.</summary>
    Opened,
}

/// <summary>Cuándo empezó un ciclo y con qué respaldo.</summary>
public sealed record CycleStart(DateTimeOffset? When, CycleStartSource Source);

/// <summary>
/// Lo que el panel lateral de V2 cuenta sobre el ciclo en curso (F5.6 §5).
/// <para>
/// <b>Por qué se deriva y no se guarda.</b> <c>InventoryCycle</c> nunca tuvo fecha de creación, y
/// añadírsela ahora no arreglaría el caso que importa: los ciclos que YA existen —los del piloto,
/// abiertos a golpe de reset— seguirían sin ella. Pero el dato está: tanto el cierre
/// (<see cref="AuditMode.Cierre"/>) como el reset (<see cref="AuditMode.Reset"/>) escriben su
/// sesión con el <c>CycleN</c> del ciclo que ABREN, así que esa sesión es literalmente el momento
/// en que empezó. Cuando no la hay se dice de dónde sale la fecha, o que no se sabe.
/// </para>
/// </summary>
public static class CycleSummary
{
    /// <summary>
    /// Un ciclo empieza cuando alguien lo abre: un cierre del anterior o un reset. El ciclo 1 no
    /// lo abre nadie —nace con el primer escaneo—, así que ahí se cae siempre al respaldo.
    /// </summary>
    public static CycleStart StartOf(IReadOnlyList<AuditSession> sessions, int cycleN)
    {
        var ofCycle = sessions.Where(s => s.CycleN == cycleN).OrderBy(s => s.StartedUtc).ToList();

        AuditSession? opening = ofCycle.FirstOrDefault(s => s.Mode is AuditMode.Cierre or AuditMode.Reset);
        if (opening is not null)
        {
            return new CycleStart(opening.StartedUtc, CycleStartSource.Opened);
        }

        return ofCycle.Count > 0
            ? new CycleStart(ofCycle[0].StartedUtc, CycleStartSource.FirstSession)
            : new CycleStart(null, CycleStartSource.Unknown);
    }

    /// <summary>
    /// Cuántas AUDITORÍAS se han lanzado en este ciclo. El cierre y el reset también dejan sesión,
    /// pero nadie los «lanza»: contarlos convertiría el número en algo que no se puede explicar en
    /// una frase, que es justo lo que hay que poder hacer con él.
    /// </summary>
    public static int LaunchesIn(IReadOnlyList<AuditSession> sessions, int cycleN)
        => sessions.Count(s => s.CycleN == cycleN && !IsSystemEvent(s.Mode));

    /// <summary>
    /// Lo que NO es una auditoría lanzada: el cierre y el reset (que nadie «lanza») y, desde F6.9,
    /// el arreglo asistido — que gasta tokens y deja sesión, pero no audita ni una unidad.
    /// Contarlo aquí haría que «3 auditorías en este ciclo» dejara de poder explicarse en una frase.
    /// </summary>
    private static bool IsSystemEvent(AuditMode mode)
        => mode is AuditMode.Cierre or AuditMode.Reset or AuditMode.Fix;

    /// <summary>«Ciclo 5 · iniciado 12 ago 2026». Sin fecha fiable, solo el número.</summary>
    public static string Label(int cycleN, CycleStart start) => start switch
    {
        { When: { } when, Source: CycleStartSource.Opened } => $"Ciclo {cycleN} · iniciado {Date(when)}",
        { When: { } when, Source: CycleStartSource.FirstSession } => $"Ciclo {cycleN} · activo desde {Date(when)}",
        _ => $"Ciclo {cycleN}",
    };

    /// <summary>
    /// Qué es un ciclo, para quien abre la aplicación por primera vez y no lo distingue de una
    /// sesión. La segunda frase, cuando hace falta, dice de dónde sale la fecha de al lado.
    /// </summary>
    public static string Tooltip(CycleStart start)
    {
        const string What = "Una vuelta completa al inventario. Se cierra al auditar todas las "
                            + "unidades; los resets abren ciclo nuevo.";

        return start.Source switch
        {
            CycleStartSource.Opened => What,
            CycleStartSource.FirstSession =>
                What + " La fecha es la de la primera sesión registrada en él: pudo abrirse antes.",
            _ => What + " Este ciclo no registra cuándo se abrió.",
        };
    }

    private static string Date(DateTimeOffset when) => when.ToLocalTime().ToString("d MMM yyyy");
}
