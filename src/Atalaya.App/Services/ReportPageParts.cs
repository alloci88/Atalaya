using System.Text;
using System.Text.RegularExpressions;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// El tono con el que se pinta un dato de la página (F36-2). No es un color: es lo que el dato
/// SIGNIFICA, y el color lo elige el tema. Se usa donde el estado ES el dato — el veredicto de una
/// verificación, el estado de un arreglo, el resultado de una compilación—, que es exactamente
/// donde D-316 reserva la paleta semántica.
/// </summary>
public enum ReportTone
{
    /// <summary>Ni bien ni mal: no se sabe, o no aplica.</summary>
    Neutral,

    /// <summary>Algo se cerró: resuelto, commiteado, build verde.</summary>
    Success,

    /// <summary>Algo queda abierto y hay que hacer algo: sin commitear, no localizado.</summary>
    Warning,

    /// <summary>Algo salió mal: build rojo.</summary>
    Danger,

    /// <summary>El estado «activo» de un hallazgo, con el color que ya tiene en la aplicación.</summary>
    Active,
}

/// <summary>
/// <b>El desenlace de una verificación</b>, con el vocabulario de D-504/D-505: resuelto, activo,
/// no localizado y no concluyente. <see cref="Otro"/> es la salida para cualquier otra palabra que
/// el informe escriba —«no-es-defecto», «sin veredicto»—: se enseña tal cual y no se traduce, que
/// es lo contrario de meterla a la fuerza en una de las cuatro.
/// </summary>
public enum ReportVerdictKind
{
    Resuelto,
    Activo,
    NoLocalizado,
    NoConcluyente,
    Otro,
}

/// <summary>
/// Cómo se lee un veredicto: de la palabra que el informe escribió a la que usa la aplicación.
/// <para>
/// <b>Solo una palabra cambia.</b> «resuelto», «no localizado» y «no concluyente» se llaman igual
/// en el documento y en la pantalla. La que cambia es «confirmado», que en el vocabulario de estado
/// de la aplicación —el de la ficha, el de Hallazgos y el de D-504/D-505— es <b>sigue activo</b>:
/// es el mismo hecho dicho con la palabra con la que se decide. El texto literal del informe está
/// a un clic, dentro de «Ficha del documento».
/// </para>
/// </summary>
public static class ReportVerdicts
{
    /// <summary>El orden en que se decide: primero lo que sigue abierto, los resueltos al final.</summary>
    public static IReadOnlyList<ReportVerdictKind> Order { get; } = new[]
    {
        ReportVerdictKind.Activo,
        ReportVerdictKind.NoLocalizado,
        ReportVerdictKind.NoConcluyente,
        ReportVerdictKind.Otro,
        ReportVerdictKind.Resuelto,
    };

    /// <summary>De la palabra del informe a su desenlace. Lo que no se reconoce es <see cref="ReportVerdictKind.Otro"/>.</summary>
    public static ReportVerdictKind Of(string verdict) => verdict.Trim().ToLowerInvariant() switch
    {
        "confirmado" or "presente" => ReportVerdictKind.Activo,
        "resuelto" or "arreglado" => ReportVerdictKind.Resuelto,
        "no localizado" => ReportVerdictKind.NoLocalizado,
        "no concluyente" => ReportVerdictKind.NoConcluyente,
        _ => ReportVerdictKind.Otro,
    };

    /// <summary>La pastilla de la tarjeta, en singular. «Otro» se enseña con la palabra del informe.</summary>
    public static string Label(ReportVerdictKind kind, string verdict) => kind switch
    {
        ReportVerdictKind.Resuelto => "resuelto",
        ReportVerdictKind.Activo => "sigue activo",
        ReportVerdictKind.NoLocalizado => "no localizado",
        ReportVerdictKind.NoConcluyente => "no concluyente",
        _ => verdict.Trim(),
    };

    /// <summary>«2 resueltos», «1 sigue activo»: concordado de verdad, como el resto de la página.</summary>
    public static string Tally(ReportVerdictKind kind, int count, string verdict)
    {
        string word = kind switch
        {
            ReportVerdictKind.Resuelto => count == 1 ? "resuelto" : "resueltos",
            ReportVerdictKind.Activo => count == 1 ? "sigue activo" : "siguen activos",
            ReportVerdictKind.NoLocalizado => count == 1 ? "no localizado" : "no localizados",
            ReportVerdictKind.NoConcluyente => count == 1 ? "no concluyente" : "no concluyentes",
            _ => verdict.Trim(),
        };

        return $"{count} {word}";
    }

