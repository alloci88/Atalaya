using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Cuánto respaldo tiene la estimación. Se DECLARA siempre (N-2).</summary>
public enum CostEvidence
{
    /// <summary>No hay ni una unidad con coste medido: no se estima nada.</summary>
    Ninguna,

    /// <summary>Hay medidas, pero pocas: vale como orden de magnitud y se dice.</summary>
    Escasa,

    /// <summary>Varias sesiones y varias unidades detrás de la media.</summary>
    Suficiente,
}

/// <summary>
/// La estimación de lo que va a costar un lanzamiento, con su procedencia pegada (F5.6 §4).
/// Nunca es un bloqueo: es un número para decidir, y por eso lleva siempre de dónde sale.
/// </summary>
/// <param name="Units">Unidades seleccionadas.</param>
/// <param name="MaxPasses">Tope de pasadas vigente con el que se lanzaría.</param>
/// <param name="CostPerUnit">Coste medio por unidad observado, o <c>null</c> si no hay historial.</param>
/// <param name="Total">La estimación, o <c>null</c> si no hay con qué calcularla.</param>
/// <param name="CostUnit">La unidad de coste que declara el SDK, tal cual la guardó la sesión.</param>
/// <param name="SampleUnits">Cuántas unidades medidas hay detrás de la media.</param>
/// <param name="SampleSessions">De cuántas sesiones salen.</param>
/// <param name="ObservedMaxPasses">Con qué tope se midieron. 0 si el historial no lo registra.</param>
/// <param name="PassFactor">Cuánto se escala por la diferencia de tope. 1 cuando se midió con el mismo.</param>
public sealed record CostEstimate(
    int Units,
    int MaxPasses,
    decimal? CostPerUnit,
    decimal? Total,
    string CostUnit,
    int SampleUnits,
    int SampleSessions,
    int ObservedMaxPasses,
    decimal PassFactor,
    CostEvidence Evidence)
{
    /// <summary>Cuántas unidades, en castellano.</summary>
    public string UnitsLabel => Units == 1 ? "1 unidad" : $"{Units} unidades";

    /// <summary>
    /// El cálculo en una línea, con sus factores a la vista: «47 unidades × ~18/unidad ≈ 850
    /// unidades SDK». Un total suelto no se puede contrastar; el desglose sí.
    /// </summary>
    public string Breakdown
    {
        get
        {
            if (Total is not { } total || CostPerUnit is not { } per)
            {
                return $"{UnitsLabel} · coste desconocido";
            }

            string factor = PassFactor == 1m
                ? string.Empty
                : $" × {MaxPasses}/{ObservedMaxPasses} pasadas";

            return $"{UnitsLabel} × ~{per:0.##}/unidad{factor} ≈ {total:0.##} {CostUnit}";
        }
    }

    /// <summary>
    /// De dónde sale el número (N-2). Es la mitad que convierte una cifra en un dato: sin esto,
    /// «≈ 850» y «me lo he inventado» se leen igual.
    /// </summary>
    public string Provenance
    {
        get
        {
            if (Evidence == CostEvidence.Ninguna)
            {
                return "Sin coste medido en el historial de esta aplicación: no hay con qué estimar.";
            }

            string sessions = SampleSessions == 1 ? "la última sesión" : $"las últimas {SampleSessions} sesiones";
            string units = SampleUnits == 1 ? "1 unidad medida" : $"{SampleUnits} unidades medidas";
            string head = Evidence == CostEvidence.Escasa
                ? $"Estimación con pocos datos: {units} en {sessions}."
                : $"Media de {units} en {sessions}.";

            if (PassFactor != 1m)
            {
                head += $" Se midieron con un tope de {ObservedMaxPasses} pasadas y el vigente es {MaxPasses}: "
                        + "el escalado es proporcional, así que sobreestima cuando el barrido se seca antes.";
            }
            else if (ObservedMaxPasses == 0)
            {
                head += " El historial no registra con qué tope se midieron, así que no se escala.";
            }

            return head;
        }
    }

    /// <summary>La estimación no bloquea nada; el diálogo la enseña aunque no haya número.</summary>
    public bool HasNumber => Total is not null;
}

