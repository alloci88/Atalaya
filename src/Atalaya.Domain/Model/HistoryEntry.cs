namespace Atalaya.Domain.Model;

/// <summary>The kinds of events recorded in a finding's <c>history</c> (§2).</summary>
public enum FindingEvent
{
    Detected,
    Confirmed,
    Resolved,
    Reopened,
    Assigned,
    SeverityChanged,
    Silenced,
    Unsilenced,
    Commented,
    /// <summary>LEGADO — no se emite desde F4. Se conserva para leer historiales anteriores.</summary>
    Recurrence,
    /// <summary>An assisted-fix branch was proposed (§5.7, H9).</summary>
    FixProposed,

    /// <summary>
    /// El auditor sostuvo que esto nunca fue un defecto (F5.1b). NO cierra el hallazgo: lo deja
    /// marcado como disputado a la espera de que lo decida una persona.
    /// </summary>
    Disputed,

    /// <summary>Una persona cerró la disputa por gobernanza (F5.1b).</summary>
    DisputeCleared,

    /// <summary>
    /// Ni el código anclado ni el símbolo del hallazgo aparecen ya donde estaban (F6.6). NO es una
    /// reapertura: un hallazgo activo no puede reabrirse, y etiquetar así el rastro perdido hacía
    /// leer «volvió el defecto» donde solo había código movido.
    /// </summary>
    NotLocated,

    /// <summary>
    /// El ancla exacta se perdió pero el <b>símbolo</b> sigue ahí y el hallazgo se re-ancló a él
    /// (F6.6). Se anota solo cuando el re-anclaje es lo ÚNICO que pasó: si detrás vino un
    /// veredicto, el veredicto es el evento y esto sería eco.
    /// </summary>
    Reanchored,
}

/// <summary>One immutable entry in a finding's audit trail.</summary>
public sealed record HistoryEntry(DateTimeOffset Utc, FindingEvent Event, string By, string? Detail)
{
    /// <summary>
    /// La sesión que provocó el evento, cuando la hubo (H9.1). Hoy lo escribe el arreglo asistido
    /// —<see cref="FindingEvent.FixProposed"/>— para que del historial de la ficha se pueda abrir
    /// el informe de ese arreglo.
    /// <para>
    /// Es opcional y va como propiedad, no en la posición: las entradas escritas antes de H9.1 no
    /// la traen, y todas las llamadas de siempre siguen construyéndose igual.
    /// </para>
    /// </summary>
    public string? SessionId { get; init; }
}