    /// <summary>El rótulo de la leyenda del rosco y del índice: en plural, que es lo que agrupa.</summary>
    public static string Group(ReportVerdictKind kind, string verdict) => kind switch
    {
        ReportVerdictKind.Resuelto => "Resueltos",
        ReportVerdictKind.Activo => "Siguen activos",
        ReportVerdictKind.NoLocalizado => "No localizados",
        ReportVerdictKind.NoConcluyente => "No concluyentes",
        _ => verdict.Trim(),
    };

    public static ReportTone Tone(ReportVerdictKind kind) => kind switch
    {
        ReportVerdictKind.Resuelto => ReportTone.Success,
        ReportVerdictKind.Activo => ReportTone.Active,
        ReportVerdictKind.NoLocalizado => ReportTone.Warning,
        _ => ReportTone.Neutral,
    };

    /// <summary>
    /// La clave del pincel del tema con la que se pinta el tramo del rosco. <b>Ninguna es nueva</b>:
    /// son las que la aplicación ya usa para «resuelto», «activo» y un aviso (D-316, D-317). Va como
    /// clave y no como hexadecimal para que el color siga siendo el del tema y no una copia suya.
    /// </summary>
    public static string BrushKey(ReportTone tone) => tone switch
    {
        ReportTone.Success => "Brush.Success.Ink",
        ReportTone.Active => "Brush.Primary.Ink",
        ReportTone.Warning => "Brush.Warning.Ink",
        ReportTone.Danger => "Brush.Danger.Ink",
        _ => "Brush.TextMuted",
    };
}

/// <summary>
/// <b>Un veredicto del informe de verificación</b>, leído para pintarlo como tarjeta (F36-2 §2).
/// <para>
/// El borde de la tarjeta va en el color del VEREDICTO y no en el de la gravedad: en una
/// verificación lo que se decide es si el hallazgo sigue vivo, y la gravedad —que también se
/// enseña, en pastilla pequeña— es el contexto, no la conclusión.
/// </para>
/// </summary>
/// <param name="Verdict">La palabra que el informe escribió, sin tocar.</param>
/// <param name="Basis">
/// «Código que se le enseñó», la línea que D-813 exige: un «no concluyente» con el método delante
/// y otro con la unidad entera delante no significan lo mismo.
/// </param>
/// <param name="Reanchor">
/// «re-anclado 507 → 497», leído del EVENTO de la verificación en la ficha del hallazgo (D-1037),
/// no del texto del informe. Vacío cuando no se movió nada o cuando el informe es anterior a
/// BUGFIX-ANCLA, que es cuando ese evento todavía no se escribía.
/// </param>
public sealed record ReportVerdict(
    ReportVerdictKind Kind,
    string Verdict,
    string Severity,
    string Alias,
    string Title,
    string Where,
    string Basis,
    string Body,
    string? FindingId,
    string Reanchor)
{
    public string Label => ReportVerdicts.Label(Kind, Verdict);

    public ReportTone Tone => ReportVerdicts.Tone(Kind);

    public bool CanOpen => !string.IsNullOrWhiteSpace(FindingId);

    public bool HasReanchor => Reanchor.Length > 0;

    public bool HasBasis => Basis.Length > 0;

    public bool HasWhere => Where.Length > 0;

    /// <summary>Cómo se nombra en el índice del carril: el alias delante cuando lo tiene.</summary>
    public string IndexLabel => Alias.Length == 0 ? Title : $"{Alias} · {Title}";

    /// <summary>
    /// Qué hacer con él, cuando hay algo que hacer. Solo un «no localizado» lo lleva, y con las
    /// MISMAS palabras que la ficha: dos frases distintas para el mismo callejón se leen como dos
    /// situaciones distintas.
    /// </summary>
    public string NextStep => Kind == ReportVerdictKind.NoLocalizado
        ? SnippetReader.NotLocatedNextStep
        : string.Empty;

    public bool HasNextStep => NextStep.Length > 0;
}

