using System.Globalization;
using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Qué clase de informe es (F6.3). Sale del modo de la sesión que lo escribió y, cuando no hay
/// sesión, de lo que declare su propia cabecera.
/// </summary>
public enum ReportKind
{
    /// <summary>Una auditoría: lotes o los modos retirados de las sesiones antiguas.</summary>
    Sesion,

    /// <summary>
    /// Una verificación (F16 §F). Tiene clase propia y no se mezcla con las auditorías porque
    /// responde a otra pregunta —«¿sigue estando este hallazgo?» y no «¿qué hay en esta unidad?»—,
    /// cuesta dinero, decide estados, y quien busca «qué se ha verificado esta semana» no tiene por
    /// qué bucear entre auditorías para encontrarlo.
    /// </summary>
    Verificacion,

    /// <summary>El consolidado de un cierre de ciclo (§7).</summary>
    Consolidado,

    /// <summary>Un evento de sistema: reset de auditoría y lo que venga después.</summary>
    Operaciones,
}

/// <summary>Cómo se nombra cada clase en la insignia y en el combo de filtro.</summary>
public static class ReportKinds
{
    public static string Display(ReportKind kind) => kind switch
    {
        ReportKind.Consolidado => "Consolidado",
        ReportKind.Operaciones => "Operaciones",
        ReportKind.Verificacion => "Verificación",
        _ => "Sesión",
    };
}

/// <summary>
/// De dónde sale la fecha de un informe. Se declara, como todo dato (N-2): «lo dice su sesión» y
/// «lo dice el nombre del fichero» no valen lo mismo, y quien ordena una lista por fecha tiene
/// derecho a saber cuál está leyendo.
/// </summary>
public enum ReportDateSource
{
    /// <summary>La de la sesión que lo escribió. Es la buena.</summary>
    Session,

    /// <summary>La que el propio informe escribe en su cabecera (informes sin sesión).</summary>
    Header,

    /// <summary>La que lleva dentro el ULID del nombre del fichero.</summary>
    Ulid,

    /// <summary>Último recurso: cuándo se escribió el fichero en este clon.</summary>
    File,
}

/// <summary>
/// Una fila de la lista de informes (F6.3 §1), ya resuelta contra su sesión.
/// <para>
/// Los conteos son NULOS y no cero cuando no hay sesión detrás: un informe importado de v4 no
/// declara cuántas unidades procesó, y escribir «0 unidades» sería inventarse una medida (D-318).
/// </para>
/// </summary>
public sealed record ReportEntry(
    string Slug,
    string AppName,
    string ReportId,
    string Path,
    string Title,
    ReportKind Kind,
    DateTimeOffset When,
    ReportDateSource DateSource,
    string? By,
    string? ModeLabel,
    int? Units,
    int? New,
    int? Resolved,
    decimal? Cost,
    string CostUnit,
    bool HasSession,
    string? FindingId = null,
    string? FindingAlias = null,
    bool Billed = true,
    CostReconciliation? Reconciled = null,
    AuditSession? Session = null)
{
    /// <summary>
    /// <b>El coste de esta sesión se calculó DESPUÉS de escribirse el informe</b> (F29 §1). La
    /// lista lo enseña calculado —es un derivado, y se recalcula en cada lectura—, pero el informe
    /// que se abre es el que se escribió aquel día y no se reescribe (F23). La línea al pie de su
    /// cabecera dice cuándo se cerró el hueco.
    /// </summary>
    public bool CostCalculatedLater => Reconciled is not null;

    /// <summary>«Coste calculado a posteriori el 06/09/2026».</summary>
    public string CalculatedLaterLine => Reconciled is null
        ? string.Empty
        : $"Coste calculado a posteriori el {Reconciled.On.ToString("dd/MM/yyyy", AppCulture.Display)}";

    /// <summary>El coste es una valoración y no una medida: lleva su asterisco (F29 §1).</summary>
    public bool CostIsEstimate => Reconciled is { IsEstimate: true };

    /// <summary>
    /// Este informe es de un arreglo asistido y se sabe de qué hallazgo (H9.1 §1). Es lo que
    /// enciende el enlace de vuelta a la ficha: los informes de arreglo anteriores a H9.1 no lo
    /// traen, y ahí el enlace simplemente no aparece.
    /// </summary>
    public bool HasFinding => !string.IsNullOrWhiteSpace(FindingId);

    /// <summary>
    /// <b>El registro tiene datos que pintar</b> (F36). Es lo que enciende la portada y la fila de
    /// cifras: un informe importado de v4 no tiene sesión detrás y se lee como hasta F36 —cuerpo y
    /// nada más—, porque no hay de dónde sacar una cifra sin inventarla (D-318).
    /// </summary>
    public bool HasRecord => Session is not null;

    /// <summary>El nombre por defecto de la descarga: descriptivo y ordenable (F6.3 §2).</summary>
    public string DownloadName =>
        $"atalaya-{Slug}-{ReportKinds.Display(Kind).ToLowerInvariant()}-{When.ToLocalTime():yyyy-MM-dd}.md";
}

