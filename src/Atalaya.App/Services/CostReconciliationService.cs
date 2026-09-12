using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Un grupo del diálogo de reconciliar: todas las sesiones que están paradas por lo mismo
/// (F29 §1).
/// </summary>
/// <param name="Reason">Por qué no tienen coste.</param>
/// <param name="Model">
/// El modelo que las paró. En <see cref="CostGapReason.Desconocido"/> es lo que la sesión escribió
/// —<c>auto</c>, o nada—, que no nombra ningún modelo.
/// </param>
/// <param name="Sessions">Las sesiones del grupo, de más reciente a más antigua.</param>
/// <param name="CallModels">
/// Los modelos que de verdad contestaron, cuando las llamadas los guardaron. Es la respuesta a
/// «¿y qué fue "auto" en realidad?».
/// </param>
/// <param name="ResolvableByCall">Todas las sesiones del grupo se pueden valorar llamada a llamada.</param>
public sealed record CostGapGroup(
    CostGapReason Reason,
    string Model,
    IReadOnlyList<AuditSession> Sessions,
    IReadOnlyList<string> CallModels,
    bool ResolvableByCall)
{
    public int Count => Sessions.Count;
}

/// <summary>
/// <b>Lo que cerró una reconciliación</b> (F30 §4): cuántas sesiones, y cuánto suman ya valoradas.
/// <para>
/// El importe no se guarda en ninguna parte —el coste se sigue derivando en cada lectura, que es
/// D-788— pero <b>hay que decirlo aquí</b>: el diálogo se queda abierto al terminar, y «3 sesiones
/// reconciliadas» a secas no contesta la pregunta por la que se abrió, que es cuánto costó aquello.
/// </para>
/// </summary>
/// <param name="Usd">El importe de esas sesiones, en dólares. Null si ninguna se pudo valorar.</param>
public sealed record ReconciliationOutcome(int Sessions, decimal? Usd);

/// <summary>Cuántas sesiones sin coste tiene una aplicación. Lo que dice la insignia.</summary>
public sealed record AppCostGap(string Slug, int Sessions, IReadOnlyList<CostGapGroup> Groups)
{
    /// <summary>«3 sesiones sin coste». Singular y plural, escritos una vez.</summary>
    public string Badge => Sessions == 1 ? "1 sesión sin coste" : $"{Sessions} sesiones sin coste";

    /// <summary>El motivo resumido, para el tooltip de la insignia y la línea del resumen.</summary>
    public string Reason
    {
        get
        {
            var partes = new List<string>();
            foreach (CostGapGroup g in Groups)
            {
                partes.Add(g.Reason == CostGapReason.Desconocido
                    ? g.Model.Length > 0
                        ? $"{g.Count} con modelo «{g.Model}», que no nombra ningún modelo"
                        : $"{g.Count} sin modelo registrado"
                    : $"{g.Count} con «{g.Model}», un modelo sin tarifa");
            }

            return string.Join(" · ", partes);
        }
    }
}

/// <summary>
/// <b>Las sesiones sin coste, y cómo se cierran</b> (F29 §1).
/// <para>
/// <b>Reconciliar no escribe un coste.</b> Escribe lo que FALTABA para poder calcularlo: que las
/// llamadas de esta sesión son con lo que hay que valorarla, o qué tarifa eligió una persona para
/// las que no dicen con qué corrieron. El coste se sigue derivando en cada lectura, como manda
/// D-788 — guardar el número que salió aquel día crearía la segunda verdad que D-788 fue a
/// eliminar, y una tarifa corregida mañana ya no lo alcanzaría.
/// </para>
/// <para>
/// <b>Y se publica con su commit</b>, como cualquier otro cambio compartido: la reconciliación es
/// una decisión sobre un dato del equipo, así que el commit del hub es su atribución — igual que
/// con las tarifas (D-786).
/// </para>
/// </summary>
public sealed class CostReconciliationService
{
    // Los identificadores de los pasos (F30 §4).
    public const string Leer = "leer";
    public const string Calcular = "calcular";
    public const string Escribir = "escribir";
    public const string Publicar = "publicar";