/// <summary>
/// <b>Un veredicto que una verificación dejó escrito en la ficha de un hallazgo</b> (F16 §F): el
/// evento del historial que apunta a su sesión. Es lo que permite saber, desde el informe de un
/// arreglo, si el hallazgo se verificó DESPUÉS — un hecho que no está ni en el registro del
/// arreglo ni en su texto.
/// </summary>
public sealed record ReportVerdictEvent(DateTimeOffset Utc, FindingEvent Event)
{
    /// <summary>Cómo acabó, con las palabras del estado de la aplicación (D-504).</summary>
    public string Label => Event switch
    {
        FindingEvent.Resolved => "el hallazgo quedó resuelto",
        FindingEvent.Confirmed => "el hallazgo sigue activo",
        FindingEvent.Disputed => "el hallazgo quedó disputado",
        FindingEvent.NotLocated => "el hallazgo no se localizó",
        _ => "el veredicto no fue concluyente",
    };
}

/// <summary>
/// <b>De qué ámbito es un fichero que el arreglo tocó</b> (D-546). El ámbito lo decide el hallazgo:
/// sus ficheros se editan directamente, y cualquier otro pasa por una autorización del usuario que
/// <b>la sesión registra</b>. <see cref="Desconocido"/> es lo que se pinta cuando esa sesión no
/// dejó las notas —los informes anteriores a que se escribieran—: sin registro, sin marca.
/// </summary>
public enum ReportFileScope
{
    Desconocido,

    /// <summary>Un fichero del hallazgo. Entra sin permiso porque el ámbito es suyo.</summary>
    Hallazgo,

    /// <summary>Fuera del hallazgo, y el usuario lo autorizó fichero a fichero.</summary>
    Autorizado,
}

/// <summary>Un fichero que el arreglo tocó, con su recuento (F36-2 §3).</summary>
/// <param name="Scope">
/// De dónde salió el permiso para tocarlo, leído de las notas de la SESIÓN (D-546) y no del cuerpo:
/// el registro es donde la autorización queda anotada.
/// </param>
public sealed record ReportFileChange(string Path, int Added, int Removed, ReportFileScope Scope)
{
    /// <summary>«+4 −0», con el signo menos de <c>FixFile.Tally</c> — es el mismo texto.</summary>
    public string Tally => $"+{Added} −{Removed}";

    public int Total => Added + Removed;

    public bool OutOfScope => Scope == ReportFileScope.Autorizado;

    /// <summary>La marca de la barra. Vacía cuando la sesión no registró el ámbito.</summary>
    public string Mark => Scope switch
    {
        ReportFileScope.Hallazgo => "hallazgo",
        ReportFileScope.Autorizado => "fuera del hallazgo",
        _ => string.Empty,
    };

    public bool HasMark => Mark.Length > 0;

    /// <summary>Lo que dice el tooltip de la ruta: la ruta entera y de dónde vino el permiso.</summary>
    public string ToolTip => Scope switch
    {
        ReportFileScope.Hallazgo => $"{Path} — es un fichero del hallazgo: entra en el ámbito sin permiso.",
        ReportFileScope.Autorizado => $"{Path} — fuera del hallazgo; lo autorizaste durante la sesión.",
        _ => Path,
    };
}

/// <summary>El estado de un arreglo, que es la única pregunta que un director hace sobre uno.</summary>
public enum ReportFixStateKind
{
    /// <summary>La sesión no llegó a tocar ningún fichero: no hay nada que commitear.</summary>
    SinCambios,

    /// <summary>Hay cambios en el clon y siguen ahí, sin commit.</summary>
    SinCommitear,

    /// <summary>Atalaya los commiteó con «Me quedo los cambios» (F32, D-1033).</summary>
    Commiteado,

    /// <summary>Y además el hallazgo tiene un veredicto POSTERIOR: se verificó (D-504).</summary>
    Verificado,
}