/// <summary>
/// Estima el coste de una tanda a partir de lo que YA se gastó (F5.6 §4).
/// <para>
/// <b>De dónde sale el dato.</b> De <see cref="AuditSession.UsageBreakdown"/>, el desglose por
/// unidad que la instrumentación (Hito 1a) guarda en cada fichero de sesión. No hay ninguna tabla
/// de precios ni ninguna constante: si el hub no tiene una sola unidad con coste medido, la
/// estimación no existe y se dice — es la regla N-2 aplicada al dinero.
/// </para>
/// <para>
/// <b>El tope de pasadas.</b> Cada sesión registra el suyo. Se prefieren las sesiones que corrieron
/// con el MISMO tope que el vigente, y entonces no hay nada que escalar. Solo cuando no hay
/// ninguna se extrapola, y el factor se enseña en el desglose para que se vea que es extrapolado.
/// </para>
/// </summary>
public sealed class CostEstimator
{
    /// <summary>Cuántas sesiones recientes entran en la media. Más allá, el dato ya no es «reciente».</summary>
    public const int RecentSessions = 5;

    /// <summary>Por debajo de esto la estimación se declara «con pocos datos».</summary>
    public const int MinSessions = 2;

    /// <inheritdoc cref="MinSessions"/>
    public const int MinUnits = 3;

    /// <summary>Lo que se lee cuando el SDK no declara unidad de coste. Igual que en los informes.</summary>
    public const string DefaultCostUnit = "unidades SDK";

    private readonly HubContext _hub;

    public CostEstimator(HubContext hub) => _hub = hub;

    /// <summary>Estima para una aplicación del hub.</summary>
    public CostEstimate Estimate(string slug, int units, int maxPasses)
        => Estimate(_hub.Store.ListSessions(slug), units, maxPasses);

    /// <summary>El cálculo, sobre una lista de sesiones y nada más. Puro: se prueba sin hub.</summary>
    public static CostEstimate Estimate(IReadOnlyList<AuditSession> sessions, int units, int maxPasses)
    {
        maxPasses = Math.Max(1, maxPasses);

        var measured = sessions
            .Where(s => s.UsageBreakdown.Any(u => u.Cost is not null))
            .OrderByDescending(s => s.StartedUtc)
            .ToList();

        if (measured.Count == 0)
        {
            return new CostEstimate(
                units, maxPasses, null, null, DefaultCostUnit, 0, 0, 0, 1m, CostEvidence.Ninguna);
        }

        // Like-for-like primero: si ya se auditó con este mismo tope, no hace falta extrapolar nada.
        var sameCap = measured.Where(s => s.MaxPassesPerUnit == maxPasses).Take(RecentSessions).ToList();

        List<AuditSession> chosen;
        int observedCap;
        decimal factor;
        if (sameCap.Count > 0)
        {
            chosen = sameCap;
            observedCap = maxPasses;
            factor = 1m;
        }
        else
        {
            chosen = measured.Take(RecentSessions).ToList();
            var caps = chosen.Where(s => s.MaxPassesPerUnit > 0).Select(s => s.MaxPassesPerUnit).ToList();
            observedCap = caps.Count > 0 ? (int)Math.Round(caps.Average(), MidpointRounding.AwayFromZero) : 0;
            factor = observedCap > 0 ? (decimal)maxPasses / observedCap : 1m;
        }

        var samples = chosen
            .SelectMany(s => s.UsageBreakdown)
            .Where(u => u.Cost is not null)
            .Select(u => u.Cost!.Value)
            .ToList();

        decimal perUnit = samples.Average();
        string costUnit = chosen
            .Select(s => s.Usage.Currency)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? DefaultCostUnit;

        CostEvidence evidence = chosen.Count >= MinSessions && samples.Count >= MinUnits
            ? CostEvidence.Suficiente
            : CostEvidence.Escasa;

        return new CostEstimate(
            units,
            maxPasses,
            perUnit,
            units * perUnit * factor,
            costUnit,
            samples.Count,
            chosen.Count,
            observedCap,
            factor,
            evidence);
    }
}
