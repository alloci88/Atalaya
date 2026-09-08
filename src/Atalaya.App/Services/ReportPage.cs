using System.Text;
using System.Text.RegularExpressions;
using Atalaya.App;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

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
        ReportFindingIndex? hub = null)
    {
        if (session is null)
        {
            return Plain;
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

        // Las cifras y las gráficas son del informe de SESIÓN (F36 Entrega 1). Los otros dos tipos
        // se quedan con la portada, el carril y las tarjetas de hallazgo hasta la Entrega 2: media
        // fila de tarjetas vacías diría menos que ninguna.
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
            + "tarjetas de abajo, que son los mismos hallazgos."));

        stats.Add(new ReportStat(
            "units",
            "Unidades auditadas",
            session.Units.Count.ToString(AppCulture.Display),
            null,
            UnitsDetail(head),
            "Las unidades que el barrido cubrió en esta sesión, y cómo cerró cada grupo."));

        stats.Add(CostStat(entry, session));

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
    private static ReportStat CostStat(ReportEntry entry, AuditSession session)
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

        int units = session.Units.Count;
        string per = units > 0
            ? string.Create(AppCulture.Display, $"{credits / units:0.#} por unidad")
            : string.Empty;

        return new ReportStat(
            "cost",
            "Coste",
            CostFormat.Marked(CostFormat.Number(credits), entry.CostIsEstimate),
            CostFormat.BillingUnit,
            per,
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

        return string.Join(" · ", parts);
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
}