/// <summary>
/// <b>El estado de un arreglo, en la portada y en grande</b> (F36-2 §3).
/// <para>
/// <b>Sale del registro</b> —<c>fixes/{ulid}.json</c>, D-1033— y no del texto: el registro es el
/// hecho, y el párrafo del informe es una frase que se reescribe encima cuando el commit llega
/// (D-1034). Cuando NO hay registro —una sesión que no tocó nada, o un informe anterior a que se
/// escribiera— se lee del cuerpo, que es la regla de D-1046 para todo lo que el registro no tiene.
/// </para>
/// </summary>
/// <param name="Sha">El commit corto, cuando lo hubo.</param>
/// <param name="Detail">«como Nombre &lt;correo&gt;» (BUGFIX-F32-2), o lo que explique el estado.</param>
/// <param name="FromRecord">De dónde salió, que es un dato como cualquier otro (N-2).</param>
public sealed record ReportFixState(
    ReportFixStateKind Kind, string Sha, string Detail, bool FromRecord)
{
    public string Text => Kind switch
    {
        ReportFixStateKind.SinCambios => "Sin cambios",
        ReportFixStateKind.SinCommitear => "Sin commitear",
        ReportFixStateKind.Commiteado => $"Commiteado {Sha}",
        _ => "Verificado",
    };

    public ReportTone Tone => Kind switch
    {
        ReportFixStateKind.SinCommitear => ReportTone.Warning,
        ReportFixStateKind.SinCambios => ReportTone.Neutral,
        _ => ReportTone.Success,
    };

    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// Lo que se ENSEÑA del detalle. Un «como Nombre &lt;correo.muy.largo@empresa.com&gt;» empuja la
    /// pastilla del estado hasta el otro lado de la portada, así que a partir de
    /// <see cref="Fit"/> caracteres se recorta con puntos suspensivos — y el entero queda en el
    /// tooltip, que es la única forma de recortar sin mentir (P-01).
    /// </summary>
    public string DetailShort => Detail.Length <= Fit ? Detail : Detail[..Fit].TrimEnd() + "…";

    public bool DetailElided => Detail.Length > Fit;

    /// <summary>Lo que cabe al lado del estado sin empujarlo. Medido sobre la portada real.</summary>
    internal const int Fit = 24;

    /// <summary>De dónde sale el estado. Se dice, como toda procedencia (N-2).</summary>
    public string Source => FromRecord
        ? "Lo dice el registro del arreglo (fixes/), que es donde queda el hecho."
        : "Este arreglo no dejó registro: el estado se lee del propio informe.";
}

/// <summary>
/// <b>La compilación de un arreglo</b>, leída del cuerpo (F36-2 §3). El registro de la sesión no
/// guarda nada de esto: el veredicto del build, los tests y la salida viven solo en el informe.
/// </summary>
/// <param name="Reason">La razón corta cuando está en rojo: la línea de «Veredicto» del informe.</param>
/// <param name="Tests">
/// Lo que el informe dice de los tests, tal cual: «sin tests» cuando el proyecto no tiene ninguno
/// —un hecho del repositorio (H9.1 §3)— o lo que escribió la línea de «Tests». <b>No se cuenta
/// cuántos son</b>: ni el registro ni el informe lo escriben, y sacarlo de la salida del compilador
/// sería inventarse una medida (D-318).
/// </param>
/// <param name="Lead">Las líneas del veredicto, tal cual, para la tarjeta.</param>
/// <param name="Detail">Los errores y la salida completa, que van plegados.</param>
public sealed record ReportBuild(
    ReportTone Tone, string Verdict, string Reason, string Tests, string Lead, string Detail)
{
    /// <summary>«Verde» / «Rojo» / «—»: lo que va en grande en la tarjeta.</summary>
    public string Text => Tone switch
    {
        ReportTone.Success => "Verde",
        ReportTone.Danger => "Rojo",
        _ => ReportPage.ReportsUnknown,
    };

    public bool HasDetail => Detail.Trim().Length > 0;

    public bool HasLead => Lead.Trim().Length > 0;
}