    /// <summary>
    /// <b>Los pasos de reconciliar</b>. Leer y calcular no escriben nada, así que hasta ahí
    /// cancelar deja el hub exactamente como estaba; a partir de escribir, no se ofrece.
    /// </summary>
    public static IReadOnlyList<StepSpec> Plan { get; } = new[]
    {
        new StepSpec(Leer, "Leer las sesiones sin coste", Cancelable: true),
        new StepSpec(Calcular, "Calcular con la tarifa", Cancelable: true),
        new StepSpec(Escribir, "Escribir en el hub"),
        new StepSpec(Publicar, "Publicar"),
    };

    /// <summary>La lista lista para colgarla del diálogo, bajo el botón.</summary>
    public static StepList NewSteps(StepFlow flow = StepFlow.Vertical) => new(Plan, flow);

    private readonly HubContext _hub;
    private readonly ModelRatesService _rates;
    private readonly TimeProvider _time;

    public CostReconciliationService(HubContext hub, ModelRatesService rates, TimeProvider? time = null)
    {
        _hub = hub;
        _rates = rates;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Con qué se pone precio a las sesiones de una aplicación.</summary>
    public CostLookup LookupFor(string slug)
        => new(_rates.Current, ReadReconciliations(slug), _hub.CostTraits);

    /// <summary>Lo mismo para el hub entero: lo que necesitan Métricas y la lista de informes.</summary>
    public CostLookup Lookup()
    {
        var all = new Dictionary<Ulid, CostReconciliation>();
        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            foreach (KeyValuePair<Ulid, CostReconciliation> pair in ReadReconciliations(slug))
            {
                all[pair.Key] = pair.Value;
            }
        }

        return new CostLookup(_rates.Current, all, _hub.CostTraits);
    }

    /// <summary>
    /// Las sesiones sin coste de una aplicación, agrupadas por motivo. Vacío cuando no hay ninguna
    /// — y entonces no hay insignia, ni línea, ni diálogo que abrir.
    /// </summary>
    /// <param name="alsoInclude">
    /// Sesiones que hay que seguir listando aunque YA tengan coste (F29 §1). Es lo que sostiene el
    /// viaje a Ajustes → Tarifas y vuelta: añadir la tarifa que faltaba cierra el hueco por sí sola
    /// —eso es D-788 haciendo su trabajo—, y sin esto el grupo desaparecería del diálogo en lugar de
    /// pasar a «listo para calcular», y sus informes se quedarían sin la línea que dice cuándo se
    /// calculó. Solo se re-listan las que no tengan ya su reconciliación escrita.
    /// </param>
    public AppCostGap GapOf(string slug, IReadOnlyCollection<Ulid>? alsoInclude = null)
    {
        IReadOnlyList<SessionCostGap> gaps = GapsFor(slug, alsoInclude);
        return new AppCostGap(slug, gaps.Count, Group(gaps));
    }

    private IReadOnlyList<SessionCostGap> GapsFor(string slug, IReadOnlyCollection<Ulid>? alsoInclude)
    {
        Dictionary<Ulid, CostReconciliation> reconciled = ReadReconciliations(slug);
        var lookup = new CostLookup(_rates.Current, reconciled, _hub.CostTraits);
        IReadOnlyList<AuditSession> sessions = _hub.Store.ListSessions(slug);

        var gaps = lookup.GapsOf(sessions).ToList();
        if (alsoInclude is not { Count: > 0 })
        {
            return gaps;
        }

        var already = gaps.Select(g => g.Session.Id).ToHashSet();
        foreach (AuditSession session in sessions)
        {
            if (already.Contains(session.Id)
                || !alsoInclude.Contains(session.Id)
                || reconciled.ContainsKey(session.Id))
            {
                continue;
            }

            gaps.Add(CostReconciler.Inspect(
                session, _rates.Current, _hub.CostTraits(session.Provider)));
        }

        return gaps;
    }