/// <summary>
/// Lo que el usuario ha elegido en la barra de filtros de Informes. Todo neutro = todo visible.
/// </summary>
public sealed record ReportsFilter(
    string? Slug = null,
    string? By = null,
    ReportKind? Kind = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string Search = "")
{
    public static ReportsFilter None { get; } = new();

    /// <summary>Hay algo puesto: es lo que enciende «Limpiar filtros» y cambia el estado vacío.</summary>
    public bool IsActive => Slug is not null
                            || By is not null
                            || Kind is not null
                            || From is not null
                            || To is not null
                            || !string.IsNullOrWhiteSpace(Search);
}

/// <summary>
/// La lista de informes del hub (F6.3 §1).
/// <para>
/// <b>La fuente son los FICHEROS, no las sesiones.</b> Una sesión sin informe no tiene nada que
/// abrir, y un informe sin sesión —los que trae el importador de v4— sí. Enumerar sesiones habría
/// dado una lista con filas que no llevan a ninguna parte y sin las que sí. La sesión se usa para
/// enriquecer lo que se sabe del fichero, nunca para decidir si existe.
/// </para>
/// <para>
/// <b>La caché.</b> Igual que en <see cref="MetricsQuery"/>: se cachea la lectura del hub en
/// memoria —nunca en disco, que solo admite datos primarios— y se tira con el evento de sync, que
/// es el único momento en que esos ficheros cambian por debajo. El CONTENIDO de cada informe se
/// cachea aparte y bajo demanda: la búsqueda de texto lo necesita, y la lista no.
/// </para>
/// </summary>
public sealed class ReportsQuery
{
    private readonly HubContext _hub;

    /// <summary>Las tarifas del hub, para derivar el coste de cada informe listado (F15).</summary>
    private ModelRateTable? Rates()
    {
        try
        {
            return _hub.Store.TryReadModelRates();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Las reconciliaciones de una aplicación (F29 §1). Sin ellas, una sesión ya reconciliada
    /// seguiría saliendo sin coste en la lista — y el informe, sin su línea de «a posteriori».
    /// </summary>
    private Dictionary<Domain.Ids.Ulid, CostReconciliation> Reconciliations(string slug)
    {
        var map = new Dictionary<Domain.Ids.Ulid, CostReconciliation>();
        try
        {
            foreach (CostReconciliation r in _hub.Store.ListCostReconciliations(slug))
            {
                map[r.SessionId] = r;
            }
        }
        catch (Exception)
        {
            // Un fichero ilegible no puede vaciar la lista de informes.
        }

        return map;
    }
    private readonly object _gate = new();
    private IReadOnlyList<ReportEntry>? _cache;
    private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReportFindingIndex> _findings =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, FixRecord>> _fixes =
        new(StringComparer.OrdinalIgnoreCase);

    public ReportsQuery(HubContext hub)
    {
        _hub = hub;
        _hub.SyncStateChanged += Invalidate;
        _hub.Changed += _ => Invalidate();
    }

    /// <summary>Tira la caché. La llama el evento de sync; también sirve a los tests.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
            _contents.Clear();
            _findings.Clear();
            _fixes.Clear();
        }
    }

