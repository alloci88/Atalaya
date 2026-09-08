using System.Text;
using System.Text.RegularExpressions;
using Atalaya.App;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Una pastilla de gravedad dentro del subtítulo de una tarjeta</b> (retoque de F36-1b). El
/// reparto por gravedad se pintaba en texto plano —«1 alta · 5 medias · 4 bajas»— dentro de la
/// misma tarjeta cuyo vecino es el rosco de colores: la gravedad se ve sin leer en el informe
/// (F27), en Portafolio, en Hallazgos y en el rosco de al lado, y ahí no.
/// </summary>
/// <param name="Severity">El nombre bien escrito, que es lo que elige el color (UI-0027).</param>
/// <param name="Text">«1 alta», con su concordancia. Es lo que ya decía el texto plano.</param>
public sealed record ReportChip(string Severity, string Text);

/// <summary>
/// Una de las cifras de cabecera de un informe (F36 §1.2): número grande, subtítulo y «Copiar».
/// <para>
/// Es hermana de <c>StatCard</c> —la del panel— y no la misma: aquélla lleva tendencia contra un
/// periodo anterior, y un informe no tiene periodo anterior con el que compararse. Lo que sí
/// comparten es la forma, para que las dos filas de tarjetas de la aplicación se lean igual.
/// </para>
/// </summary>
public sealed record ReportStat(
    string Key, string Title, string Value, string? Unit, string Subtitle, string ToolTip)
{
    /// <summary>
    /// El subtítulo en pastillas, cuando lo que enumera son gravedades. Vacío en las demás
    /// tarjetas, que llevan su subtítulo en texto — «1 completa · 849 pendientes» no es una escala
    /// de colores y pintarla como si lo fuera sería inventarse un significado.
    /// <para>
    /// <b>Y el texto sigue estando</b>: <see cref="Subtitle"/> no cambia, porque es lo que se
    /// copia. Las pastillas son cómo se dibuja lo mismo.
    /// </para>
    /// </summary>
    public IReadOnlyList<ReportChip> Chips { get; init; } = Array.Empty<ReportChip>();

    public bool HasChips => Chips.Count > 0;

    /// <summary>
    /// Cuando la cifra ES un estado, el tono con el que se pinta. Solo lo lleva «Build» (F36-2 §3):
    /// verde y rojo ahí no son decoración, son el dato — que es donde D-316 permite la paleta
    /// semántica. Las demás cifras van en <see cref="ReportTone.Neutral"/> y con la tinta de
    /// siempre: teñir un recuento diría algo que el recuento no dice.
    /// </summary>
    public ReportTone Tone { get; init; } = ReportTone.Neutral;

    public bool HasTone => Tone != ReportTone.Neutral;

    /// <summary>La cifra con su unidad, para el texto que se copia.</summary>
    public string Amount => string.IsNullOrEmpty(Unit) ? Value : $"{Value} {Unit}";

    /// <summary>
    /// Lo que copia el «Copiar» (F33, D-1038). Lleva el título delante por lo mismo que en el
    /// panel: un «10» pegado en un correo no dice de qué es.
    /// </summary>
    public string CopyText => Subtitle.Length == 0 ? $"{Title}: {Amount}" : $"{Title}: {Amount} · {Subtitle}";
}

/// <summary>
/// Un tramo de una gráfica del informe: un nombre, su recuento y su parte del total. Sin colores.
/// </summary>
/// <param name="Share">
/// «40 %», ya escrito. Los dos tramos de una barra de origen SUMAN 100 exacto: el segundo se
/// calcula restando, no redondeando por su cuenta — dos redondeos independientes dan «17 % · 84 %»
/// y una barra que no suma cien no es una proporción.
/// </param>
public sealed record ReportSlice(string Name, int Count, string Share = "")
{
    /// <summary>«4 · 40 %», que es lo que va al final de la barra.</summary>
    public string Tally => Share.Length == 0 ? Count.ToString(AppCulture.Display) : $"{Count} · {Share}";
}

/// <summary>Una pasada del barrido de una unidad, para la barra de cobertura.</summary>
public sealed record ReportPass(int Index, int New)
{
    /// <summary>La pasada trajo algo. Una seca es un hueco en la barra, no un tramo de otro color.</summary>
    public bool Found => New > 0;
}

/// <summary>
/// La cobertura de UNA unidad (F36 §1, informe de sesión): sus pasadas y por qué dejó de barrerse.
/// </summary>
/// <param name="Reason">
/// El motivo de cierre TAL Y COMO LO ESCRIBIÓ el informe (D-887). No se vuelve a deducir aquí: se
/// lee de la línea de cobertura del cuerpo, que es donde ya está escrito, para que la página y el
/// documento no puedan decir cosas distintas de la misma unidad (D-591).
/// </param>
/// <param name="Reviewed">
/// Lo que el auditor declaró haber revisado en esa unidad, tal cual (D-888). Vacío cuando el
/// informe no lo escribe — que es lo que pasa cuando el resumen del auditor no siguió el contrato.
/// </param>
public sealed record ReportCoverage(
    string Name, string Folder, IReadOnlyList<ReportPass> Passes, string Reason, string Reviewed)
{
    public bool HasReviewed => Reviewed.Length > 0;

    public bool HasPasses => Passes.Count > 0;

    public string Where => Folder.Length == 0 ? string.Empty : $"{Folder}/";
}

/// <summary>
/// Un hallazgo del cuerpo del informe, leído para poder pintarlo como tarjeta (F36 §1.5).
/// </summary>
/// <param name="Severity">Su gravedad, reconocida por texto igual que la pastilla de F27.</param>
/// <param name="Body">
/// Lo que va DENTRO de la tarjeta —descripción, recomendación, la marca de duplicado— tal cual lo
/// escribió el informe. Se pinta con el mismo renderizador que el resto del documento.
/// </param>
/// <param name="FindingId">
/// El ULID de su ficha en el hub, cuando el alias del informe corresponde a un hallazgo que sigue
/// existiendo. Null hace que la tarjeta no ofrezca «Abrir»: un botón que no lleva a ningún sitio es
/// peor que no tenerlo.
/// </param>
public sealed record ReportFinding(
    string Severity,
    string Alias,
    string Title,
    string Unit,
    string Line,
    string RuleId,
    string RuleDetail,
    string Body,
    string? FindingId)
{
    public bool CanOpen => !string.IsNullOrWhiteSpace(FindingId);

    /// <summary>Cómo se nombra en el índice del carril: su alias delante cuando lo tiene.</summary>
    public string Label => Alias.Length == 0 ? Title : $"{Alias} · {Title}";

    public bool HasLine => Line.Length > 0;

    public bool HasRuleDetail => RuleDetail.Length > 0;
}