    /// <summary>Lo mismo, aplicación por aplicación y solo las que tienen hueco.</summary>
    public IReadOnlyList<AppCostGap> Gaps()
        => _hub.Store.ListAppSlugs()
            .Select(slug => GapOf(slug))
            .Where(g => g.Sessions > 0)
            .ToList();

    /// <summary>
    /// <b>Agrupa por lo que las para</b>, que es lo que el diálogo tiene que enseñar: el remedio de
    /// un grupo vale para todas sus sesiones y para ninguna de las demás.
    /// </summary>
    private static IReadOnlyList<CostGapGroup> Group(IReadOnlyList<SessionCostGap> gaps)
        => gaps
            .GroupBy(g => (g.Reason, Model: g.Model), TupleComparer)
            .Select(g => new CostGapGroup(
                g.Key.Reason,
                g.Key.Model,
                g.Select(x => x.Session).OrderByDescending(s => s.StartedUtc).ToList(),
                g.SelectMany(x => x.CallModels)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList(),

                // El grupo se puede calcular por llamada solo si TODAS pueden: media reconciliación
                // dejaría un botón que dice N y cierra menos de N.
                g.All(x => x.ResolvableByCall)))
            .OrderBy(g => g.Reason)
            .ThenBy(g => g.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly TupleReasonModelComparer TupleComparer = new();

    private sealed class TupleReasonModelComparer : IEqualityComparer<(CostGapReason Reason, string Model)>
    {
        public bool Equals((CostGapReason Reason, string Model) a, (CostGapReason Reason, string Model) b)
            => a.Reason == b.Reason && string.Equals(a.Model, b.Model, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((CostGapReason Reason, string Model) x)
            => HashCode.Combine(x.Reason, x.Model.ToLowerInvariant());
    }

    /// <summary>
    /// <b>Cierra los huecos que se pueden cerrar</b>, y publica. Devuelve cuántas sesiones quedan
    /// reconciliadas.
    /// <para>
    /// Cada sesión se resuelve por el camino que le corresponde y no por el que se pida: si sus
    /// llamadas dicen con qué modelo contestaron, se valora por llamada —eso es una MEDIDA— aunque
    /// haya una tarifa asignada encima. Una tarifa asignada solo gobierna lo que nadie midió.
    /// </para>
    /// </summary>
    /// <param name="assignedModel">
    /// La tarifa que el usuario eligió para las sesiones cuyo modelo no se sabe y cuyas llamadas
    /// tampoco lo dicen. Null si no eligió ninguna: entonces esas sesiones se quedan como están.
    /// </param>
    /// <param name="steps">
    /// Los pasos que se están enseñando (F30 §4). Si no llega uno se crea aquí: el camino que
    /// ejecuta es el mismo se enseñe o no.
    /// </param>
    public ReconciliationOutcome Reconcile(
        string slug, string? assignedModel = null, IReadOnlyCollection<Ulid>? scope = null,
        StepList? steps = null)
    {
        steps ??= NewSteps();
        ModelRateTable? rates = _rates.Current;
        string by = _hub.ResolveIdentity().Name;
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        IReadOnlyList<SessionCostGap> gaps = steps.Run(Leer, () => GapsFor(slug, scope));

        List<(SessionCostGap Gap, CostReconciliation Decision)> decided = steps.Run(Calcular, () =>
            gaps.Select(g => (
                    Gap: g,
                    Decision: Decide(
                        g, rates, assignedModel, by, today, _hub.CostTraits(g.Session.Provider))))
                .Where(p => p.Decision is not null)
                .Select(p => (p.Gap, Decision: p.Decision!))
                .ToList());

        steps.Run(Escribir, () =>
        {
            foreach ((_, CostReconciliation decision) in decided)
            {
                _hub.Store.WriteCostReconciliation(decision);
            }
        });

        // El commit va SIEMPRE dentro de su paso, aunque no haya nada que publicar: un paso que a
        // veces se ejecuta y a veces no es un paso que a veces se enseña, y la lista dejaría de
        // ser la lista.
        bool published = steps.Run(Publicar, () => decided.Count == 0
            || (_hub.Sync?.CommitAndPush(
                $"costes: {decided.Count} sesión(es) de {slug} reconciliadas por {by}") ?? true));

        if (!published)
        {
            steps.Fail(Publicar, StepList.PendingPublish);
        }

        return new ReconciliationOutcome(decided.Count, Valued(slug, decided.Select(d => d.Gap.Session)));
    }

    /// <summary>
    /// <b>Cuánto suman las sesiones que se acaban de cerrar</b>, ya con su reconciliación escrita.
    /// Se lee otra vez del hub a propósito: el coste es un derivado (D-788) y aquí se está diciendo
    /// lo mismo que dirán el resumen y los informes, por el mismo camino.
    /// </summary>
    private decimal? Valued(string slug, IEnumerable<AuditSession> sessions)
    {
        CostLookup lookup = LookupFor(slug);
        decimal? total = null;
        foreach (AuditSession session in sessions)
        {
            if (lookup.Of(session).Usd is { } usd)
            {
                total = (total ?? 0m) + usd;
            }
        }

        return total;
    }

    /// <summary>Qué se puede escribir de esta sesión, o null si todavía nada.</summary>
    private static CostReconciliation? Decide(
        SessionCostGap gap, ModelRateTable? rates, string? assignedModel, string by, DateOnly today,
        ProviderCostTraits traits)
    {
        // Con qué identificador se busca la tarifa: el que declare su casa, que no es siempre el
        // que la sesión escribió (PROV-2 §3).
        string? provider = traits.ProviderId ?? gap.Session.Provider;

        var record = new CostReconciliation
        {
            SessionId = gap.Session.Id,
            AppSlug = gap.Session.AppSlug,
            By = by,
            On = today,
        };

        // 1. Lo medido gana. Las llamadas dicen con qué modelo contestó cada una y sus tokens son
        //    los de la sesión: el coste sale de sumarlas, y eso no es una estimación.
        if (gap.ResolvableByCall)
        {
            record.How = CostResolution.PorLlamada;
            record.Models = gap.CallModels.ToList();
            return record;
        }

        // 2. El modelo de la sesión ya tiene tarifa: alguien la añadió mientras el diálogo estaba
        //    abierto. No hay nada que elegir —el coste ya sale de la fórmula de siempre—, solo que
        //    dejar constancia de cuándo se cerró: es lo que el informe enseña como «calculado a
        //    posteriori».
        if (gap.Reason == CostGapReason.SinTarifa && rates?.Find(gap.Model, provider) is not null)
        {
            record.How = CostResolution.TarifaAnadida;
            return record;
        }

        // 3. Y lo que nadie midió, valorado con la tarifa que una persona eligió. Sin elección, la
        //    sesión se queda sin coste: inventar una tarifa «parecida» es lo que D-787 prohíbe.
        if (gap.Reason == CostGapReason.Desconocido
            && assignedModel is { Length: > 0 }
            && rates?.Find(assignedModel, provider) is not null)
        {
            record.How = CostResolution.TarifaAsignada;
            record.AssignedModel = assignedModel;
            return record;
        }

        return null;
    }

    private Dictionary<Ulid, CostReconciliation> ReadReconciliations(string slug)
    {
        var map = new Dictionary<Ulid, CostReconciliation>();
        try
        {
            foreach (CostReconciliation r in _hub.Store.ListCostReconciliations(slug))
            {
                map[r.SessionId] = r;
            }
        }
        catch (Exception)
        {
            // Un fichero a medio escribir por un merge no puede dejar el Portafolio sin pintarse:
            // sin reconciliación, la sesión vuelve a salir sin coste, que es lo que era.
        }

        return map;
    }
}
