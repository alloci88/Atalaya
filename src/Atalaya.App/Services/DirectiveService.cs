using Atalaya.Copilot;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Una fila del panel de directivas (F7): lo que el catálogo propuso, lo que el hub registró, o
/// las dos cosas.
/// </summary>
/// <param name="Registered">La entrada del hub, o null si es un candidato que nadie ha mirado.</param>
/// <param name="Candidate">Lo que encontró el escaneo, o null si el fichero ya no está en el clon.</param>
public sealed record DirectiveEntry(ProjectDirective? Registered, DirectiveCandidate? Candidate)
{
    public string Path => Registered?.Path ?? Candidate!.Path;

    public string Kind => Registered?.Kind ?? Candidate!.Kind;

    public DirectiveScope Scope => Registered?.Scope ?? DirectiveScope.Ninguno;

    public int Order => Registered?.Order ?? 0;

    /// <summary>
    /// Registrada y sin fichero en el clon. No es un error ni rompe nada: alguien renombró o
    /// borró el fichero en el repo de la app, que es donde manda. Se enseña como «no encontrada»
    /// para que una persona decida si actualizar la ruta o retirar la entrada.
    /// </summary>
    public bool Missing => Candidate is null;

    /// <summary>
    /// Nadie la ha mirado todavía: el catálogo la propone y el hub no sabe nada de ella. Es lo que
    /// se anuncia tras un re-escaneo, y lo que NUNCA se activa solo.
    /// </summary>
    public bool IsNew => Registered is null;
}

/// <summary>
/// Las directivas de una aplicación: qué propone el catálogo, qué ha curado el equipo, y qué
/// viaja en cada prompt (F7).
/// <para>
/// Es el único sitio que sabe juntar las dos mitades de la funcionalidad —el REGISTRO, que vive en
/// el hub, y el CONTENIDO, que vive en el repo de la app— y por eso es también el único que lee el
/// clon. Los tres flujos (auditoría, verificación y arreglo) le piden un <see cref="DirectiveBundle"/>
/// y no vuelven a decidir nada: el presupuesto se aplica aquí, una vez, para todos.
/// </para>
/// </summary>
public sealed class DirectiveService
{
    private readonly HubContext _hub;
    private readonly DirectiveScanner _scanner;
    private readonly IUlidFactory _ulids;

    public DirectiveService(HubContext hub, DirectiveScanner scanner, IUlidFactory ulids)
    {
        _hub = hub;
        _scanner = scanner;
        _ulids = ulids;
    }

    private string Me => _hub.ResolveIdentity().Name;

    // ------------------------------------------------------------------ el panel

    /// <summary>
    /// La lista que ve el panel: la unión de lo detectado en el clon y lo registrado en el hub.
    /// <para>
    /// Las dos mitades hacen falta. Solo lo detectado perdería las entradas añadidas a mano y las
    /// que apuntan a un fichero que ya no está; solo lo registrado no propondría nada nuevo nunca.
    /// El orden es el de inclusión —prioridad, y ruta para deshacer los empates— para que la lista
    /// se lea como el prompt se compone.
    /// </para>
    /// </summary>
    public DirectiveInventory Inventory(string slug, string? clonePath)
    {
        IReadOnlyList<ProjectDirective> registered = _hub.Store.ListDirectives(slug);
        DirectiveScanOutput scan = _scanner.Scan(clonePath ?? string.Empty);

        var byPath = new Dictionary<string, DirectiveCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (DirectiveCandidate c in scan.Candidates)
        {
            byPath[c.Path] = c;
        }

        var rows = new List<DirectiveEntry>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectDirective d in registered)
        {
            // Si el catálogo no la propuso, se comprueba en disco antes de darla por perdida. Una
            // directiva añadida a mano NUNCA está entre los candidatos —por definición: se añade
            // precisamente porque el catálogo no conoce su ruta— y darla por «no encontrada» habría
            // dejado permanentemente rota la válvula de F7 §1.
            DirectiveCandidate? candidate = byPath.TryGetValue(d.Path, out DirectiveCandidate? c)
                ? c
                : Present(clonePath, d);

            rows.Add(new DirectiveEntry(d, candidate));
            claimed.Add(d.Path);
        }

        foreach (DirectiveCandidate c in scan.Candidates)
        {
            if (!claimed.Contains(c.Path))
            {
                rows.Add(new DirectiveEntry(null, c));
            }
        }

