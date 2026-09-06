namespace Atalaya.Domain.Model;

/// <summary>Por qué una sesión facturable con tokens se quedó sin coste (F29 §1).</summary>
public enum CostGapReason
{
    /// <summary>Su modelo está escrito y no tiene tarifa en la tabla de la organización.</summary>
    SinTarifa,

    /// <summary>
    /// No hay modelo que valorar: la sesión no lo registró, o registró <c>auto</c> —que no es un
    /// modelo—. Ninguna tarifa lo cubre, por muchas que se añadan.
    /// </summary>
    Desconocido,
}

/// <summary>
/// <b>Una sesión sin coste, y qué se puede hacer con ella</b> (F29 §1).
/// </summary>
/// <param name="Model">El modelo que la sesión escribió, tal cual. Vacío si no escribió ninguno.</param>
/// <param name="CallModels">
/// Los modelos REALES que contestaron, leídos de las llamadas. Vacía cuando la sesión no guardó
/// muestras o las guardó sin modelo.
/// </param>
/// <param name="ResolvableByCall">
/// Se puede calcular por llamada <b>sin aproximar nada</b>: cada llamada dice con qué modelo
/// contestó, ese modelo tiene tarifa, y los tokens de las llamadas suman exactamente los de la
/// sesión. Lo último es lo que separa un coste medido de uno repartido a ojo — si las muestras no
/// son la sesión entera, la suma de sus costes tampoco lo sería.
/// </param>
public sealed record SessionCostGap(
    AuditSession Session,
    CostGapReason Reason,
    string Model,
    IReadOnlyList<string> CallModels,
    bool ResolvableByCall);

/// <summary>
/// <b>Qué sesiones se quedaron sin coste y por qué</b> (F29 §1).
/// <para>
/// De aquí salen los tres sitios que lo dicen: la insignia de la tarjeta del Portafolio, la línea
/// del resumen del ciclo y el diálogo de reconciliar. Una sola cuenta, para que la insignia no
/// pueda decir tres y el diálogo listar dos.
/// </para>
/// </summary>
public static class CostReconciler
{
    /// <summary>
    /// Las sesiones de una aplicación que <b>deberían</b> tener coste y no lo tienen.
    /// <para>
    /// Deja fuera lo que no es un hueco: lo que no factura a la organización (no hay coste que
    /// calcular) y lo que no guardó tokens (no hay nada que valorar — D-787 ya decía que eso no
    /// cuenta como parcial, y manchar el aviso con esas sesiones enseña a ignorarlo).
    /// </para>
    /// </summary>
    public static IReadOnlyList<SessionCostGap> GapsOf(
        IEnumerable<AuditSession> sessions,
        ModelRateTable? rates,
        Func<AuditSession, CostReconciliation?>? reconciled = null)
    {
        var gaps = new List<SessionCostGap>();
        foreach (AuditSession session in sessions)
        {
            CostResult cost = CreditCalculator.Calculate(session, rates, reconciled?.Invoke(session));
            if (cost.HasValue
                || cost.Why is CostUnavailable.NotBilled or CostUnavailable.TokensMissing)
            {
                continue;
            }

            gaps.Add(Inspect(session, rates));
        }

        return gaps;
    }

    /// <summary>Lo que se sabe de UNA sesión sin coste: por qué, y con qué se podría cerrar.</summary>
    public static SessionCostGap Inspect(AuditSession session, ModelRateTable? rates)
    {
        string model = session.Model?.Trim() ?? string.Empty;
        CostGapReason reason = model.Length == 0 || ModelIds.IsPlaceholder(model)
            ? CostGapReason.Desconocido
            : CostGapReason.SinTarifa;

        IReadOnlyList<CallSample> samples = CallsOf(session);
        var callModels = samples
            .Select(s => s.Model?.Trim() ?? string.Empty)
            .Where(m => m.Length > 0 && !ModelIds.IsPlaceholder(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SessionCostGap(session, reason, model, callModels, CanCostByCall(session, rates));
    }

    /// <summary>
    /// <b>¿Son las llamadas la sesión entera, y sabe cada una con qué modelo contestó?</b>
    /// <para>
    /// Las tres condiciones son la misma exigencia dicha tres veces: que sumar los costes de las
    /// llamadas dé el coste de la sesión y no una parte de él. Sin muestras no hay nada que sumar;
    /// con una muestra sin modelo —o con un modelo sin tarifa— faltaría un sumando; y si los
    /// tokens de las muestras no cuadran con los de la sesión, lo que se sumaría sería otro
    /// consumo parecido.
    /// </para>
    /// </summary>
    public static bool CanCostByCall(AuditSession session, ModelRateTable? rates)
    {
        IReadOnlyList<CallSample> samples = CallsOf(session);
        if (samples.Count == 0 || rates is null)
        {
            return false;
        }

        foreach (CallSample sample in samples)
        {
            string model = sample.Model?.Trim() ?? string.Empty;
            if (model.Length == 0 || ModelIds.IsPlaceholder(model)
                || rates.Find(model, session.Provider) is null)
            {
                return false;
            }
        }

        return Covers(session, samples);
    }

    /// <summary>Las llamadas registradas de la sesión, de todas sus unidades.</summary>
    public static IReadOnlyList<CallSample> CallsOf(AuditSession session)
        => session.UsageBreakdown.SelectMany(u => u.Samples).ToList();

    /// <summary>Los tokens de las llamadas son EXACTAMENTE los de la sesión, tipo a tipo.</summary>
    private static bool Covers(AuditSession session, IReadOnlyList<CallSample> samples)
        => samples.Sum(s => s.InputTokens) == session.Usage.InputTokens
        && samples.Sum(s => s.OutputTokens) == session.Usage.OutputTokens
        && samples.Sum(s => s.CacheReadTokens) == session.Usage.CacheReadTokens
        && samples.Sum(s => s.CacheWriteTokens) == session.Usage.CacheWriteTokens;
}