/// <summary>
/// <b>El cuerpo de un informe, partido por sus secciones</b> (F36-2).
/// <para>
/// El informe de sesión se parte por dos encabezados concretos (F36 §1). Los de verificación y
/// arreglo tienen más secciones y cada una se pinta de una forma, así que se parten por TODOS sus
/// encabezados de segundo nivel y luego se reconocen por nombre. <b>Ni una palabra se pierde y ni
/// una se reescribe</b> (D-441): cada trozo lleva dentro exactamente el texto que el informe
/// escribió, y <see cref="Parts"/> concatenado con la firma vuelve a ser el cuerpo entero.
/// </para>
/// </summary>
/// <param name="Head">Lo de antes del primer encabezado: la cabecera y su cita.</param>
/// <param name="Parts">Cada sección con su encabezado, en el orden del documento.</param>
/// <param name="Foot">La firma, que cierra el documento y no la última sección.</param>
public sealed record ReportSections(
    string Head, IReadOnlyList<string> Parts, string Foot)
{
    private const string NL = "\n";

    /// <summary>La sección cuyo encabezado empieza por <paramref name="heading"/>, o vacía.</summary>
    public string Section(string heading)
        => Parts.FirstOrDefault(p => p.StartsWith(heading, StringComparison.Ordinal)) ?? string.Empty;

    /// <summary>
    /// Las secciones que no reconoce nadie, en el orden del documento. Se pintan tal cual: lo que
    /// esta página no sabe leer no se esconde, se enseña como hasta hoy.
    /// </summary>
    public IReadOnlyList<string> Rest(params string[] known)
        => Parts.Where(p => !known.Any(k => p.StartsWith(k, StringComparison.Ordinal))).ToList();

    /// <summary>
    /// Parte el cuerpo. La firma sale primero —cierra el documento, no la última sección— y lo
    /// demás se corta por cada <c>## </c> al principio de línea.
    /// </summary>
    public static ReportSections Split(string body)
    {
        string text = body ?? string.Empty;
        string foot = string.Empty;

        // La firma: la ÚLTIMA raya horizontal seguida de la línea de «Atalaya», igual que en el
        // informe de sesión (F36 §1).
        int sign = text.LastIndexOf("\n---", StringComparison.Ordinal);
        if (sign >= 0 && text[(sign + 4)..].TrimStart('\r', '\n').StartsWith("Atalaya", StringComparison.Ordinal))
        {
            foot = text[(sign + 1)..].TrimEnd();
            text = text[..sign].TrimEnd();
        }

        var parts = new List<string>();
        int at = text.StartsWith("## ", StringComparison.Ordinal) ? 0 : Next(text, 0);
        string head = at < 0 ? text : text[..at].TrimEnd();
        while (at >= 0)
        {
            int next = Next(text, at + 3);
            parts.Add((next < 0 ? text[at..] : text[at..next]).TrimEnd());
            at = next;
        }

        return new ReportSections(head, parts, foot);
    }

    private static int Next(string text, int from)
    {
        if (from >= text.Length)
        {
            return -1;
        }

        int at = text.IndexOf(NL + "## ", from, StringComparison.Ordinal);
        return at < 0 ? -1 : at + 1;
    }
}