        rows.Sort(Compare);
        return new DirectiveInventory(rows, scan.Truncated, clonePath is { Length: > 0 });
    }

    /// <summary>
    /// Candidatos que el clon tiene y el hub no conoce (F7 §1). Es lo que un re-escaneo anuncia
    /// con un aviso, y nada más: <b>no se activa ninguno</b>. La curación es humana porque la
    /// misma ruta significa cosas distintas en proyectos distintos, y una directiva activada sola
    /// cambiaría en silencio el criterio con el que se audita.
    /// </summary>
    public IReadOnlyList<string> NewCandidates(string slug, string? clonePath)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return Array.Empty<string>();
        }

        var known = new HashSet<string>(
            _hub.Store.ListDirectives(slug).Select(d => d.Path), StringComparer.OrdinalIgnoreCase);

        return _scanner.Scan(clonePath!).Candidates
            .Where(c => !known.Contains(c.Path))
            .Select(c => c.Path)
            .ToList();
    }

    // ------------------------------------------------------------------ curación

    /// <summary>
    /// Fija el ámbito de una directiva. Crea la entrada si no existía —es lo que pasa al marcar un
    /// candidato— y la conserva con ámbito <c>Ninguno</c> al desmarcarla, en vez de borrarla: una
    /// entrada borrada volvería a anunciarse como candidato nuevo en el siguiente re-escaneo, y el
    /// equipo tendría que volver a decidir lo que ya decidió.
    /// </summary>
    public ProjectDirective SetScope(string slug, string path, string kind, DirectiveScope scope)
    {
        ProjectDirective? existing = _hub.Store.ListDirectives(slug)
            .FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

        ProjectDirective directive = existing ?? new ProjectDirective
        {
            Id = _ulids.NewUlid(),
            Path = path,
            Kind = kind,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
        };

        directive.Scope = scope;
        directive.By = Me;
        directive.Utc = DateTimeOffset.UtcNow;
        _hub.Store.WriteDirective(slug, directive);
        Push(slug, $"directives: {path} → {DirectiveScopeNames.Display(scope).ToLowerInvariant()}");
        return directive;
    }

    /// <summary>Cambia la prioridad de inclusión. Menor entra antes cuando el presupuesto aprieta.</summary>
    public bool SetOrder(string slug, Ulid id, int order)
    {
        ProjectDirective? directive = _hub.Store.TryReadDirective(slug, id);
        if (directive is null)
        {
            return false;
        }

        directive.Order = order;
        _hub.Store.WriteDirective(slug, directive);
        Push(slug, $"directives: prioridad de {directive.Path} = {order}");
        return true;
    }

    /// <summary>
    /// Añade a mano un fichero del repo que el catálogo no conoce. Es la válvula de F7 §1: un
    /// proyecto con sus propias costumbres no tiene que esperar a que alguien amplíe el catálogo.
    /// </summary>
    /// <returns>Null si la ruta no existe en el clon, o si ya estaba registrada.</returns>
    public ProjectDirective? AddManual(string slug, string? clonePath, string relativePath, DirectiveScope scope)
    {
        string path = (relativePath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || string.IsNullOrWhiteSpace(clonePath))
        {
            return null;
        }

        if (!File.Exists(Path.Combine(clonePath!, path.Replace('/', Path.DirectorySeparatorChar))))
        {
            return null;
        }

        if (_hub.Store.ListDirectives(slug)
            .Any(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return SetScope(slug, path, kind: "manual", scope);
    }

    /// <summary>
    /// Cambia el presupuesto de directivas de ESTA aplicación (F7 §2). 0 lo apaga.
    /// <para>
    /// Se edita aquí y no en Ajustes a propósito. El presupuesto es por-aplicación —vive en
    /// <c>app.json</c>, y una app con un monorepo lleno de ADRs no necesita lo mismo que una con
    /// un CLAUDE.md— mientras que Ajustes guarda los valores por defecto de ESTA MÁQUINA, que no
    /// llegan a las apps ya dadas de alta. Un campo allí sería un control conectado a nada, que es
    /// justo lo que F5.7 vino a quitar. Y aquí se ve su consecuencia mientras se decide: el panel
    /// enseña el consumo de lo activado contra el número que se está escribiendo.
    /// </para>
    /// </summary>
    public bool SetBudget(string slug, int tokens)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return false;
        }

        app.Thresholds.DirectiveTokenBudget = Math.Max(0, tokens);
        _hub.Store.WriteApp(app);
        Push(slug, $"directives: presupuesto de {slug} = {app.Thresholds.DirectiveTokenBudget} tokens");
        return true;
    }

    /// <summary>Retira la entrada del registro. La usa quien quiere olvidar una «no encontrada».</summary>
    public bool Forget(string slug, Ulid id)
    {
        ProjectDirective? directive = _hub.Store.TryReadDirective(slug, id);
        if (!_hub.Store.DeleteDirective(slug, id))
        {
            return false;
        }

        Push(slug, $"directives: retirada {directive?.Path ?? id.ToString()}");
        return true;
    }

    // ------------------------------------------------------------------ lo que viaja en el prompt

    /// <summary>
    /// Las directivas de <paramref name="purpose"/> leídas del clon y recortadas al presupuesto de
    /// la app.
    /// <para>
    /// Se lee SIEMPRE del clon y nunca de una caché: la gracia de que el contenido viva en el repo
    /// de la aplicación es que la auditoría de hoy use las convenciones de hoy. Un fichero que ya
    /// no está simplemente no viaja — la entrada queda «no encontrada» en el panel y ni la sesión
    /// ni el arreglo se caen por ello.
    /// </para>
    /// </summary>
    /// <param name="purpose">
    /// <see cref="DirectiveScope.Auditoria"/> o <see cref="DirectiveScope.Arreglo"/>. Nunca
    /// <c>Ambos</c>: eso es un ámbito de curación, no un uso.
    /// </param>
    public DirectiveBundle Bundle(string slug, string? clonePath, DirectiveScope purpose)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        int budget = Math.Max(0, app?.Thresholds.DirectiveTokenBudget ?? 0);
        if (budget == 0 || string.IsNullOrWhiteSpace(clonePath))
        {
            return DirectiveBundle.Empty;
        }

        List<ProjectDirective> applicable = _hub.Store.ListDirectives(slug)
            .Where(d => d.AppliesTo(purpose))
            .ToList();

        applicable.Sort((a, b) =>
        {
            int byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        var docs = new List<DirectiveDoc>();
        foreach (ProjectDirective d in applicable)
        {
            string abs = Path.Combine(clonePath!, d.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                byte[] bytes = File.ReadAllBytes(abs);
                docs.Add(new DirectiveDoc(d.Path, ReadText(bytes), HashUtil.Sha256Hex(bytes)));
            }
            catch
            {
                // No encontrada o ilegible: no viaja. La causa se ve en el panel, que es donde se
                // puede hacer algo al respecto; hacer caer una sesión por esto sería castigar al
                // auditor por un renombrado en otro repositorio.
            }
        }

        return DirectiveBudget.Apply(docs, budget);
    }

    /// <summary>
    /// La directiva registrada, vista en disco: existe y cuánto ocupa, o null si ya no está. Es lo
    /// que separa «el catálogo no la propone» de «el fichero no está», que son cosas distintas.
    /// </summary>
    private static DirectiveCandidate? Present(string? clonePath, ProjectDirective directive)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(
                Path.Combine(clonePath!, directive.Path.Replace('/', Path.DirectorySeparatorChar)));

            return info.Exists
                ? new DirectiveCandidate(
                    directive.Path,
                    directive.Kind,
                    "Registrada por el equipo: el catálogo no propone esta ruta.",
                    info.Length)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>El texto de un fichero de directivas, sin BOM y con saltos normalizados.</summary>
    private static string ReadText(byte[] bytes)
    {
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n");
    }

    private void Push(string slug, string message) => _hub.Sync?.CommitAndPush(message);

    private static int Compare(DirectiveEntry a, DirectiveEntry b)
    {
        int byOrder = a.Order.CompareTo(b.Order);
        return byOrder != 0 ? byOrder : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Lo que el panel necesita saber de una vez (F7): las filas, si el escaneo se cortó, y si
/// siquiera había un clon que escanear.
/// </summary>
/// <param name="Scanned">
/// False cuando no hay clon vinculado. Sin esto, un panel con solo las entradas del hub se leería
/// como «el catálogo no encontró nada», que es una conclusión distinta de «no se ha podido mirar».
/// </param>
public sealed record DirectiveInventory(
    IReadOnlyList<DirectiveEntry> Entries, bool Truncated, bool Scanned);