    /// <summary>Todos los informes del hub, del más reciente al más antiguo.</summary>
    public IReadOnlyList<ReportEntry> All()
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }
        }

        var entries = new List<ReportEntry>();
        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            AppConfig? app = _hub.Store.TryReadApp(slug);
            if (app is null)
            {
                continue;
            }

            string appName = string.IsNullOrWhiteSpace(app.Name) ? slug : app.Name;
            var sessions = _hub.Store.ListSessions(slug)
                .GroupBy(s => s.Id.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var reconciled = Reconciliations(slug);
            foreach (string reportId in _hub.Store.ListReports(slug))
            {
                entries.Add(Describe(slug, appName, reportId, sessions, reconciled));
            }
        }

        var ordered = entries
            .OrderByDescending(e => e.When)
            .ThenBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ReportId, StringComparer.Ordinal)
            .ToList();

        lock (_gate)
        {
            _cache = ordered;
            return _cache;
        }
    }

    /// <summary>
    /// Los autores REALES de lo que hay en la lista, para poblar su combo. No sale de las sesiones
    /// del hub sino de los informes: un combo con nombres que no filtran nada es una promesa falsa.
    /// </summary>
    public IReadOnlyList<string> Authors()
        => All()
            .Select(e => e.By)
            .Where(by => !string.IsNullOrWhiteSpace(by))
            .Select(by => by!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(by => by, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Las aplicaciones que tienen algún informe, para el combo. Vacías no salen.</summary>
    public IReadOnlyList<(string Slug, string Name)> Apps()
        => All()
            .GroupBy(e => e.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Slug: g.Key, Name: g.First().AppName))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// El markdown de un informe, cacheado. Devuelve cadena vacía si el fichero desapareció por
    /// debajo (un pull puede haberlo movido): un informe ilegible no puede tumbar la lista.
    /// </summary>
    public string Read(ReportEntry entry)
    {
        lock (_gate)
        {
            if (_contents.TryGetValue(entry.Path, out string? cached))
            {
                return cached;
            }
        }

        string text;
        try
        {
            text = File.Exists(entry.Path) ? File.ReadAllText(entry.Path) : string.Empty;
        }
        catch (IOException)
        {
            text = string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            text = string.Empty;
        }

        lock (_gate)
        {
            _contents[entry.Path] = text;
            return text;
        }
    }

    /// <summary>
    /// <b>Cómo llegar de un hallazgo de un informe a su ficha</b> (F36 §1.5). Ver
    /// <see cref="ReportFindingIndex"/>: por alias cuando el informe lo escribe, y por unidad y
    /// título —que es lo que sí escribe— cuando la pareja identifica a uno solo.
    /// <para>
    /// Cacheado y tirado con el resto de la lectura del hub: son cientos de ficheros pequeños y
    /// abrir un informe no puede releerlos todos cada vez.
    /// </para>
    /// </summary>
    public ReportFindingIndex FindingIndex(string slug)
    {
        lock (_gate)
        {
            if (_findings.TryGetValue(slug, out ReportFindingIndex? cached))
            {
                return cached;
            }
        }

        var byAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        var verdicts = new Dictionary<string, ReportVerdictEvent>(StringComparer.Ordinal);
        try
        {
            foreach (Finding f in _hub.Store.ListFindings(slug))
            {
                ReadEvents(f, moves, verdicts);
                if (!string.IsNullOrWhiteSpace(f.DisplayId))
                {
                    byAlias[f.DisplayId!] = f.Id.ToString();
                }

                string unit = f.Locations.Count > 0 ? f.Locations[0].Path : string.Empty;
                string key = ReportFindingIndex.Key(unit, f.Title);
                // DOS HALLAZGOS CON EL MISMO TÍTULO EN LA MISMA UNIDAD no se pueden distinguir con
                // lo que el informe escribe, así que ninguno de los dos se enlaza: un «Abrir» que
                // lleva a la ficha equivocada afirma algo falso, y no tenerlo solo calla.
                if (!byTitle.TryAdd(key, f.Id.ToString()))
                {
                    ambiguous.Add(key);
                }
            }
        }
        catch (Exception)
        {
            // Un hub ilegible deja las tarjetas sin «Abrir», no sin tarjetas.
        }

        foreach (string key in ambiguous)
        {
            byTitle.Remove(key);
        }

        var index = new ReportFindingIndex(byAlias, byTitle)
        {
            Reanchors = moves,
            LastVerdicts = verdicts,
        };

        lock (_gate)
        {
            _findings[slug] = index;
            return index;
        }
    }

    /// <summary>«re-anclado 507 → 497», tal y como lo escribe <c>VerifyCoordinator</c> (D-1037).</summary>
    private static readonly System.Text.RegularExpressions.Regex Moved =
        new(@"re-anclado\s+\d+\s*→\s*\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <b>Lo que el historial de un hallazgo sabe y su informe no.</b> Dos cosas, y las dos vienen
    /// del mismo sitio: los eventos que llevan sesión.
    /// <list type="bullet">
    /// <item>El <b>re-anclaje</b> que hizo una verificación (D-1037), que va dentro del evento del
    /// veredicto y no en el informe — el informe se escribió sin él.</item>
    /// <item>El <b>último veredicto</b> del hallazgo, que es lo que permite a un informe de arreglo
    /// decir «Verificado»: ese veredicto es posterior al arreglo y no está en su registro.</item>
    /// </list>
    /// Los eventos de arreglo también llevan sesión y NO son veredictos: se descartan por su tipo.
    /// </summary>
    private static void ReadEvents(
        Finding f,
        Dictionary<string, string> moves,
        Dictionary<string, ReportVerdictEvent> verdicts)
    {
        string id = f.Id.ToString();
        foreach (HistoryEntry h in f.History)
        {
            if (h.SessionId is not { Length: > 0 } session
                || h.Event is FindingEvent.FixProposed or FindingEvent.FixCommitted)
            {
                continue;
            }

            if (h.Detail is { Length: > 0 } detail
                && Moved.Match(detail) is { Success: true } m)
            {
                moves[ReportFindingIndex.MoveKey(session, id)] = m.Value;
            }

            if (!verdicts.TryGetValue(id, out ReportVerdictEvent? last) || h.Utc >= last.Utc)
            {
                verdicts[id] = new ReportVerdictEvent(h.Utc, h.Event);
            }
        }
    }

    /// <summary>
    /// <b>El registro de un arreglo</b> (D-1033): el hecho de si sus cambios están commiteados y
    /// con quién quedaron firmados. Null cuando esa sesión no dejó ninguno — que es lo que pasa
    /// cuando no tocó ningún fichero.
    /// </summary>
    public FixRecord? Fix(string slug, string sessionId)
    {
        lock (_gate)
        {
            if (_fixes.TryGetValue(slug, out Dictionary<string, FixRecord>? cached))
            {
                return cached.TryGetValue(sessionId, out FixRecord? hit) ? hit : null;
            }
        }

        var map = new Dictionary<string, FixRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FixRecord record in _hub.Store.ListFixes(slug))
            {
                map[record.Id.ToString()] = record;
            }
        }
        catch (Exception)
        {
            // Un registro ilegible deja el estado leyéndose del cuerpo, no deja la página sin abrir.
        }

        lock (_gate)
        {
            _fixes[slug] = map;
        }

        return map.TryGetValue(sessionId, out FixRecord? found) ? found : null;
    }

    /// <summary>
    /// Aplica la barra de filtros. La búsqueda de texto mira DENTRO del informe además de en sus
    /// metadatos: es lo que permite «busca ReadCSV» y dar con el informe que la menciona.
    /// </summary>
    public IReadOnlyList<ReportEntry> Filter(ReportsFilter filter)
    {
        string needle = TextSearch.Normalize(filter.Search);
        var result = new List<ReportEntry>();

        foreach (ReportEntry entry in All())
        {
            if (filter.Slug is { Length: > 0 } slug
                && !string.Equals(entry.Slug, slug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter.By is { Length: > 0 } by
                && !string.Equals(entry.By ?? string.Empty, by, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter.Kind is { } kind && entry.Kind != kind)
            {
                continue;
            }

            if (filter.From is { } from && entry.When < from)
            {
                continue;
            }

            if (filter.To is { } to && entry.When >= to)
            {
                continue;
            }

            if (needle.Length > 0 && !Matches(entry, needle))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Un informe casa con la búsqueda por sus metadatos o por su contenido. El contenido se lee
    /// tal cual y se compara normalizado: son ficheros de unas pocas decenas de kB en disco local,
    /// así que no hay nada que indexar (anti-objetivo del encargo).
    /// </summary>
    private bool Matches(ReportEntry entry, string normalizedNeedle)
    {
        if (TextSearch.Contains(entry.Title, normalizedNeedle)
            || TextSearch.Contains(entry.AppName, normalizedNeedle)
            || TextSearch.Contains(entry.By, normalizedNeedle)
            || TextSearch.Contains(entry.ModeLabel, normalizedNeedle))
        {
            return true;
        }

        return TextSearch.Contains(Read(entry), normalizedNeedle);
    }

    /// <summary>Resuelve un informe concreto, para los enlaces que llegan de otras vistas.</summary>
    public ReportEntry? Find(string slug, string reportId)
        => All().FirstOrDefault(e =>
            string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ReportId, reportId, StringComparison.OrdinalIgnoreCase));

    // ---------- De fichero a fila ----------

    private ReportEntry Describe(
        string slug, string appName, string reportId,
        IReadOnlyDictionary<string, AuditSession> sessions,
        IReadOnlyDictionary<Domain.Ids.Ulid, CostReconciliation>? reconciliations = null)
    {
        string path = _hub.HubPaths.ReportFile(slug, reportId);

        if (sessions.TryGetValue(reportId, out AuditSession? session))
        {
            SessionCounters c = session.Counters;
            CostReconciliation? reconciled = null;
            reconciliations?.TryGetValue(session.Id, out reconciled);
            return new ReportEntry(
                slug,
                appName,
                reportId,
                path,
                TitleOf(session, appName),
                KindOf(session.Mode),
                session.StartedUtc,
                ReportDateSource.Session,
                session.By,
                AuditModeLabel(session.Mode),
                session.Units.Count,
                c.New,
                c.Resolved,
                // F15 — derivado de los tokens con la tarifa del modelo de la sesión, igual que en
                // Métricas y en el informe. Una sola aritmética para el mismo número.
                // F29 §1 — y con su reconciliación, si la hubo: en la LISTA el coste sale
                // calculado, aunque el informe que se abre siga siendo el que se escribió aquel día.
                CreditCalculator.Calculate(session, Rates(), reconciled).Credits,
                CostFormat.BillingUnit,
                HasSession: true,
                FindingId: session.FixFindingId,
                FindingAlias: session.FixFindingAlias,
                // F16-RETOQUE §1 — si su casa no factura, la fila no dice «—» (que es «no se
                // sabe»): dice que va contra la suscripción, que sí se sabe.
                Billed: CreditCalculator.IsBilled(session.Provider),
                Reconciled: reconciled,
                // F36 — la página del informe se compone del REGISTRO, así que viaja con la fila:
                // volver a leerlo del disco al abrir sería una segunda lectura que puede diferir
                // de la que llenó la lista.
                Session: session);
        }

        // Sin sesión: lo único que se sabe es lo que el informe declara de sí mismo. Se lee la
        // cabecera —no el fichero entero— y lo que no diga se queda en null, no en cero.
        ReportHeader header = ReportHeader.Parse(Head(path));
        (DateTimeOffset when, ReportDateSource source) = DateOf(header, reportId, path);

        return new ReportEntry(
            slug,
            appName,
            reportId,
            path,
            header.Title ?? reportId,
            header.Kind ?? ReportKind.Operaciones,
            when,
            source,
            header.By,
            header.Mode,
            null,
            null,
            null,
            null,
            CostFormat.Unit,
            HasSession: false);
    }

    /// <summary>
    /// La fecha, y de dónde sale. El ULID del nombre lleva dentro su marca de tiempo, así que un
    /// informe sin sesión ni cabecera fechada sigue pudiendo ordenarse por cuándo se escribió; y
    /// si el nombre tampoco es un ULID —los importados de v4 se llaman <c>HISTORICO</c>— queda la
    /// fecha del fichero, que al menos es una fecha real y no una inventada.
    /// </summary>
    private static (DateTimeOffset When, ReportDateSource Source) DateOf(
        ReportHeader header, string reportId, string path)
    {
        if (header.When is { } declared)
        {
            return (declared, ReportDateSource.Header);
        }

        if (Ulid.TryParse(reportId, out Ulid ulid))
        {
            return (ulid.Timestamp, ReportDateSource.Ulid);
        }

        try
        {
            if (File.Exists(path))
            {
                return (new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), ReportDateSource.File);
            }
        }
        catch (IOException)
        {
            // Cae al mínimo de abajo: sin fecha, al final de la lista.
        }

        return (DateTimeOffset.MinValue, ReportDateSource.File);
    }

    /// <summary>Los primeros kB bastan para la cabecera; no hace falta leer el informe entero.</summary>
    private static string Head(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var reader = new StreamReader(path);
            var sb = new StringBuilder();
            for (int i = 0; i < ReportHeader.MaxLines && reader.ReadLine() is { } line; i++)
            {
                sb.AppendLine(line);
            }

            return sb.ToString();
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// El cierre de ciclo escribe un consolidado; el reset y lo que venga detrás son eventos de
    /// sistema. Todo lo demás es una auditoría, incluidos los modos retirados que siguen vivos en
    /// los informes antiguos del hub.
    /// </summary>
    internal static ReportKind KindOf(AuditMode mode) => mode switch
    {
        AuditMode.Cierre => ReportKind.Consolidado,
        AuditMode.Reset => ReportKind.Operaciones,
        AuditMode.Verify => ReportKind.Verificacion,
        _ => ReportKind.Sesion,
    };

    private static string TitleOf(AuditSession session, string appName) => session.Mode switch
    {
        AuditMode.Cierre => $"Cierre de ciclo {session.CycleN - 1} — {appName}",
        AuditMode.Reset => $"Reset de auditoría — {appName}",
        AuditMode.Fix => $"Arreglo asistido — {appName}",
        AuditMode.Verify => $"Verificación — {appName}",
        _ => $"Informe de sesión — {appName}",
    };

    /// <summary>El modo tal y como se escribe en la aplicación, no el nombre del enum.</summary>
    internal static string AuditModeLabel(AuditMode mode) => mode switch
    {
        AuditMode.Lotes => "Lotes",
        AuditMode.Verify => "Verify",
        AuditMode.Integral => "Integral",
        AuditMode.Superficial => "Superficial",
        AuditMode.Cierre => "Cierre de ciclo",
        AuditMode.Reset => "Reset de auditoría",
        AuditMode.Fix => "Arreglo asistido",
        _ => mode.ToString(),
    };
}

/// <summary>
/// Lo que un informe declara de sí mismo en su cabecera (F6.3 §1). Es la única fuente para los
/// informes que no tienen sesión detrás — los que trae el importador de v4—, y por eso se lee de
/// forma tolerante: lo que no esté se queda a null y la fila lo enseña como «—».
/// </summary>
public sealed record ReportHeader(string? Title, ReportKind? Kind, DateTimeOffset? When, string? By, string? Mode)
{
    /// <summary>Cuántas líneas se miran. La cabecera de los informes cabe de sobra.</summary>
    public const int MaxLines = 30;

    public static ReportHeader Empty { get; } = new(null, null, null, null, null);

    public static ReportHeader Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Empty;
        }

        string? title = null;
        string? by = null;
        string? mode = null;
        DateTimeOffset? when = null;

        foreach (string raw in markdown.Split('\n').Take(MaxLines))
        {
            string line = raw.Trim();
            if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }

            if (Field(line, "Fecha") is { } date && when is null)
            {
                when = ParseDate(date);
            }
            else if (Field(line, "Autor") is { } author && by is null)
            {
                // «alvaro (PC-01)» → «alvaro»: la máquina no es el autor.
                int paren = author.IndexOf('(');
                by = (paren > 0 ? author[..paren] : author).Trim();
            }
            else if (Field(line, "Modo") is { } m && mode is null)
            {
                mode = m;
            }
        }

        return new ReportHeader(title, KindOfTitle(title), when, Blank(by), Blank(mode));
    }

    /// <summary>Un campo <c>- **Nombre**: valor</c> de la cabecera, o null si esta línea no lo es.</summary>
    private static string? Field(string line, string name)
    {
        string prefix = $"- **{name}**:";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].Trim()
            : null;
    }

    /// <summary>
    /// La clase sale del título, que es lo único que un informe sin sesión declara sobre qué es.
    /// Sin título reconocible se queda en <see cref="ReportKind.Operaciones"/>: decir «sesión» de
    /// algo que no se sabe qué es sería afirmar de más.
    /// </summary>
    private static ReportKind? KindOfTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string t = TextSearch.Normalize(title);
        if (t.Contains("cierre de ciclo", StringComparison.Ordinal))
        {
            return ReportKind.Consolidado;
        }

        if (t.Contains("verificacion", StringComparison.Ordinal))
        {
            return ReportKind.Verificacion;
        }

        return t.Contains("informe de sesion", StringComparison.Ordinal) ? ReportKind.Sesion : null;
    }

    /// <summary>Las fechas de los informes se escriben <c>yyyy-MM-dd HH:mm UTC</c>.</summary>
    private static DateTimeOffset? ParseDate(string text)
    {
        string value = text.Replace("UTC", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        string[] formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" };

        return DateTimeOffset.TryParseExact(
            value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// La búsqueda de texto de la aplicación: sin acentos, sin mayúsculas y sin sorpresas.
/// <para>
/// Normalizar importa aquí más que en otros sitios porque lo que se busca lo ESCRIBIÓ un modelo en
/// español: «sesión», «análisis», «duplicación». Un <c>Contains</c> a secas obliga a teclear la
/// tilde exacta para encontrar lo que uno mismo está viendo en pantalla.
/// </para>
/// </summary>
public static class TextSearch
{
    /// <summary>Minúsculas y sin diacríticos. Lo que entra vacío sale vacío.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>¿Contiene <paramref name="normalizedNeedle"/>, ya normalizado por quien llama?</summary>
    public static bool Contains(string? haystack, string normalizedNeedle)
        => normalizedNeedle.Length > 0
           && Normalize(haystack).Contains(normalizedNeedle, StringComparison.Ordinal);
}