/// <summary>
/// <b>Cómo se llega de un hallazgo del informe a su ficha</b> (F36 §1.5).
/// <para>
/// <b>Por qué hacen falta dos llaves.</b> La natural sería el alias —«MEJ-0045»—, y la página lo
/// usa cuando está. Pero el informe se escribe al cerrar la sesión y el alias se asigna DESPUÉS, así
/// que <b>ninguno de los informes ya archivados lo lleva en el encabezado de sus hallazgos</b>: se
/// comprobó en el hub, 24 informes con hallazgos y cero con alias. Sin una segunda llave, «Abrir»
/// no aparecería nunca en nada de lo ya escrito.
/// </para>
/// <para>
/// <b>La segunda llave es lo que el informe SÍ escribe</b>: el título del hallazgo y la unidad bajo
/// la que lo agrupa. No es un heurístico con margen — se resuelve solo cuando la pareja identifica
/// a UN hallazgo y a uno solo (418 de 418 en este hub); con dos candidatos no se enlaza nada, que
/// es lo correcto: un «Abrir» que lleva a la ficha equivocada es peor que no tenerlo.
/// </para>
/// </summary>
public sealed record ReportFindingIndex(
    IReadOnlyDictionary<string, string> ByAlias,
    IReadOnlyDictionary<string, string> ByTitle)
{
    public static ReportFindingIndex Empty { get; } =
        new(new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>
    /// <b>Los re-anclajes que escribió cada verificación</b> (BUGFIX-ANCLA, D-1037), por sesión y
    /// hallazgo: <c>«{ulid de la sesión}|{ulid del hallazgo}» → «re-anclado 507 → 497»</c>.
    /// <para>
    /// Se lee del EVENTO de la ficha y no del texto del informe, porque el informe no lo escribe:
    /// verificar re-ancla en disco desde BUGFIX-ANCLA y lo dice en el mismo evento del historial.
    /// Un informe anterior a esa fecha no tiene ninguno, y ahí la tarjeta no lleva pastilla — que
    /// es la verdad, no un hueco.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Reanchors { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <b>Cuándo se verificó por última vez cada hallazgo</b>, con su desenlace. Es lo que permite
    /// que un informe de arreglo diga «Verificado»: ese veredicto es POSTERIOR al arreglo y vive en
    /// la ficha, no en el registro del arreglo ni en su informe (F36-2 §3).
    /// </summary>
    public IReadOnlyDictionary<string, ReportVerdictEvent> LastVerdicts { get; init; } =
        new Dictionary<string, ReportVerdictEvent>(StringComparer.Ordinal);

    /// <summary>La llave de un re-anclaje: la sesión que lo hizo y el hallazgo que lo recibió.</summary>
    public static string MoveKey(string sessionId, string findingId) => $"{sessionId}|{findingId}";

    /// <summary>«re-anclado 507 → 497» de esta sesión sobre este hallazgo, o vacío.</summary>
    public string Reanchor(string sessionId, string findingId)
        => Reanchors.TryGetValue(MoveKey(sessionId, findingId), out string? moved) ? moved : string.Empty;

    /// <summary>El último veredicto de este hallazgo, o null si nunca se verificó.</summary>
    public ReportVerdictEvent? LastVerdict(string findingId)
        => LastVerdicts.TryGetValue(findingId, out ReportVerdictEvent? v) ? v : null;

    /// <summary>La llave por título: la unidad y el título, que es lo que el informe escribe.</summary>
    public static string Key(string unit, string title) => $"{unit.Trim()}|{title.Trim()}";

    public string? Resolve(string alias, string unit, string title)
    {
        if (alias.Length > 0 && ByAlias.TryGetValue(alias, out string? byAlias))
        {
            return byAlias;
        }

        return ByTitle.TryGetValue(Key(unit, title), out string? byTitle) ? byTitle : null;
    }
}

/// <summary>
/// Los hallazgos de UNA unidad. El grupo existe porque el informe ya agrupa por unidad (F23 §5):
/// la ruta iba repetida en los veinticinco hallazgos del caso de referencia y pasó a ser el título
/// del grupo. Repetirla en cada tarjeta la traería de vuelta.
/// </summary>
public sealed record ReportFindingGroup(string Unit, IReadOnlyList<ReportFinding> Findings);

/// <summary>
/// <b>La página de un informe</b> (F36): lo que se enseña alrededor del documento.
/// <para>
/// <b>El <c>.md</c> no se toca y no se reconstruye</b> (D-441, F23). Lo que cambia es la pantalla:
/// deja de ser un markdown pintado y pasa a ser una página compuesta desde el REGISTRO de la sesión
/// —el JSON que ya tiene veredictos, tokens, coste y duración— con el markdown como cuerpo.
/// </para>
/// <para>
/// <b>De dónde sale cada cifra, y por qué son dos sitios y no tres.</b> Lo que el registro tiene
/// —cuántas unidades, cuántos hallazgos nuevos, el coste, la duración— sale del registro, con la
/// MISMA función que usó el informe al escribirlo (<c>CreditCalculator</c>), así que no hay una
/// segunda aritmética que pueda desviarse (D-591). Lo que el registro NO tiene —la gravedad de los
/// hallazgos, el origen catálogo/criterio, las unidades pendientes del inventario y el motivo de
/// cierre de cada unidad— se lee del CUERPO, reconocido por texto igual que la pastilla de gravedad
/// de F27, y no se calcula: se cuenta lo que la propia página va a pintar. Un hallazgo no guarda el
/// ULID de la sesión que lo creó (lo dice el propio <c>OpenSessionStore</c>), así que reconstruir la
/// gravedad desde el hub sería inventarse una tercera fuente que además cambia cuando alguien
/// reclasifica un hallazgo — y el informe es lo que se vio aquel día.
/// </para>
/// <para>
/// <b>Sin registro, no hay portada.</b> Un informe importado de v4 no declara nada de sí mismo más
/// allá de su cabecera: se pinta el cuerpo como hasta hoy, sin portada y sin tarjetas. No se
/// inventa una cifra para llenar el hueco (D-318).
/// </para>
/// </summary>
public sealed record ReportPage
{
    /// <summary>La página de un informe sin registro: solo cuerpo, como hasta F36.</summary>
    public static ReportPage Plain { get; } = new();

    /// <summary>Hay registro con datos detrás: se pinta la portada.</summary>
    public bool HasCover { get; private init; }

    /// <summary>Este tipo de informe tiene fila de cifras. En F36 §1, el de sesión.</summary>
    public bool HasStats => Stats.Count > 0;

    public string Lead { get; private init; } = string.Empty;

    public string Provider { get; private init; } = string.Empty;

    public IReadOnlyList<ReportStat> Stats { get; private init; } = Array.Empty<ReportStat>();

    /// <summary>El rosco de gravedad de los hallazgos nuevos, en orden de gravedad.</summary>
    public IReadOnlyList<ReportSlice> Severities { get; private init; } = Array.Empty<ReportSlice>();

    /// <summary>La barra de origen: del catálogo de reglas o del criterio del auditor (D-887).</summary>
    public IReadOnlyList<ReportSlice> Origins { get; private init; } = Array.Empty<ReportSlice>();

    public IReadOnlyList<ReportCoverage> Coverage { get; private init; } = Array.Empty<ReportCoverage>();

    public IReadOnlyList<ReportFinding> Findings { get; private init; } = Array.Empty<ReportFinding>();

    /// <summary>Los mismos hallazgos, agrupados por unidad y en el orden del informe.</summary>
    public IReadOnlyList<ReportFindingGroup> Groups { get; private init; } = Array.Empty<ReportFindingGroup>();

    public bool HasFindings => Groups.Count > 0;

    /// <summary>
    /// El índice del carril: un hallazgo por entrada, ordenado por gravedad. Es la MISMA lista de
    /// objetos que las tarjetas del cuerpo, para que pulsar una entrada pueda llevar a su tarjeta
    /// sin guardar una correspondencia aparte que se pueda desincronizar.
    /// </summary>
    public IReadOnlyList<ReportFinding> Index { get; private init; } = Array.Empty<ReportFinding>();

    /// <summary>
    /// <b>La ficha del documento</b> (F36-1b): la cabecera y el resumen del informe, que son
    /// exactamente lo que la portada ya dice con otras palabras. Va PLEGADA encima del cuerpo — no
    /// se borra ni se reescribe (D-441): quien quiera el documento tal cual lo despliega y ahí
    /// está, entero. Lo que se gana es que el cuerpo empiece por lo que la portada no dice.
    /// </summary>
    public string Sheet { get; private init; } = string.Empty;

    public bool HasSheet => Sheet.Trim().Length > 0;

    /// <summary>Lo que el informe escribe entre la cobertura y los hallazgos: incidencias, patrones,
    /// directivas, veredictos degradados. Se pinta tal cual, como hasta F36.</summary>
    public string Middle { get; private init; } = string.Empty;

    public bool HasMiddle => Middle.Trim().Length > 0;

    public bool HasCoverage => Coverage.Count > 0;

    /// <summary>La firma del pie, que va detrás de las tarjetas de hallazgo.</summary>
    public string Foot { get; private init; } = string.Empty;

    public bool HasFoot => Foot.Length > 0;

    /// <summary>
    /// Lo que se lleva «Copiar resumen»: la frase ejecutiva y las tarjetas, en ese orden. Es el
    /// informe en cinco líneas — lo que se pega en un correo o en un chat sin adjuntar el <c>.md</c>.
    /// </summary>
    public string Summary { get; private init; } = string.Empty;

    public bool HasSummary => Summary.Length > 0;

    // ------------------------------------------------------- F36-2 · verificación y arreglo

    /// <summary>
    /// <b>Los veredictos de una verificación, uno por tarjeta</b> y en el orden del informe
    /// (F36-2 §2). Vacío en los otros dos tipos.
    /// </summary>
    public IReadOnlyList<ReportVerdict> Verdicts { get; private init; } = Array.Empty<ReportVerdict>();

    public bool HasVerdicts => Verdicts.Count > 0;

    /// <summary>El rosco de veredictos, en el orden en que se decide y sin los que no hay.</summary>
    public IReadOnlyList<ReportSlice> VerdictSlices { get; private init; } = Array.Empty<ReportSlice>();

    /// <summary>
    /// El índice del carril de una verificación: <b>por veredicto y no por gravedad</b>, con los
    /// resueltos al final. Lo que hay que decidir es lo que sigue abierto.
    /// </summary>
    public IReadOnlyList<ReportVerdict> VerdictIndex { get; private init; } = Array.Empty<ReportVerdict>();

    /// <summary>
    /// <b>El estado del arreglo</b>, leído de <c>fixes/{ulid}.json</c> (D-1033) y, cuando no hay
    /// registro, del cuerpo. Null en los otros dos tipos y en un arreglo que no declare ninguno.
    /// </summary>
    public ReportFixState? FixState { get; private init; }

    public bool HasFixState => FixState is not null;

    /// <summary>Los ficheros que tocó el arreglo, para la barra de +/− (F36-2 §3).</summary>
    public IReadOnlyList<ReportFileChange> Files { get; private init; } = Array.Empty<ReportFileChange>();

    /// <summary>La compilación del arreglo, leída del cuerpo. Null cuando el informe no la trae.</summary>
    public ReportBuild? Build { get; private init; }

    public bool HasBuild => Build is not null;

    /// <summary>«Qué cambió y por qué», tal cual. Es prosa y va en su medida de lectura (F27).</summary>
    public string Story { get; private init; } = string.Empty;

    public bool HasStory => Story.Trim().Length > 0;

    // ------------------------------------------------------------------ composición

    /// <summary>
    /// Compone la página de un informe. <paramref name="session"/> null —un informe sin registro—
    /// devuelve <see cref="Plain"/>: cuerpo y nada más.
    /// </summary>
    /// <param name="body">El cuerpo, ya separado de su anexo técnico (F27).</param>
    /// <param name="hub">
    /// Cómo llegar de un hallazgo del informe a su ficha. Lo que no resuelva sale sin «Abrir».
    /// </param>
    public static ReportPage Compose(
        ReportEntry entry,
        AuditSession? session,
        string body,
        ReportFindingIndex? hub = null,
        FixRecord? fix = null)
    {
        if (session is null)
        {
            return Plain;
        }

        // Los otros dos tipos se componen aparte: sus cuerpos tienen otras secciones y cada una se
        // pinta de una forma. El de sesión no se toca (F36-1b).
        if (entry.Kind == ReportKind.Verificacion)
        {
            return ComposeVerify(entry, session, body ?? string.Empty, hub);
        }

        if (session.Mode == AuditMode.Fix)
        {
            return ComposeFix(entry, session, body ?? string.Empty, hub, fix);
        }

        (string head, string cover, string middle, string? section, string foot) =
            SplitBody(body ?? string.Empty);
        var findings = ReadFindings(section, hub);

        var page = new ReportPage
        {
            HasCover = true,
            Provider = $"{ProviderNames.Display(session.Provider)} · {session.Model ?? "n/d"}",
            Sheet = head,
            Middle = middle,
            Foot = foot,
            Findings = findings,
            Groups = findings
                .GroupBy(f => f.Unit, StringComparer.Ordinal)
                .Select(g => new ReportFindingGroup(g.Key, g.ToList()))
                .ToList(),
            Index = findings
                .OrderBy(f => Rank(f.Severity))
                .ThenBy(f => f.Unit, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        // Consolidado y operaciones se quedan con la portada, el carril y las tarjetas de hallazgo:
        // no tienen cifras propias, y media fila de tarjetas vacías diría menos que ninguna.
        if (entry.Kind != ReportKind.Sesion)
        {
            return page;
        }

        var severities = CountSeverities(findings);
        var stats = SessionStats(entry, session, head, severities);
        string lead = LeadText(entry, session, head);

        return page with
        {
            Lead = lead,
            Stats = stats,
            Severities = severities,
            Origins = ReadOrigins(head),
            Coverage = Coverages(session, cover),
            Summary = SummaryText(lead, stats),
        };
    }

    // ------------------------------------------------------------------ el cuerpo, partido

    private const char Lf = '\n';

    private const char Cr = '\r';

    private const string NL = "\n";

    /// <summary>El encabezado con el que <c>ReportBuilder</c> abre los hallazgos de una sesión.</summary>
    internal const string FindingsHeading = "## Hallazgos nuevos";

    /// <summary>Y el de la cobertura (F23 §4), que se pinta como barras en vez de como viñetas.</summary>
    internal const string CoverageHeading = "## Cobertura";

    /// <summary>
    /// Parte el cuerpo en los cinco trozos que la página necesita: lo de antes de la cobertura, la
    /// cobertura, lo que va entre ésta y los hallazgos, los hallazgos y la firma.
    /// <para>
    /// <b>Ni una palabra se pierde y ni una se reescribe</b> (D-441). Los dos trozos que se
    /// «pintan» —cobertura y hallazgos— llevan dentro exactamente el texto que el informe escribió;
    /// lo que cambia es que se leen como barras y tarjetas en vez de como dos listas de viñetas de
    /// treinta líneas. Los otros tres se pintan como hasta hoy.
    /// </para>
    /// </summary>
    internal static (string Head, string Coverage, string Middle, string? Findings, string Foot) SplitBody(
        string body)
    {
        (string upto, string? findings, string foot) = SplitFindings(body);

        int at = IndexOfHeading(upto, CoverageHeading);
        if (at < 0)
        {
            return (upto, string.Empty, string.Empty, findings, foot);
        }

        string head = upto[..at].TrimEnd();
        string rest = upto[at..];
        int next = NextHeading(rest, CoverageHeading.Length);
        string cover = next < 0 ? rest : rest[..next].TrimEnd();
        string middle = next < 0 ? string.Empty : rest[next..].Trim(Lf, Cr);
        return (head, cover, middle, findings, foot);
    }

    /// <summary>Dónde empieza un encabezado de segundo nivel, y solo al principio de una línea.</summary>
    private static int IndexOfHeading(string text, string heading)
    {
        if (text.StartsWith(heading, StringComparison.Ordinal))
        {
            return 0;
        }

        int at = text.IndexOf(NL + heading, StringComparison.Ordinal);
        return at < 0 ? -1 : at + 1;
    }

    /// <summary>El siguiente encabezado de segundo nivel a partir de <paramref name="from"/>.</summary>
    private static int NextHeading(string text, int from)
    {
        int at = text.IndexOf(NL + "## ", from, StringComparison.Ordinal);
        return at < 0 ? -1 : at + 1;
    }

    /// <summary>
    /// Parte el cuerpo en lo que va antes de los hallazgos, la sección de hallazgos y la firma.
    /// <para>
    /// La firma —la raya y «Atalaya»— viaja aparte porque cierra el DOCUMENTO, no la lista de
    /// hallazgos: dejarla dentro del último hallazgo la metería dentro de su tarjeta.
    /// </para>
    /// </summary>
    internal static (string Head, string? Findings, string Foot) SplitFindings(string body)
    {
        int at = body.IndexOf(FindingsHeading, StringComparison.Ordinal);
        if (at < 0)
        {
            return (body, null, string.Empty);
        }

        string head = body[..at].TrimEnd();
        string section = body[at..];

        // La firma del pie: la ÚLTIMA raya horizontal seguida de la línea de «Atalaya».
        int sign = section.LastIndexOf("\n---", StringComparison.Ordinal);
        string foot = string.Empty;
        if (sign >= 0 && section[(sign + 4)..].TrimStart('\r', '\n').StartsWith("Atalaya", StringComparison.Ordinal))
        {
            foot = section[(sign + 1)..].TrimEnd();
            section = section[..sign].TrimEnd();
        }

        return (head, section, foot);
    }

    /// <summary>
    /// El encabezado de un hallazgo, tal y como lo escribe el informe:
    /// <c>#### [Alta] MEJ-0045 · Título — línea 42</c>. El alias es opcional y la línea también.
    /// </summary>
    private static readonly Regex AliasHead = new(@"^(?<a>[A-Z]{2,6}-\d+)\s*·\s*", RegexOptions.Compiled);

    private static readonly Regex LineTail = new(@"\s+—\s+línea\s+(?<n>\d+)\s*$", RegexOptions.Compiled);

    /// <summary>La primera línea del cuerpo de un hallazgo: <c>- `REGLA` · Pilar</c>.</summary>
    private static readonly Regex RuleLine = new(@"^-\s+`(?<r>[^`]+)`\s*(?<d>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Lee los hallazgos de la sección. <b>No reconstruye el markdown</b> (D-441): parte por sus
    /// encabezados y le devuelve a cada tarjeta su propio trozo, tal cual, para que lo pinte el
    /// mismo renderizador que el resto del documento.
    /// </summary>
    internal static IReadOnlyList<ReportFinding> ReadFindings(
        string? section, ReportFindingIndex? hub = null)
    {
        var found = new List<ReportFinding>();
        if (string.IsNullOrWhiteSpace(section))
        {
            return found;
        }

        string unit = string.Empty;
        string? head = null;
        var chunk = new StringBuilder();

        void Flush()
        {
            if (head is null)
            {
                return;
            }

            if (Read(head, unit, chunk.ToString(), hub) is { } finding)
            {
                found.Add(finding);
            }

            head = null;
            chunk.Clear();
        }

        foreach (string raw in section.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                Flush();
                head = line[5..].Trim();
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                unit = line[4..].Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (head is not null)
            {
                chunk.Append(line).Append('\n');
            }
        }

        Flush();
        return found;
    }

    private static ReportFinding? Read(
        string head, string unit, string chunk, ReportFindingIndex? hub)
    {
        // LA GRAVEDAD SE RECONOCE COMO EN F27 y con el mismo resolutor: un encabezado que no abra
        // con una gravedad no es un hallazgo y no se pinta como tarjeta.
        if (MarkdownFlowDocument.SeverityOf(head) is not { } mark)
        {
            return null;
        }

        (string severity, string rest) = mark;

        string alias = string.Empty;
        Match m = AliasHead.Match(rest);
        if (m.Success)
        {
            alias = m.Groups["a"].Value;
            rest = rest[m.Length..];
        }

        string line = string.Empty;
        Match l = LineTail.Match(rest);
        if (l.Success)
        {
            line = $"línea {l.Groups["n"].Value}";
            rest = rest[..l.Index];
        }

        string rule = string.Empty;
        string detail = string.Empty;
        var body = new StringBuilder();
        bool ruleTaken = false;
        foreach (string raw in chunk.Split('\n'))
        {
            if (!ruleTaken && RuleLine.Match(raw.Trim()) is { Success: true } r)
            {
                rule = r.Groups["r"].Value;
                detail = r.Groups["d"].Value.TrimStart(' ', '·').Trim();
                ruleTaken = true;
                continue;
            }

            body.Append(raw).Append('\n');
        }

        string title = rest.Trim();
        string? id = hub?.Resolve(alias, unit, title);

        return new ReportFinding(
            severity, alias, title, unit, line, rule, detail, body.ToString().Trim('\n'), id);
    }

    // ------------------------------------------------------------------ lo que dice el cuerpo

    /// <summary>«- Origen: 17 del catálogo de reglas · 8 del criterio del auditor (32 %)» (D-887).</summary>
    private static readonly Regex OriginLine = new(
        @"^-\s+Origen:\s+(?<c>\d+)\s+del catálogo de reglas\s*·\s*(?<j>\d+)\s+del criterio del auditor",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>«- Unidades: 1 auditada: 1 completa · 849 pendientes en el inventario» (D-887).</summary>
    private static readonly Regex UnitsLine = new(
        @"^-\s+Unidades:\s+\d+\s+auditadas?(?<d>[^\n]*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PendingTail = new(
        @"·\s*(?<n>\d+)\s+pendientes?\s+en el inventario", RegexOptions.Compiled);

    /// <summary>«- **Foo.cs** (src/) — auditada · 6 pasadas, cerrada por tope: … · 3 nuevos».</summary>
    private static readonly Regex CoverageEntry = new(
        @"^-\s+\*\*(?<f>[^*]+)\*\*(?:\s+\((?<d>[^)]*)\))?\s+—\s+(?<t>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>De dónde salieron los hallazgos. Vacío cuando el informe no lo escribe.</summary>
    internal static IReadOnlyList<ReportSlice> ReadOrigins(string head)
    {
        Match m = OriginLine.Match(head);
        if (!m.Success)
        {
            return Array.Empty<ReportSlice>();
        }

        int catalogo = int.Parse(m.Groups["c"].Value);
        int criterio = int.Parse(m.Groups["j"].Value);

        // EL PORCENTAJE DEL CRITERIO ES EL QUE EL INFORME ESCRIBE —`PercentText.Of(criterio, total)`,
        // la misma función—, y el del catálogo se obtiene RESTANDO. Redondear los dos por separado
        // da barras que suman 99 o 101, y una proporción que no suma cien no es una proporción.
        int total = catalogo + criterio;
        string suyo = PercentText.Of(criterio, total);
        return new[]
        {
            new ReportSlice("Catálogo de reglas", catalogo, Complement(suyo)),
            new ReportSlice("Criterio del auditor", criterio, suyo),
        };
    }

    /// <summary>Lo que le falta a un porcentaje para cien, escrito igual que él.</summary>
    private static string Complement(string percent)
    {
        string number = percent.Replace("%", string.Empty).Trim();
        return double.TryParse(number, System.Globalization.NumberStyles.Any, AppCulture.Display, out double value)
            ? PercentText.Of((100 - value) / 100.0)
            : percent;
    }

    /// <summary>Lo que la línea de unidades dice además del recuento: «1 completa · 849 pendientes».</summary>
    internal static string UnitsDetail(string head)
    {
        Match m = UnitsLine.Match(head);
        if (!m.Success)
        {
            return string.Empty;
        }

        string tail = m.Groups["d"].Value.Trim();
        tail = tail.StartsWith(":", StringComparison.Ordinal) ? tail[1..].Trim() : tail;
        return tail.Replace(" en el inventario", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>Las unidades que el inventario tenía pendientes aquel día. Null si no lo dice.</summary>
    internal static int? Pending(string head)
    {
        Match u = UnitsLine.Match(head);
        if (!u.Success)
        {
            return null;
        }

        Match p = PendingTail.Match(u.Value);
        return p.Success ? int.Parse(p.Groups["n"].Value) : null;
    }

    /// <summary>
    /// La cobertura por unidad: las pasadas salen del REGISTRO y el motivo de cierre del CUERPO.
    /// Se emparejan por posición —<c>ReportBuilder</c> recorre <c>session.Units</c> en orden— y se
    /// comprueba el nombre: si no casa, la unidad se queda sin motivo en vez de llevar el de otra.
    /// </summary>
    internal static IReadOnlyList<ReportCoverage> Coverages(AuditSession session, string section)
    {
        var lines = CoverageEntry.Matches(section);
        var reviewed = ReviewedLines(section);
        var result = new List<ReportCoverage>();
        for (int i = 0; i < session.Units.Count; i++)
        {
            UnitVerdictRecord u = session.Units[i];
            string name = Path.GetFileName(u.Unit);
            string folder = (Path.GetDirectoryName(u.Unit) ?? string.Empty).Replace('\\', '/');

            string reason = string.Empty;
            string members = string.Empty;
            if (i < lines.Count && lines[i].Groups["f"].Value.Trim() == name)
            {
                reason = lines[i].Groups["t"].Value.Trim();
                members = i < reviewed.Count ? reviewed[i] : string.Empty;
            }

            var passes = (u.Passes ?? new List<UnitPassRecord>())
                .Select(p => new ReportPass(p.Index, p.New))
                .ToList();

            result.Add(new ReportCoverage(name, folder, passes, reason, members));
        }

        return result;
    }

    /// <summary>
    /// La línea «Revisados: A, B, C» de cada unidad, en el orden en que el informe las escribe
    /// (D-888). Una unidad sin ella deja una cadena vacía en su sitio para que la correspondencia
    /// por posición no se descoloque — es la misma regla que el emparejamiento de arriba.
    /// </summary>
    private static IReadOnlyList<string> ReviewedLines(string section)
    {
        const string mark = "- Revisados:";
        var result = new List<string>();
        bool open = false;
        foreach (string raw in section.Split(Lf))
        {
            string line = raw.TrimEnd(Cr);
            if (line.StartsWith("- **", StringComparison.Ordinal))
            {
                if (open)
                {
                    result.Add(string.Empty);
                }

                open = true;
                continue;
            }

            if (open && line.TrimStart().StartsWith(mark, StringComparison.Ordinal))
            {
                result.Add(line.TrimStart()[mark.Length..].Trim());
                open = false;
            }
        }

        if (open)
        {
            result.Add(string.Empty);
        }

        return result;
    }

    // ------------------------------------------------------------------ las cifras

    private static int Rank(string severity) => severity switch
    {
        "Crítica" => 0,
        "Alta" => 1,
        "Media" => 2,
        _ => 3,
    };

    /// <summary>
    /// El reparto por gravedad de los hallazgos del cuerpo, en orden y <b>sin las que no hay</b>:
    /// «0 Críticas» ocupa sitio para no decir nada, y un tramo de rosco a cero no es un tramo.
    /// </summary>
    internal static IReadOnlyList<ReportSlice> CountSeverities(IReadOnlyList<ReportFinding> findings)
        => new[] { Severity.Critica, Severity.Alta, Severity.Media, Severity.Baja }
            .Select(s => new ReportSlice(
                SeverityNames.Display(s),
                findings.Count(f => f.Severity == SeverityNames.Display(s))))
            .Where(s => s.Count > 0)
            .ToList();

    /// <summary>Las cuatro cifras del informe de sesión (F36 §1).</summary>
    private static IReadOnlyList<ReportStat> SessionStats(
        ReportEntry entry, AuditSession session, string head, IReadOnlyList<ReportSlice> severities)
    {
        var stats = new List<ReportStat>();

        int nuevos = session.Counters.New;
        stats.Add(new ReportStat(
            "findings",
            "Hallazgos nuevos",
            nuevos.ToString(AppCulture.Display),
            null,
            string.Join(" · ", severities.Select(s => $"{s.Count} {Lower(s.Name, s.Count)}")),
            "Los hallazgos que esta sesión dio de alta. El reparto por gravedad es el de las "
            + "tarjetas de abajo, que son los mismos hallazgos.")
        {
            // UNA PASTILLA POR GRAVEDAD PRESENTE, y ninguna por las que no hay: el color dice «hay
            // algo de esta gravedad», y una pastilla a cero diría lo contrario de su número
            // (UI-0051, D-318).
            Chips = severities.Select(s => new ReportChip(s.Name, $"{s.Count} {Lower(s.Name, s.Count)}")).ToList(),
        });

        stats.Add(new ReportStat(
            "units",
            "Unidades auditadas",
            session.Units.Count.ToString(AppCulture.Display),
            null,
            UnitsDetail(head),
            "Las unidades que el barrido cubrió en esta sesión, y cómo cerró cada grupo."));

        stats.Add(CostStat(entry, session.Units.Count, "por unidad"));

        string elapsed = Elapsed(session);
        stats.Add(new ReportStat(
            "duration",
            "Duración",
            elapsed.Length > 0 ? elapsed : ReportsUnknown,
            null,
            elapsed.Length > 0 ? string.Empty : "la sesión no registró cuándo terminó",
            "Reloj de pared entre el arranque de la sesión y su cierre."));

        return stats;
    }

    /// <summary>Lo que se escribe donde no hay dato. Nunca un cero con formato (D-318).</summary>
    internal const string ReportsUnknown = "—";

    /// <summary>
    /// El coste. Sale del mismo <c>CreditCalculator</c> que escribió el informe y con la
    /// reconciliación que la lista ya aplica (F29 §1): cuando las dos no coinciden, es porque el
    /// coste se cerró después, y eso lo dice la línea de «calculado a posteriori» que ya está.
    /// </summary>
    private static ReportStat CostStat(ReportEntry entry, int divisor, string per)
    {
        if (entry.Cost is not { } credits)
        {
            return new ReportStat(
                "cost",
                "Coste",
                entry.Billed ? ReportsUnknown : CostFormat.SubscriptionCostShort,
                null,
                entry.Billed
                    ? "no hay tarifa para el modelo de esta sesión"
                    : CostFormat.SubscriptionCost,
                CostFormat.Caveat);
        }

        string each = divisor > 0
            ? string.Create(AppCulture.Display, $"{credits / divisor:0.#} {per}")
            : string.Empty;

        return new ReportStat(
            "cost",
            "Coste",
            CostFormat.Marked(CostFormat.Number(credits), entry.CostIsEstimate),
            CostFormat.BillingUnit,
            each,
            CostFormat.Both(credits));
    }

    /// <summary>Lo que duró la sesión, con la misma forma que la escribe el informe.</summary>
    internal static string Elapsed(AuditSession session)
    {
        if (session.EndedUtc is not { } ended || ended <= session.StartedUtc)
        {
            return string.Empty;
        }

        TimeSpan span = ended - session.StartedUtc;
        return span.TotalMinutes >= 1
            ? string.Create(AppCulture.Display, $"{(int)span.TotalMinutes} min {span.Seconds} s")
            : string.Create(AppCulture.Display, $"{span.Seconds} s");
    }

    /// <summary>«Crítica» → «crítica»/«críticas», que es como se leen dentro de una frase.</summary>
    private static string Lower(string severity, int count)
    {
        string word = count == 1 ? severity : severity + "s";
        return word.ToLower(AppCulture.Display);
    }

    /// <summary>
    /// <b>La frase ejecutiva</b> (F36 §1.1): qué pasó en esta sesión, en un renglón, por plantilla
    /// determinista y sin modelo. Concordada, y lo que no hay no se nombra — una frase que dice
    /// «0 hallazgos, 0 críticas» gasta una línea en no decir nada.
    /// <para>
    /// <b>Sin desglose por gravedad</b>: cuántos hay de cada una lo dicen la tarjeta de hallazgos y
    /// el rosco, que están dos centímetros más abajo. Nombrar solo la más alta obligaba además a
    /// elegir cuál, y con dos altas la frase decía «2 altas» y se callaba las tres medias.
    /// </para>
    /// </summary>
    internal static string LeadText(ReportEntry entry, AuditSession session, string head)
    {
        var parts = new List<string>();

        int units = session.Units.Count;
        if (units > 0)
        {
            string verb = units == 1 ? "Se auditó" : "Se auditaron";
            string noun = units == 1 ? "unidad" : "unidades";
            // «de 850» es la suma de lo auditado y lo pendiente, los dos escritos en la misma línea
            // del informe. Sin la cifra de pendientes no se escribe el total: media división no es
            // una cobertura (D-318).
            string total = Pending(head) is { } pending
                ? string.Create(AppCulture.Display, $" de {units + pending}")
                : string.Empty;
            parts.Add(string.Create(AppCulture.Display, $"{verb} {units} {noun}{total}"));
        }

        int nuevos = session.Counters.New;
        if (nuevos == 0)
        {
            parts.Add("sin hallazgos nuevos");
        }
        else
        {
            string noun = nuevos == 1 ? "hallazgo nuevo" : "hallazgos nuevos";
            parts.Add(string.Create(AppCulture.Display, $"{nuevos} {noun}"));
        }

        AppendCost(parts, entry, session);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// «58,0 AI credits en 2 min 33 s», que es como acaban las tres frases ejecutivas. Lo que no
    /// se sabe no se nombra: sin coste queda «en 2 min 33 s» y sin reloj, el coste a secas.
    /// </summary>
    internal static void AppendCost(List<string> parts, ReportEntry entry, AuditSession session)
    {
        string cost = entry.Cost is { } credits
            ? $"{CostFormat.Number(credits)} {CostFormat.BillingUnit}"
            : entry.Billed ? string.Empty : CostFormat.SubscriptionCostShort;
        string elapsed = Elapsed(session);
        if (cost.Length > 0 && elapsed.Length > 0)
        {
            parts.Add($"{cost} en {elapsed}");
        }
        else if (cost.Length > 0)
        {
            parts.Add(cost);
        }
        else if (elapsed.Length > 0)
        {
            parts.Add($"en {elapsed}");
        }
    }

    /// <summary>La frase y las tarjetas, en ese orden. Es lo que se lleva «Copiar resumen».</summary>
    internal static string SummaryText(string lead, IReadOnlyList<ReportStat> stats)
    {
        var sb = new StringBuilder();
        if (lead.Length > 0)
        {
            sb.AppendLine(lead);
        }

        foreach (ReportStat stat in stats)
        {
            sb.AppendLine(stat.CopyText);
        }

        return sb.ToString().TrimEnd();
    }

    // ================================================================ F36-2 · la verificación

    /// <summary>
    /// <b>La página de una verificación</b> (F36-2 §2).
    /// <para>
    /// <b>Todo lo que se pinta sale del CUERPO, y el §0 dice por qué</b>: el registro de una sesión
    /// <c>verify</c> se escribe con <c>counters</c> a cero y <c>units</c> vacía —los trece de este
    /// hub—, así que no tiene ni un veredicto. Lo que sí tiene, y de ahí sale, es el coste, la
    /// duración, el proveedor y el modelo. Es la regla de D-1046 aplicada tal cual: dos fuentes y
    /// solo dos.
    /// </para>
    /// <para>
    /// La excepción es el <b>re-anclaje</b>, que no está en ninguna de las dos: lo escribió el
    /// EVENTO de la verificación en la ficha del hallazgo (D-1037). Se lee de ahí, y un informe
    /// anterior a BUGFIX-ANCLA no lo lleva porque entonces no se escribía.
    /// </para>
    /// </summary>
    private static ReportPage ComposeVerify(
        ReportEntry entry, AuditSession session, string body, ReportFindingIndex? hub)
    {
        ReportSections cut = ReportSections.Split(body);
        var verdicts = ReportReader.ReadVerdicts(
            cut.Section(ReportReader.VerdictsHeading), hub, session.Id.ToString());

        // LA FICHA DEL DOCUMENTO se lleva la cabecera, la cita de qué es verificar y las notas de
        // la sesión, que repiten los veredictos con otras palabras. No se borra ni se reescribe
        // nada (D-441): se pliega, y el texto sigue entero a un clic.
        string sheet = Join(
            cut.Head,
            Join(cut.Rest(ReportReader.VerdictsHeading, ReportReader.NotesHeading).ToArray()),
            cut.Section(ReportReader.NotesHeading));

        var slices = CountVerdicts(verdicts);
        var stats = VerifyStats(entry, verdicts);
        string lead = VerifyLead(entry, session, verdicts);

        return new ReportPage
        {
            HasCover = true,
            Provider = $"{ProviderNames.Display(session.Provider)} · {session.Model ?? "n/d"}",
            Sheet = sheet,
            Foot = cut.Foot,
            Verdicts = verdicts,
            VerdictSlices = slices,
            VerdictIndex = verdicts
                .OrderBy(v => ReportVerdicts.Order.ToList().IndexOf(v.Kind))
                .ThenBy(v => v.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Lead = lead,
            Stats = stats,
            Summary = SummaryText(lead, stats),
        };
    }

    /// <summary>Dónde cae un desenlace en el orden en que se decide.</summary>
    private static int Rank(ReportVerdictKind kind) => ReportVerdicts.Order.ToList().IndexOf(kind);

    /// <summary>
    /// El reparto por veredicto, en el orden en que se decide y <b>sin los que no hay</b>: un tramo
    /// de rosco a cero no es un tramo (D-318). «Otro» agrupa por la palabra literal del informe, así
    /// que dos veredictos que no reconocemos no se mezclan en un mismo tramo.
    /// </summary>
    internal static IReadOnlyList<ReportSlice> CountVerdicts(IReadOnlyList<ReportVerdict> verdicts)
        => verdicts
            .GroupBy(v => (v.Kind, Name: ReportVerdicts.Group(v.Kind, v.Verdict)))
            .OrderBy(g => Rank(g.Key.Kind))
            .ThenBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReportSlice(g.Key.Name, g.Count()))
            .ToList();

    /// <summary>
    /// Las cuatro cifras de una verificación. <b>No localizado y no concluyente van de subtítulo de
    /// «Verificados»</b> y no de tarjeta propia: son los desenlaces raros —cero de trece en este
    /// hub— y una fila con dos tarjetas a cero se lee peor que cuatro llenas (D-318).
    /// </summary>
    private static IReadOnlyList<ReportStat> VerifyStats(
        ReportEntry entry, IReadOnlyList<ReportVerdict> verdicts)
    {
        int resueltos = verdicts.Count(v => v.Kind == ReportVerdictKind.Resuelto);
        int activos = verdicts.Count(v => v.Kind == ReportVerdictKind.Activo);
        int moved = Reanchored(verdicts);

        string otros = string.Join(" · ", verdicts
            .Where(v => v.Kind is not (ReportVerdictKind.Resuelto or ReportVerdictKind.Activo))
            .GroupBy(v => (v.Kind, v.Verdict))
            .OrderBy(g => Rank(g.Key.Kind))
            .Select(g => ReportVerdicts.Tally(g.Key.Kind, g.Count(), g.Key.Verdict)));

        return new List<ReportStat>
        {
            new(
                "verified",
                "Verificados",
                verdicts.Count.ToString(AppCulture.Display),
                null,
                otros,
                "Los hallazgos que esta sesión le puso delante al instrumento. Cada uno tiene su "
                + "tarjeta abajo, con lo que se le enseñó y lo que contestó."),
            new(
                "resolved",
                "Resueltos",
                resueltos.ToString(AppCulture.Display),
                null,
                string.Empty,
                "El defecto ya no está, y con evidencia: verificar es la única forma de resolver "
                + "un hallazgo (D-557)."),
            new(
                "active",
                "Siguen activos",
                activos.ToString(AppCulture.Display),
                null,
                moved == 0 ? string.Empty : moved == 1 ? "1 re-anclado" : $"{moved} re-anclados",
                "El instrumento miró el código de hoy y el defecto sigue ahí."),
            CostStat(entry, verdicts.Count, "por hallazgo"),
        };
    }

    /// <summary>Cuántos veredictos movieron el ancla en esta sesión (D-1037).</summary>
    internal static int Reanchored(IReadOnlyList<ReportVerdict> verdicts)
        => verdicts.Count(v => v.HasReanchor);

    /// <summary>
    /// <b>La frase de una verificación</b>: «Se verificaron 3 hallazgos · 2 resueltos · 1 sigue
    /// activo · 1 re-anclado · 0,15 $ en 48 s». Concordada, y <b>solo los veredictos con datos</b>
    /// — enumerar «0 no localizados» gasta media línea en no decir nada (D-318).
    /// </summary>
    internal static string VerifyLead(
        ReportEntry entry, AuditSession session, IReadOnlyList<ReportVerdict> verdicts)
    {
        var parts = new List<string>();
        if (verdicts.Count > 0)
        {
            string verb = verdicts.Count == 1 ? "Se verificó" : "Se verificaron";
            string noun = verdicts.Count == 1 ? "hallazgo" : "hallazgos";
            parts.Add(string.Create(AppCulture.Display, $"{verb} {verdicts.Count} {noun}"));
        }
        else
        {
            parts.Add("Ningún hallazgo llegó al instrumento");
        }

        foreach (IGrouping<(ReportVerdictKind Kind, string Verdict), ReportVerdict> group in verdicts
            .GroupBy(v => (v.Kind, v.Verdict))
            .OrderBy(g => Rank(g.Key.Kind)))
        {
            parts.Add(ReportVerdicts.Tally(group.Key.Kind, group.Count(), group.Key.Verdict));
        }

        int moved = Reanchored(verdicts);
        if (moved > 0)
        {
            parts.Add(moved == 1 ? "1 re-anclado" : $"{moved} re-anclados");
        }

        AppendCost(parts, entry, session);
        return string.Join(" · ", parts);
    }

    // ================================================================ F36-2 · el arreglo asistido

    internal const string RisksHeading = "## Riesgos declarados";

    internal const string CommitHeading = "## Sugerencia de commit";

    /// <summary>
    /// <b>La página de un arreglo asistido</b> (F36-2 §3).
    /// <para>
    /// <b>El estado sale del REGISTRO</b> —<c>fixes/{ulid}.json</c>, D-1033— porque ahí es donde
    /// está el hecho: el párrafo del informe es una frase que se sustituye encima cuando el commit
    /// llega (D-1034), y un informe que nadie reescribiera seguiría diciendo «NO están commiteados»
    /// de un arreglo que sí lo está. Los ficheros, sus recuentos y la compilación no están en
    /// ningún registro, así que se leen del cuerpo — la regla de D-1046, otra vez.
    /// </para>
    /// </summary>
    private static ReportPage ComposeFix(
        ReportEntry entry,
        AuditSession session,
        string body,
        ReportFindingIndex? hub,
        FixRecord? fix)
    {
        ReportSections cut = ReportSections.Split(body);
        var files = ReportReader.ReadFiles(cut.Section(ReportReader.FilesHeading));
        ReportBuild? build = ReportReader.ReadBuild(cut.Section(ReportReader.BuildHeading));
        ReportFixState? state = ReadFixState(entry, session, cut.Head, files.Count, hub, fix);

        // La cabecera y la cita de «no están commiteados» se pliegan: el estado ya está en la
        // portada, y en grande. Con las directivas, que son de la misma clase de dato.
        string sheet = Join(
            cut.Head,
            Join(cut.Rest(
                ReportReader.SummaryHeading,
                ReportReader.FilesHeading,
                ReportReader.BuildHeading,
                RisksHeading,
                CommitHeading).ToArray()));

        // Lo que esta página no lee se pinta tal cual y junto, debajo de la compilación: los
        // riesgos declarados y la sugerencia de commit siguen siendo el mismo texto.
        string rest = Join(cut.Section(RisksHeading), cut.Section(CommitHeading));

        var stats = FixStats(entry, files, build);
        string lead = FixLead(entry, session, files, build);

        return new ReportPage
        {
            HasCover = true,
            Provider = $"{ProviderNames.Display(session.Provider)} · {session.Model ?? "n/d"}",
            Sheet = sheet,
            Middle = rest,
            Foot = cut.Foot,
            Story = ReportReader.StripHeading(cut.Section(ReportReader.SummaryHeading)),
            Files = files,
            Build = build,
            FixState = state,
            Lead = lead,
            Stats = stats,
            Summary = SummaryText(lead, stats),
        };
    }

    /// <summary>
    /// El estado que se pinta. <b>Manda el registro</b>; sin registro se lee del cuerpo, que es lo
    /// único que queda. Y encima de los dos, <b>«Verificado»</b>: un veredicto posterior sobre el
    /// hallazgo que se arregló es lo que dice que este arreglo llegó a comprobarse (D-504) — y no
    /// se aplica sobre una sesión que no tocó nada, porque ahí no había nada que verificar.
    /// </summary>
    internal static ReportFixState? ReadFixState(
        ReportEntry entry,
        AuditSession session,
        string head,
        int touched,
        ReportFindingIndex? hub,
        FixRecord? fix)
    {
        ReportFixState? state = ReportReader.FixStateFromRecord(fix)
                                ?? ReportReader.FixStateFromBody(head, touched);
        if (state is null || state.Kind == ReportFixStateKind.SinCambios)
        {
            return state;
        }

        string? findingId = entry.FindingId ?? session.FixFindingId;
        if (findingId is null || hub?.LastVerdict(findingId) is not { } verdict
            || verdict.Utc <= session.StartedUtc)
        {
            return state;
        }

        // El commit no se pierde: pasa al detalle, que es donde cabe entero con su autor.
        string detail = state.Kind == ReportFixStateKind.Commiteado
            ? JoinLine(" · ", $"commiteado en {state.Sha}{(state.HasDetail ? " " + state.Detail : string.Empty)}", verdict.Label)
            : JoinLine(" · ", "sin commitear", verdict.Label);

        return state with { Kind = ReportFixStateKind.Verificado, Detail = detail };
    }

    /// <summary>Las cuatro cifras de un arreglo: ficheros, cambios, compilación y coste.</summary>
    private static IReadOnlyList<ReportStat> FixStats(
        ReportEntry entry, IReadOnlyList<ReportFileChange> files, ReportBuild? build)
    {
        int added = files.Sum(f => f.Added);
        int removed = files.Sum(f => f.Removed);
        int outside = files.Count(f => f.OutOfScope);

        return new List<ReportStat>
        {
            new(
                "files",
                "Ficheros",
                files.Count.ToString(AppCulture.Display),
                null,
                outside == 0
                    ? string.Empty
                    : outside == 1 ? "1 fuera del hallazgo" : $"{outside} fuera del hallazgo",
                "Los ficheros que el arreglo dejó escritos en el clon."),
            new(
                "lines",
                "Cambios",
                $"+{added} −{removed}",
                null,
                string.Empty,
                "Líneas añadidas y quitadas, sumando todos los ficheros tocados."),
            new(
                "build",
                "Build",
                build?.Text ?? ReportsUnknown,
                null,
                JoinLine(" · ", build?.Reason ?? string.Empty, build?.Tests ?? string.Empty),
                "El resultado de compilar y pasar los tests al cerrar el arreglo.")
            {
                Tone = build?.Tone ?? ReportTone.Neutral,
            },
            CostStat(entry, 0, string.Empty),
        };
    }

    /// <summary>
    /// <b>La frase de un arreglo</b>: «MEJ-0046 · 1 fichero · +0 −38 · build verde · 0,27 $ en
    /// 54 s». El alias sale del registro (H9.1), lo demás del cuerpo, y lo que no hay no se nombra.
    /// </summary>
    internal static string FixLead(
        ReportEntry entry,
        AuditSession session,
        IReadOnlyList<ReportFileChange> files,
        ReportBuild? build)
    {
        var parts = new List<string>();
        string alias = entry.FindingAlias ?? session.FixFindingAlias ?? string.Empty;
        if (alias.Length > 0)
        {
            parts.Add(alias);
        }

        if (files.Count == 0)
        {
            parts.Add("sin cambios en el clon");
        }
        else
        {
            parts.Add(files.Count == 1 ? "1 fichero" : $"{files.Count} ficheros");
            parts.Add($"+{files.Sum(f => f.Added)} −{files.Sum(f => f.Removed)}");
        }

        if (build is { Tone: ReportTone.Success or ReportTone.Danger })
        {
            parts.Add(build.Tone == ReportTone.Success ? "build verde" : "build rojo");
        }

        AppendCost(parts, entry, session);
        return string.Join(" · ", parts);
    }

    /// <summary>Junta trozos de texto dejando una línea en blanco, y sin los vacíos.</summary>
    private static string Join(params string[] parts)
        => string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim('\n', '\r')));

    /// <summary>
    /// Junta trozos cortos en una línea, y sin los vacíos. <b>Nombre propio y no una sobrecarga de
    /// <see cref="Join(string[])"/></b>: con tres cadenas, C# elegía la del separador y el primer
    /// trozo se convertía en el pegamento — la cabecera desaparecía de la ficha del documento.
    /// </summary>
    private static string JoinLine(string separator, params string[] parts)
        => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
