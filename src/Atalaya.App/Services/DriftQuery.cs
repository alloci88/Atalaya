using System.Diagnostics;
using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using LibGit2Sharp;

namespace Atalaya.App.Services;

/// <summary>
/// Qué ha cambiado en el código desde que se auditó (F9 §1). Es la consulta que sostiene la
/// operación de cada sprint: auditar lo que ha cambiado, en vez de repetir un ciclo entero.
/// <para>
/// <b>La deriva es DERIVADA y no se persiste jamás.</b> Se calcula del historial del clon local en
/// cada consulta. En el hub solo viven hechos —el commit de cada auditoría, las huellas de lo que
/// dejó cada arreglo—; un «cambiada: sí» guardado sería un dato que envejece solo, que dos máquinas
/// pueden contradecirse y que nadie sabría cuándo invalidar.
/// </para>
/// <para>
/// <b>Y siempre commit contra commit.</b> Nunca contra el árbol de trabajo: un fichero a medio
/// editar o un <c>core.autocrlf</c> distinto convertirían medio repositorio en deriva inventada.
/// Lo que hay sin commitear se dice aparte, como aviso, porque es justo lo que este análisis NO ve.
/// </para>
/// </summary>
public sealed class DriftQuery
{
    private readonly HubContext _hub;

    /// <summary>
    /// La caché, por aplicación. La clave la forman HEAD, el mapa de unidad→commit-de-auditoría y
    /// cuántos arreglos hay registrados: si cambia cualquiera de los tres, el resultado ya no vale
    /// y la clave deja de casar sola. No hay que acordarse de invalidarla al auditar ni al arreglar.
    /// </summary>
    private readonly Dictionary<string, (string Key, AppDrift Value)> _cache = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    public DriftQuery(HubContext hub)
    {
        _hub = hub;

        // La invalidación tras un pull se engancha AQUÍ y no en quien consulta (F9 §1.2): un pull
        // puede traer la sesión de otra máquina que re-auditó una unidad, y eso cambia la deriva sin
        // que HEAD del clon se haya movido. Colgándolo del servicio, ninguna vista futura puede
        // olvidarse de hacerlo.
        _hub.Changed += _ => Invalidate();
    }

    /// <summary>
    /// Tira la caché entera. Se llama tras un pull del hub: una sesión de otra máquina puede haber
    /// cambiado el commit de auditoría de una unidad sin que HEAD del clon se haya movido, y esa es
    /// la única forma de que la clave no se entere sola (F9 §1.2).
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// La deriva de una aplicación, cacheada. <paramref name="clonePath"/> es la ruta del clon en
    /// ESTA máquina: el hub no sabe nada de las rutas locales de nadie y no debe (§4).
    /// </summary>
    public AppDrift For(string slug, string? clonePath)
    {
        string key = KeyFor(slug, clonePath);
        lock (_gate)
        {
            if (_cache.TryGetValue(slug, out var hit) && hit.Key == key)
            {
                return hit.Value;
            }
        }

        AppDrift computed = Compute(slug, clonePath);
        lock (_gate)
        {
            _cache[slug] = (key, computed);
        }

        return computed;
    }