/// <summary>
/// <b>El resolutor por texto devuelve TEXTO, no markdown</b> (F36-2b §1.4).
/// <para>
/// Una línea del informe que se pinta como un dato suelto —«**la unidad entera**, porque…»— llega
/// con sus asteriscos, y fuera del renderizador de markdown un asterisco es un asterisco. Se
/// quitan las marcas de énfasis, de código y de enlace, y <b>no se toca nada más</b>: el texto es
/// el mismo, sin la notación que servía para dibujarlo.
/// </para>
/// </summary>
internal static class PlainText
{
    /// <summary>La negrita: <c>**x**</c> y <c>__x__</c>. Va primero, o la cursiva se la come.</summary>
    private static readonly Regex Strong = new(
        @"\*\*(?<t>.+?)\*\*|__(?<t>.+?)__",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>La cursiva: <c>*x*</c> y <c>_x_</c>, sin tocar el guion bajo de un identificador.</summary>
    private static readonly Regex Emphasis = new(
        @"(?<![\w*])\*(?<t>[^*]+?)\*(?![\w*])|(?<![\w_])_(?<t>[^_]+?)_(?![\w_])",
        RegexOptions.Compiled);

    /// <summary>Un enlace: <c>[texto](destino)</c>. Se queda el texto, que es lo que se lee.</summary>
    private static readonly Regex Link = new(@"\[(?<t>[^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string plain = Link.Replace(text, Inner);
        plain = Strong.Replace(plain, Inner);
        plain = Emphasis.Replace(plain, Inner);
        return plain.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static string Inner(Match m) => m.Groups["t"].Value;
}

/// <summary>
/// <b>Lo que el cuerpo de una verificación y el de un arreglo dicen</b>, reconocido por texto
/// (F36-2 §0). Es la misma regla de D-1046: lo que el registro tiene sale del registro; lo que no,
/// se lee del documento en el mismo sitio en el que está escrito, y lo que no se reconoce no se
/// pinta.
/// </summary>
internal static class ReportReader
{
    internal const string VerdictsHeading = "## Veredictos";

    internal const string NotesHeading = "## Notas de la sesión";

    internal const string SummaryHeading = "## Qué cambió y por qué";

    internal const string FilesHeading = "## Ficheros tocados";

    internal const string BuildHeading = "## Compilación y tests";

    /// <summary>«### BUG-0016 — email.Split(AT)[0] sin validar…»: el alias y el título.</summary>
    private static readonly Regex VerdictHead = new(
        @"^###\s+(?<a>\S+)\s+—\s+(?<t>.+)$", RegexOptions.Compiled);

    private static readonly Regex Field = new(
        @"^-\s+\*\*(?<k>[^*]+)\*\*:\s*(?<v>.*)$", RegexOptions.Compiled);

    /// <summary>«- `ruta` (+4 −0)» y, si lo lleva, su marca de fuera del hallazgo.</summary>
    private static readonly Regex FileLine = new(
        @"^-\s+`(?<p>[^`]+)`\s*\(\+(?<a>\d+)\s*−(?<r>\d+)\)(?<x>.*)$", RegexOptions.Compiled);

    /// <summary>«16 error(es) nuevo(s)», que es como <c>BuildVerdict.Headline</c> siempre abre.</summary>
    private static readonly Regex NewErrors = new(
        @"^(?<n>\d+)\s+error\(es\)\s+nuevo", RegexOptions.Compiled);

    /// <summary>
    /// Los veredictos de la sección, en el orden del informe. Un bloque cuyo encabezado no tenga la
    /// forma de un veredicto no se pinta como tarjeta: lo que no se reconoce, no se inventa.
    /// </summary>
    internal static IReadOnlyList<ReportVerdict> ReadVerdicts(
        string section, ReportFindingIndex? hub, string sessionId)
    {
        var result = new List<ReportVerdict>();
        if (string.IsNullOrWhiteSpace(section))
        {
            return result;
        }

        string? head = null;
        var chunk = new StringBuilder();

        void Flush()
        {
            if (head is not null && Read(head, chunk.ToString(), hub, sessionId) is { } verdict)
            {
                result.Add(verdict);
            }

            head = null;
            chunk.Clear();
        }

        foreach (string raw in section.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                head = line;
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
        return result;
    }

    private static ReportVerdict? Read(
        string head, string chunk, ReportFindingIndex? hub, string sessionId)
    {
        Match m = VerdictHead.Match(head);
        if (!m.Success)
        {
            return null;
        }

        string alias = m.Groups["a"].Value.Trim();
        string title = m.Groups["t"].Value.Trim();

        string severity = string.Empty;
        string where = string.Empty;
        string path = string.Empty;
        string basis = string.Empty;
        string verdict = string.Empty;
        var body = new StringBuilder();

        foreach (string raw in chunk.Split('\n'))
        {
            Match f = Field.Match(raw.Trim());
            if (f.Success)
            {
                string value = f.Groups["v"].Value.Trim();
                switch (f.Groups["k"].Value.Trim())
                {
                    case "Severidad":
                        // El informe escribe el NOMBRE DEL ENUM —«Critica», sin tilde—; la pantalla
                        // usa el rotulado único de la aplicación (UI-0027). No se traduce nada: es
                        // el mismo valor escrito como se escribe en todas partes.
                        severity = Enum.TryParse(PlainText.Of(value), out Severity s)
                            ? SeverityNames.Display(s)
                            : PlainText.Of(value);
                        continue;
                    case "Ubicación":
                        where = PlainText.Of(value);
                        path = where.Contains(':') ? where[..where.LastIndexOf(':')] : where;
                        continue;
                    case "Código que se le enseñó":
                        // TEXTO, NO MARKDOWN: «**la unidad entera**» se pinta en una línea suelta,
                        // fuera del renderizador, y ahí un asterisco es un asterisco (§1.4).
                        basis = PlainText.Of(value);
                        continue;
                    case "Veredicto":
                        verdict = PlainText.Of(value);
                        continue;
                }
            }

            body.Append(raw).Append('\n');
        }

        if (verdict.Length == 0)
        {
            return null;   // Sin veredicto escrito no hay tarjeta de veredicto que pintar.
        }

        string? id = hub?.Resolve(alias, path, title);
        return new ReportVerdict(
            ReportVerdicts.Of(verdict),
            verdict,
            severity,
            alias,
            title,
            where,
            basis,
            body.ToString().Trim('\n'),
            id,
            id is null ? string.Empty : hub?.Reanchor(sessionId, id) ?? string.Empty);
    }

    /// <summary>
    /// Los ficheros que el arreglo tocó, con su «+N −M». «- Ninguno.» no da ninguno.
    /// <para>
    /// Los recuentos salen del CUERPO —el registro guarda rutas y huellas, no líneas—, y el
    /// <b>ámbito</b> de <paramref name="scopes"/>, que sale de las notas de la sesión (D-546). Sin
    /// notas, sin marca: no se deduce del texto del informe, porque el ámbito es una decisión que
    /// se tomó y se anotó, no una frase.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ReportFileChange> ReadFiles(
        string section, IReadOnlyDictionary<string, ReportFileScope>? scopes = null)
    {
        var result = new List<ReportFileChange>();
        foreach (string raw in (section ?? string.Empty).Split('\n'))
        {
            Match m = FileLine.Match(raw.TrimEnd('\r').Trim());
            if (m.Success)
            {
                string path = m.Groups["p"].Value;
                result.Add(new ReportFileChange(
                    path,
                    int.Parse(m.Groups["a"].Value, AppCulture.Display),
                    int.Parse(m.Groups["r"].Value, AppCulture.Display),
                    scopes is not null && scopes.TryGetValue(path, out ReportFileScope scope)
                        ? scope
                        : ReportFileScope.Desconocido));
            }
        }

        return result;
    }

    /// <summary>
    /// <b>El ámbito de cada fichero, leído de las notas de la SESIÓN</b> (D-546). El arreglo anota
    /// «tocado: ruta (+N −M)» por cada fichero y le añade «— fuera del hallazgo, autorizado por el
    /// usuario» a los que salieron del ámbito: la autorización queda ahí, que es donde se tomó.
    /// Una sesión sin esas notas devuelve un mapa vacío y las barras salen sin marca.
    /// </summary>
    internal static IReadOnlyDictionary<string, ReportFileScope> Scopes(AuditSession session)
    {
        var map = new Dictionary<string, ReportFileScope>(StringComparer.OrdinalIgnoreCase);
        foreach (string note in session.Notes)
        {
            Match m = TouchedNote.Match(note);
            if (m.Success)
            {
                // La marca va DETRÁS del recuento, así que se mira la nota entera y no el trozo
                // que casó: `m.Value` se queda en el paréntesis y todo salía «del hallazgo».
                map[m.Groups["p"].Value.Trim()] = note.Contains(
                    "fuera del hallazgo", StringComparison.Ordinal)
                    ? ReportFileScope.Autorizado
                    : ReportFileScope.Hallazgo;
            }
        }

        return map;
    }

    /// <summary>«tocado: ruta (+5 −5) — fuera del hallazgo, autorizado por el usuario».</summary>
    private static readonly Regex TouchedNote = new(
        @"^tocado:\s+(?<p>.+?)\s+\(\+\d+\s*−\d+\)", RegexOptions.Compiled);

    /// <summary>
    /// La compilación. <b>Verde y rojo salen de lo que el informe escribió</b>: rojo con errores
    /// nuevos o con tests que no pasan, verde con un veredicto sin ninguna de las dos cosas, y sin
    /// veredicto —«No se pidió compilar»— no se pinta ni verde ni rojo (D-318).
    /// </summary>
    internal static ReportBuild? ReadBuild(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return null;
        }

        // Lo que va DELANTE del primer detalle es el veredicto; lo demás —errores nuevos,
        // preexistentes, fuera del alcance y la salida completa— se pliega.
        int cut = Cut(section);
        string lead = (cut < 0 ? section : section[..cut]).TrimEnd();
        string detail = cut < 0 ? string.Empty : section[cut..].TrimEnd();

        string verdict = string.Empty;
        string tests = string.Empty;
        foreach (string raw in lead.Split('\n'))
        {
            Match f = Field.Match(raw.TrimEnd('\r').Trim());
            if (!f.Success)
            {
                continue;
            }

            switch (f.Groups["k"].Value.Trim())
            {
                case "Veredicto":
                    verdict = f.Groups["v"].Value.Trim();
                    break;
                case "Tests":
                    tests = f.Groups["v"].Value.Trim();
                    break;
            }
        }

        // Sin proyecto de tests se dice, y es un hecho del repositorio y no un resultado (H9.1 §3).
        bool noTests = section.Contains("no tiene proyecto de tests", StringComparison.Ordinal)
                       || section.Contains("no tiene proyectos de tests", StringComparison.Ordinal);

        if (verdict.Length == 0)
        {
            return new ReportBuild(
                ReportTone.Neutral, string.Empty,
                section.Contains("No se pidió compilar", StringComparison.Ordinal)
                    ? "no se pidió compilar"
                    : string.Empty,
                noTests ? "sin tests" : string.Empty,
                lead.Length > 0 ? StripHeading(lead) : string.Empty,
                detail);
        }

        Match errors = NewErrors.Match(verdict);
        bool red = (errors.Success && int.Parse(errors.Groups["n"].Value, AppCulture.Display) > 0)
                   || tests.Contains("NO pasan", StringComparison.Ordinal);

        return new ReportBuild(
            red ? ReportTone.Danger : ReportTone.Success,
            verdict,
            red ? PlainText.Of(verdict) : string.Empty,
            noTests ? "sin tests" : PlainText.Of(tests),
            StripHeading(lead),
            detail);
    }

    /// <summary>Dónde acaba el veredicto y empieza lo que se pliega.</summary>
    private static int Cut(string section)
    {
        int heading = section.IndexOf("\n### ", StringComparison.Ordinal);
        int details = section.IndexOf("\n<details>", StringComparison.Ordinal);
        return heading < 0 ? details : details < 0 ? heading : Math.Min(heading, details);
    }

    /// <summary>El encabezado de la sección se va: la tarjeta ya lleva su título.</summary>
    internal static string StripHeading(string section)
    {
        int nl = section.IndexOf('\n');
        return nl < 0 ? string.Empty : section[(nl + 1)..].Trim('\n', '\r').TrimEnd();
    }

    /// <summary>
    /// <b>El estado del arreglo tal y como lo dice el CUERPO.</b> Existe por dos motivos: es lo que
    /// se pinta cuando no hay registro —una sesión que no tocó nada no escribe uno—, y es la
    /// segunda lectura con la que el test comprueba que las dos fuentes no se han separado.
    /// </summary>
    /// <param name="touched">
    /// Cuántos ficheros lista el propio informe. <b>Manda sobre la cita</b> cuando dice cero: un
    /// informe anterior a R10 §7 escribía el aviso de «NO están commiteados» aunque la sesión no
    /// hubiera tocado nada, así que su cita y su lista de ficheros se contradicen. La lista es la
    /// que cuenta un hecho; la cita, en ese caso, advertía de unos cambios que no existían.
    /// </param>
    internal static ReportFixState? FixStateFromBody(string head, int touched)
    {
        if (touched == 0 && head.Contains("NO están commiteados", StringComparison.Ordinal))
        {
            return new ReportFixState(
                ReportFixStateKind.SinCambios, string.Empty,
                "el informe no lista ningún fichero tocado", FromRecord: false);
        }

        if (head.Contains("No hay cambios en el clon", StringComparison.Ordinal))
        {
            return new ReportFixState(
                ReportFixStateKind.SinCambios, string.Empty,
                "la sesión terminó sin tocar ningún fichero", FromRecord: false);
        }

        Match sha = Regex.Match(head, @"Commiteados en `(?<s>[0-9a-f]+)`");
        if (sha.Success)
        {
            return new ReportFixState(
                ReportFixStateKind.Commiteado, sha.Groups["s"].Value, string.Empty, FromRecord: false);
        }

        return head.Contains("NO están commiteados", StringComparison.Ordinal)
            ? new ReportFixState(
                ReportFixStateKind.SinCommitear, string.Empty,
                "los cambios están en el clon de quien lo lanzó", FromRecord: false)
            : null;
    }

    /// <summary>
    /// El estado que dice el REGISTRO (D-1033), que es el que manda. <c>null</c> cuando no hay
    /// registro: un arreglo que no tocó nada no escribe ninguno.
    /// </summary>
    internal static ReportFixState? FixStateFromRecord(FixRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        if (record.Files.Count == 0)
        {
            return new ReportFixState(
                ReportFixStateKind.SinCambios, string.Empty,
                "el registro no anotó ningún fichero", FromRecord: true);
        }

        if (record.CommitSha is not { Length: > 0 } sha)
        {
            return new ReportFixState(
                ReportFixStateKind.SinCommitear, string.Empty,
                "los cambios están en el clon de quien lo lanzó", FromRecord: true);
        }

        // Con quién quedó firmado (BUGFIX-F32-2). Los registros anteriores no lo traen y ahí no se
        // escribe nada, en vez de un «como (desconocido)».
        return new ReportFixState(
            ReportFixStateKind.Commiteado, sha,
            record.CommitAuthor is { Length: > 0 } who ? $"como {who}" : string.Empty,
            FromRecord: true);
    }
}