    /// <summary>Lo que hay en caché para esta app, o null. No calcula nada.</summary>
    public AppDrift? Cached(string slug)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(slug, out var hit) ? hit.Value : null;
        }
    }

    private string KeyFor(string slug, string? clonePath)
    {
        string head = GitInfo.HeadSha(clonePath);
        AppConfig? app = _hub.Store.TryReadApp(slug);
        InventoryCycle? inv = app is null ? null : _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        var audited = inv?.Units
            .Where(u => u.State == UnitState.Auditada)
            .OrderBy(u => u.Path, StringComparer.Ordinal)
            .Select(u => $"{u.Path}={u.AuditedInSession}")
            ?? Enumerable.Empty<string>();

        return $"{head}|{clonePath}|{_hub.Store.ListFixes(slug).Count}|"
            + HashUtil.Sha256Hex(string.Join('\n', audited));
    }

    /// <summary>
    /// El cálculo, sin caché. Público para que los tests puedan ejercitarlo directamente sobre un
    /// repositorio de verdad, que es la única forma de probar algo que habla con git.
    /// </summary>
    public AppDrift Compute(string slug, string? clonePath)
    {
        var clock = Stopwatch.StartNew();

        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return AppDrift.Unavailable(slug, "Esta aplicación ya no está en el hub.");
        }

        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return AppDrift.Unavailable(slug,
                "Vincula tu clon para ver la deriva: sin el código no hay historial que comparar.");
        }

        if (!Repository.IsValid(clonePath))
        {
            return AppDrift.Unavailable(slug,
                $"{clonePath} no es un repositorio git, así que no hay historial que comparar.");
        }

        InventoryCycle inventory = _hub.Store.TryReadInventory(slug, app.CurrentCycle)
            ?? new InventoryCycle { CycleN = app.CurrentCycle };

        var commitOfSession = _hub.Store.ListSessions(slug)
            .Where(s => !string.IsNullOrWhiteSpace(s.Commit))
            .GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => g.First().Commit!);

        try
        {
            using var repo = new Repository(clonePath);
            Commit? head = repo.Head.Tip;
            if (head is null)
            {
                return AppDrift.Unavailable(slug, "El clon no tiene ningún commit todavía.");
            }

            var context = new Context(repo, head, inventory, commitOfSession, _hub.Store.ListFixes(slug));
            List<UnitDrift> units = Classify(context);
            List<OrphanFinding> orphans = FindOrphans(repo, head, _hub.Store.ListFindings(slug));

            WorkingTreeState tree = WorkingTree.Inspect(clonePath);
            string branch = repo.Head.FriendlyName;

            return new AppDrift(slug, units, orphans)
            {
                Branch = branch,
                BranchIsDefault = IsDefaultBranch(repo, branch),
                Head = head.Sha[..Math.Min(7, head.Sha.Length)],
                UncommittedFiles = tree.Problem is null && !tree.Clean ? tree.Total : 0,
                BehindRemote = repo.Head.TrackingDetails?.BehindBy > 0,
                Elapsed = clock.Elapsed,
            };
        }
        catch (LibGit2SharpException ex)
        {
            // N-2: la incertidumbre se declara. Un catch que devolviera «sin cambios» convertiría
            // un repositorio ilegible en una foto tranquilizadora y falsa.
            return AppDrift.Unavailable(slug, $"No se pudo leer el historial del clon: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ clasificación

    /// <summary>Lo que hace falta para clasificar, junto: se pasa una vez y no se vuelve a leer.</summary>
    private sealed record Context(
        Repository Repo,
        Commit Head,
        InventoryCycle Inventory,
        IReadOnlyDictionary<Ulid, string> CommitOfSession,
        IReadOnlyList<FixRecord> Fixes);

    /// <summary>
    /// Un grupo de unidades que comparten commit de auditoría, ya anclado contra el historial. El
    /// recorrido se hace UNA vez para todos los grupos, así que hay que tenerlos todos planteados
    /// antes de empezar a mirar commits (F9 §1.2).
    /// </summary>
    private sealed class GroupPlan
    {
        public required Commit AuditCommit { get; init; }

        public required IReadOnlyList<InventoryUnit> Units { get; init; }

        /// <summary>Los commits de <c>auditCommit..HEAD</c>. Se obtienen sin difear nada (ms).</summary>
        public HashSet<string> Range { get; } = new(StringComparer.Ordinal);
    }

    private static List<UnitDrift> Classify(Context ctx)
    {
        var results = new List<UnitDrift>();
        var audited = ctx.Inventory.Units.Where(u => u.State == UnitState.Auditada).ToList();

        // Una unidad auditada cuya sesión no dejó commit —o cuya sesión ya no está— no es «sin
        // cambios»: es una unidad de la que no sabemos contra qué compararla.
        var withCommit = new List<(InventoryUnit Unit, string Sha)>();
        foreach (InventoryUnit u in audited)
        {
            string? sha = u.AuditedInSession is { } id && ctx.CommitOfSession.TryGetValue(id, out string? s)
                ? s
                : null;

            if (string.IsNullOrWhiteSpace(sha) || sha == "unknown")
            {
                results.Add(new UnitDrift(u.Path, DriftState.HistorialNoDisponible, Note:
                    "Su auditoría no registró contra qué commit se hizo, así que no hay nada con lo "
                    + "que comparar. Re-auditarla la deja anclada."));
            }
            else
            {
                withCommit.Add((u, sha));
            }
        }

        // UN grupo por commit de auditoría DISTINTO (F9 §1.2). Con 900 unidades auditadas en la
        // misma sesión eso es un grupo, no novecientos.
        var plans = new List<GroupPlan>();
        foreach (var group in withCommit.GroupBy(x => x.Sha, StringComparer.OrdinalIgnoreCase))
        {
            var units = group.Select(x => x.Unit).ToList();
            if (Anchor(ctx, group.Key, units, results) is { } plan)
            {
                plans.Add(plan);
            }
        }

        if (plans.Count == 0)
        {
            return results;
        }

        // El rango de cada grupo: recorrer sin difear cuesta milisegundos.
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (GroupPlan plan in plans)
        {
            foreach (Commit commit in ctx.Repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = ctx.Head,
                ExcludeReachableFrom = plan.AuditCommit,
                SortBy = CommitSortStrategies.Topological,
            }))
            {
                plan.Range.Add(commit.Sha);
            }

            union.UnionWith(plan.Range);
        }

        var tracked = new Tracked(plans.SelectMany(p => p.Units).Select(u => Normalize(u.Path)));
        IReadOnlyDictionary<string, IReadOnlyList<Touch>> touches = DiffPass(ctx, union, tracked);

        foreach (GroupPlan plan in plans)
        {
            results.AddRange(Verdicts(ctx, plan, touches, tracked));
        }

        return results;
    }

    /// <summary>
    /// Ancla un grupo contra el historial (F9 §1.1): decide si hay rango que recorrer o si el estado
    /// honesto es «no se puede saber». Los casos resueltos se escriben directamente en
    /// <paramref name="results"/>; solo el caso normal devuelve plan.
    /// </summary>
    private static GroupPlan? Anchor(
        Context ctx, string auditSha, IReadOnlyList<InventoryUnit> units, List<UnitDrift> results)
    {
        Commit? auditCommit = TryLookup(ctx.Repo, auditSha);
        if (auditCommit is null)
        {
            results.AddRange(Unavailable(units,
                $"El commit {auditSha} de su auditoría no está en este clon (clon superficial, recién "
                + "hecho, o con el historial podado). Re-auditarla es lo recomendable."));
            return null;
        }

        if (auditCommit.Sha == ctx.Head.Sha)
        {
            results.AddRange(units.Select(u => new UnitDrift(u.Path, DriftState.SinCambios)));
            return null;
        }

        // merge-base ANTES de difear: un diff entre ramas divergentes daría cambios fantasma.
        Commit? mergeBase = ctx.Repo.ObjectDatabase.FindMergeBase(auditCommit, ctx.Head);
        if (mergeBase is null)
        {
            results.AddRange(Unavailable(units,
                $"El commit {auditSha} de su auditoría y HEAD no comparten historia. Un diff entre "
                + "ramas divergentes daría cambios fantasma, así que no se hace: re-auditarla es lo "
                + "recomendable."));
            return null;
        }

        if (mergeBase.Sha == ctx.Head.Sha)
        {
            // Se auditó en otra máquina, en un commit que este clon todavía no tiene.
            results.AddRange(Unavailable(units,
                $"Se auditó en el commit {auditSha}, que va por DELANTE de tu HEAD: haz pull para ver "
                + "la deriva real. Lo que se viera ahora sería la de un clon atrasado."));
            return null;
        }

        if (mergeBase.Sha != auditCommit.Sha)
        {
            results.AddRange(Unavailable(units,
                $"El commit {auditSha} de su auditoría existe pero no es antecesor de HEAD: el "
                + "historial se ha reescrito (rebase o force-push). Re-auditarla es lo recomendable."));
            return null;
        }

        return new GroupPlan { AuditCommit = auditCommit, Units = units };
    }

    /// <summary>
    /// Las rutas que interesan y a qué unidad pertenece cada una. Empieza siendo la ruta de cada
    /// unidad auditada y CRECE con los renombrados que aparecen en el recorrido: una unidad movida
    /// se llamaba de otra forma en la mitad del rango, y sin esto saldría con «0 commits».
    /// </summary>
    private sealed class Tracked
    {
        private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);

        /// <summary>
        /// El índice inverso. Existe porque sin él responder «¿por qué nombres ha pasado esta
        /// unidad?» recorría el diccionario entero por unidad: con 925 unidades y ocho grupos eso
        /// son siete millones de comparaciones, y se notaban — 6,3 s de los que 5 eran esto.
        /// </summary>
        private readonly Dictionary<string, List<string>> _aliases = new(StringComparer.Ordinal);

        public Tracked(IEnumerable<string> unitPaths)
        {
            foreach (string path in unitPaths)
            {
                if (_canonical.TryAdd(path, path))
                {
                    _aliases[path] = new List<string> { path };
                }
            }
        }

        /// <summary>Todas las rutas conocidas de una unidad, incluida la suya.</summary>
        public IReadOnlyList<string> AliasesOf(string unitPath)
            => _aliases.TryGetValue(unitPath, out List<string>? list) ? list : Array.Empty<string>();

        public bool TryCanonical(string path, out string unitPath)
            => _canonical.TryGetValue(path, out unitPath!);

        /// <summary>
        /// Un renombrado <paramref name="from"/>→<paramref name="to"/>. Se aprende en las DOS
        /// direcciones: el inventario puede llevar todavía la ruta vieja (si nadie ha re-escaneado)
        /// o ya la nueva (si el re-escaneo arrastró el estado auditado), y los dos casos son reales.
        /// </summary>
        public void Learn(string from, string to)
        {
            if (_canonical.TryGetValue(from, out string? unit))
            {
                Add(to, unit);
            }
            else if (_canonical.TryGetValue(to, out unit))
            {
                Add(from, unit);
            }
        }

        private void Add(string path, string unit)
        {
            if (_canonical.TryAdd(path, unit))
            {
                _aliases[unit].Add(path);
            }
        }
    }

    /// <summary>Un commit tocó una unidad, y era un arreglo nuestro o no lo era.</summary>
    private readonly record struct Touch(string UnitPath, bool Ours, DateTimeOffset When);

    /// <summary>
    /// El ÚNICO sitio donde se difea. Se recorre la unión de los rangos de todos los grupos, una
    /// vez, y se guarda por commit qué unidades tocó.
    /// <para>
    /// <b>Por qué no hay un diff de árbol a árbol.</b> Lo había, y era el cuello de botella: medido
    /// sobre xblast, comparar el árbol de HEAD~1500 con el de HEAD cuesta <b>9,8 s</b> —y uno por
    /// grupo—, mientras que recorrer esos mismos 1.500 commits difeando cada uno contra su padre
    /// cuesta <b>1,3 s</b> en total. Los diffs entre commits contiguos son diminutos; el de dos
    /// árboles separados por año y medio de historia, no. Y esta pasada hacía falta igualmente para
    /// contar commits y atribuir los arreglos propios, así que el árbol contra árbol era, además,
    /// trabajo duplicado.
    /// </para>
    /// <para>
    /// <b>Los merges no se cuentan.</b> Un merge que trae veinte commits volvería a contar los
    /// veinte, y «21 commits la tocaron» sería falso. Es la misma lectura que <c>--no-merges</c>:
    /// se cuenta el trabajo, no el acto de juntarlo.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Touch>> DiffPass(
        Context ctx, HashSet<string> union, Tracked tracked)
    {
        var result = new Dictionary<string, IReadOnlyList<Touch>>(StringComparer.Ordinal);
        if (union.Count == 0)
        {
            return result;
        }

        Dictionary<string, HashSet<string>> fixHashes = FixHashesByPath(ctx.Fixes);

        // Del más VIEJO al más nuevo: así, cuando un commit renombra un fichero, lo aprendido vale
        // para todos los commits que vienen después, que son los que usan el nombre nuevo.
        var options = new CompareOptions { Similarity = SimilarityOptions.Renames };
        int seen = 0;

        foreach (Commit commit in ctx.Repo.Commits.QueryBy(new CommitFilter
        {
            IncludeReachableFrom = ctx.Head,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
        }))
        {
            if (!union.Contains(commit.Sha))
            {
                continue;
            }

            seen++;
            if (commit.Parents.Count() <= 1)
            {
                Commit? parent = commit.Parents.FirstOrDefault();
                TreeChanges diff = ctx.Repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree, options);

                List<Touch>? touches = null;
                foreach (TreeEntryChanges change in diff)
                {
                    string path = Normalize(change.Path);
                    if (change.Status == ChangeKind.Renamed && change.OldPath is { Length: > 0 } old)
                    {
                        tracked.Learn(Normalize(old), path);
                    }

                    if (!tracked.TryCanonical(path, out string unit))
                    {
                        continue;
                    }

                    touches ??= new List<Touch>();
                    touches.Add(new Touch(
                        unit, IsOurFix(commit, change.Path, fixHashes), commit.Committer.When));
                }

                if (touches is not null)
                {
                    result[commit.Sha] = touches;
                }
            }

            if (seen == union.Count)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>El veredicto de cada unidad del grupo, ya con todo difeado.</summary>
    private static IEnumerable<UnitDrift> Verdicts(
        Context ctx, GroupPlan plan,
        IReadOnlyDictionary<string, IReadOnlyList<Touch>> touches, Tracked tracked)
    {
        var tally = new Dictionary<string, UnitTally>(StringComparer.Ordinal);
        foreach (string sha in plan.Range)
        {
            if (!touches.TryGetValue(sha, out IReadOnlyList<Touch>? list))
            {
                continue;
            }

            foreach (Touch touch in list)
            {
                if (!tally.TryGetValue(touch.UnitPath, out UnitTally? t))
                {
                    tally[touch.UnitPath] = t = new UnitTally();
                }

                if (touch.Ours)
                {
                    t.Own++;
                }
                else
                {
                    t.Foreign++;
                }

                if (t.Last is null || touch.When > t.Last)
                {
                    t.Last = touch.When;
                }
            }
        }

        foreach (InventoryUnit u in plan.Units)
        {
            string path = Normalize(u.Path);
            tally.TryGetValue(path, out UnitTally? t);
            int foreign = t?.Foreign ?? 0;
            int own = t?.Own ?? 0;

            // Dónde ha ido a parar: puede seguir donde estaba, haberse movido, o ya no estar. Se
            // pregunta SIEMPRE, incluso cuando ningún commit del rango la tocó: un fichero que
            // desapareció solo dentro de un merge —y los merges no se cuentan— no puede salir como
            // «sin cambios» cuando en HEAD no está.
            string? livePath = tracked.AliasesOf(path).FirstOrDefault(p => ctx.Head[p] is not null);
            if (livePath is null)
            {
                yield return new UnitDrift(u.Path, DriftState.Borrada, foreign + own, t?.Last, own);
                continue;
            }

            if (t is null)
            {
                yield return new UnitDrift(u.Path, DriftState.SinCambios);
                continue;
            }

            string? moved = livePath == path ? null : livePath;

            // El guardarraíl anti-bucle (F9 §2): si lo único que la ha tocado son arreglos de la
            // propia aplicación, esto no es deriva — es trabajo pendiente de VERIFICAR.
            bool onlyOurs = foreign == 0 && own > 0;
            DriftState state = onlyOurs && own < DriftRules.MaxOwnFixesBeforeReaudit
                ? DriftState.ArregladaPendienteDeVerificar
                : DriftState.Modificada;

            // Cuando la unidad pasa a «cambiada» por acumulación, lo que la ha tocado son esos
            // arreglos: el número que se enseña tiene que ser el de commits que la tocaron, o la
            // etiqueta diría «0 commits».
            int shown = state == DriftState.Modificada && foreign == 0 ? own : foreign;
            yield return new UnitDrift(u.Path, state, shown, t.Last, own, moved);
        }
    }

    private sealed class UnitTally
    {
        public int Foreign;
        public int Own;
        public DateTimeOffset? Last;
    }

    /// <summary>
    /// Las huellas que dejó cada arreglo, por ruta. Una ruta puede acumular varias: tres arreglos
    /// sobre el mismo fichero dejan tres contenidos distintos, y los tres son nuestros.
    /// </summary>
    private static Dictionary<string, HashSet<string>> FixHashesByPath(IReadOnlyList<FixRecord> fixes)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (FixRecord record in fixes)
        {
            foreach (FixFileStamp file in record.Files)
            {
                string path = Normalize(file.Path);
                if (!map.TryGetValue(path, out HashSet<string>? set))
                {
                    map[path] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(file.ContentHash);
            }
        }

        return map;
    }

    /// <summary>
    /// ¿Este commit recogió un arreglo NUESTRO de este fichero? Se responde comparando el contenido
    /// del fichero EN ese commit con la huella que el arreglo dejó registrada (F9 §2).
    /// <para>
    /// Es una prueba, no una aproximación: si el contenido casa, ese commit publicó exactamente lo
    /// que escribió el agente. Y degrada en la dirección segura — si el usuario enmienda, aplasta o
    /// rebasa antes de publicar, el contenido ya no casa y la unidad sale como «cambiada»:
    /// re-auditar de más, nunca de menos.
    /// </para>
    /// </summary>
    private static bool IsOurFix(
        Commit commit, string path, Dictionary<string, HashSet<string>> fixHashes)
    {
        if (fixHashes.Count == 0 || !fixHashes.TryGetValue(Normalize(path), out HashSet<string>? expected))
        {
            return false;
        }

        if (commit[path]?.Target is not Blob blob)
        {
            return false;
        }

        try
        {
            using Stream content = blob.GetContentStream();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            return expected.Contains(HashUtil.NormalizedContentHash(buffer.ToArray()));
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ huérfanos y borrados

    /// <summary>
    /// Los hallazgos ACTIVOS cuyo código ya no está en HEAD (F9 §4). No se resuelve nada: se
    /// informa, y la acción la ejecuta una persona.
    /// <para>
    /// Se calculan sobre los HALLAZGOS y no sobre el inventario a propósito: un re-escaneo saca del
    /// inventario la unidad borrada, y a partir de ese momento el único sitio donde queda rastro del
    /// código que ya no está son sus hallazgos. Mirar solo el inventario los dejaría zombis.
    /// </para>
    /// </summary>
    private static List<OrphanFinding> FindOrphans(
        Repository repo, Commit head, IReadOnlyList<Finding> findings)
    {
        var exists = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Exists(string path)
        {
            if (!exists.TryGetValue(path, out bool value))
            {
                exists[path] = value = head[path] is not null;
            }

            return value;
        }

        var orphans = new List<OrphanFinding>();
        foreach (Finding f in findings.Where(f => f.Status == FindingStatus.Activo))
        {
            var paths = f.Locations
                .Select(l => Normalize(l.Path))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (paths.Count == 0 || paths.Any(Exists))
            {
                continue;
            }

            orphans.Add(new OrphanFinding(
                paths[0], f.Id.ToString(), f.DisplayId, f.Title, f.Severity, null, null));
        }

        return orphans
            .OrderBy(o => o.Severity)
            .ThenBy(o => o.UnitPath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// El commit que borró un fichero, buscado bajo demanda (F9 §4). Es la EVIDENCIA de una
    /// resolución por código eliminado, y por eso no se calcula con el resto: cuesta un recorrido
    /// del historial por ruta y solo hace falta cuando alguien va a ejecutar la acción.
    /// </summary>
    public (string? Sha, DateTimeOffset? Utc) FindDeletion(string? clonePath, string unitPath)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || !Repository.IsValid(clonePath))
        {
            return (null, null);
        }

        try
        {
            using var repo = new Repository(clonePath);
            Commit? head = repo.Head.Tip;
            if (head is null || head[unitPath] is not null)
            {
                return (null, null);
            }

            // El primer commit hacia atrás en el que la ruta pasó de estar a no estar. Se recorre
            // desde HEAD: la respuesta suele estar en los últimos commits, no en los primeros.
            foreach (Commit commit in repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = head,
                SortBy = CommitSortStrategies.Topological,
            }))
            {
                if (commit[unitPath] is not null)
                {
                    continue;
                }

                Commit? parent = commit.Parents.FirstOrDefault();
                if (parent?[unitPath] is not null)
                {
                    return (commit.Sha[..Math.Min(7, commit.Sha.Length)], commit.Committer.When);
                }
            }
        }
        catch (LibGit2SharpException)
        {
            // N-2: no localizarlo se DICE; la acción sigue siendo posible, sin evidencia de commit.
        }

        return (null, null);
    }

    // ------------------------------------------------------------------ auxiliares

    private static IEnumerable<UnitDrift> Unavailable(IReadOnlyList<InventoryUnit> units, string note)
        => units.Select(u => new UnitDrift(u.Path, DriftState.HistorialNoDisponible, Note: note));

    private static string Normalize(string path) => CodeAnchor.NormalizePath(path);

    /// <summary>Acepta el sha abreviado que guardan las sesiones (7 caracteres) y el completo.</summary>
    private static Commit? TryLookup(Repository repo, string sha)
    {
        try
        {
            return repo.Lookup<Commit>(sha);
        }
        catch (Exception)
        {
            // Un sha con forma inválida —«unknown», un resto de una versión antigua— no es un error
            // del programa: es un dato que no sirve, y el estado honesto ya está decidido arriba.
            return null;
        }
    }

    /// <summary>
    /// Si la rama actual es la por defecto del repositorio. Se pregunta a <c>origin/HEAD</c>, que es
    /// quien lo sabe; sin remoto se acepta la convención, y en la duda se dice que sí para no
    /// llenar de avisos a quien trabaja en un repositorio local.
    /// </summary>
    private static bool IsDefaultBranch(Repository repo, string branch)
    {
        try
        {
            if (repo.Refs["refs/remotes/origin/HEAD"] is SymbolicReference symbolic)
            {
                string target = symbolic.Target?.CanonicalName ?? symbolic.TargetIdentifier ?? string.Empty;
                const string prefix = "refs/remotes/origin/";
                if (target.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return string.Equals(target[prefix.Length..], branch, StringComparison.Ordinal);
                }
            }
        }
        catch (LibGit2SharpException)
        {
            // Sin referencia legible se cae a la convención de abajo.
        }

        return branch is "main" or "master" or "(no branch)";
    }
}
