using System.Text;
using Atalaya.Copilot;

namespace Atalaya.App.Services;

/// <summary>Una edición ya aplicada, con las dos versiones del fichero: es lo que pinta el diff.</summary>
/// <param name="RelativePath">Ruta relativa al clon, con barras normales.</param>
/// <param name="InScope">
/// El fichero era del hallazgo (o su test). Los que no lo son han pasado por una autorización del
/// usuario, y la vista lo dice.
/// </param>
public sealed record FixEditApplied(
    string RelativePath, string? Before, string After, string Reason, bool InScope);

/// <summary>Quién autoriza tocar un fichero que NO es del hallazgo (F6.9 §3).</summary>
public interface IFixApprovals
{
    /// <summary>
    /// Pregunta al usuario si el agente puede modificar <paramref name="relativePath"/>.
    /// Devolver <c>false</c> es una decisión, no un error: el agente tiene que replantear.
    /// </summary>
    Task<bool> ApproveFileAsync(string relativePath, string reason, CancellationToken ct);
}

/// <summary>
/// Pausa cooperativa de una sesión de arreglo (F6.9 §4).
/// <para>
/// <b>Qué significa «Pausar» aquí, exactamente.</b> El SDK no sabe congelar a un modelo a mitad de
/// razonamiento, así que prometerlo sería mentir. Lo que esto pausa es lo único que importa: que
/// NO caiga ni un cambio más en el clon ni se lance una compilación mientras el usuario está
/// leyendo. El agente puede seguir pensando; su siguiente <c>apply_edit</c> se queda esperando en
/// la puerta hasta que se continúe o se detenga.
/// </para>
/// </summary>
public sealed class FixPauseGate
{
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public bool IsPaused => !_open.IsSet;

    public void Pause() => _open.Reset();

    public void Resume() => _open.Set();

    /// <summary>Espera a que se reanude. Detener cancela y la espera termina con excepción.</summary>
    public void Wait(CancellationToken ct) => _open.Wait(ct);
}

/// <summary>
/// Las cuatro herramientas de una sesión de arreglo, con sus límites puestos (F6.9 §3).
/// <para>
/// <b>El agente nunca toca el disco directamente.</b> Todo pasa por aquí: la lectura tiene
/// presupuesto y no sale del clon, la escritura solo existe en <c>apply_edit</c> —que copia el
/// contenido previo ANTES de escribir y pide permiso fuera del ámbito del hallazgo—, y compilar es
/// una petición que ejecuta la aplicación. El <c>OnPermissionRequest</c> de la sesión rechaza
/// cualquier otra cosa (shell, git, red), igual que en una auditoría.
/// </para>
/// </summary>
public sealed class FixToolbox : IFixToolbox
{
    /// <summary>Cuántos ficheros puede leer el agente. Explorar no es arreglar (F6.8, D-526).</summary>
    public const int DefaultReadBudget = 30;

    /// <summary>
    /// Tope de lo que se le devuelve de un fichero EN UNA LECTURA. Un fichero enorme no cabe en un
    /// turno — pero cabe en varios: desde BUGFIX-LECTURA lo que no entra no se pierde, se pide por
    /// rango, y la respuesta dice cuántas líneas quedan. Subir el tope solo movería el problema al
    /// siguiente fichero.
    /// </summary>
    public const int MaxFileChars = 120_000;

    private readonly string _cloneRoot;
    private readonly HashSet<string> _inScope;
    private readonly FixSnapshotStore _snapshots;
    private readonly FixSnapshotSet _set;
    private readonly IFixApprovals _approvals;
    private readonly FixPauseGate _pause;
    private readonly BuildRunner _builds;
    private readonly Func<bool> _fullSolution;
    private readonly string? _commit;
    private readonly CancellationToken _ct;
    private readonly object _gate = new();

    private int _readsLeft;

    public FixToolbox(
        string cloneRoot,
        IEnumerable<string> inScopePaths,
        FixSnapshotStore snapshots,
        FixSnapshotSet set,
        IFixApprovals approvals,
        FixPauseGate pause,
        BuildRunner builds,
        CancellationToken ct,
        int readBudget = DefaultReadBudget,
        Func<bool>? fullSolution = null)
    {
        _cloneRoot = Path.GetFullPath(cloneRoot);
        _inScope = new HashSet<string>(
            inScopePaths.Select(FixSnapshotStore.Normalize), StringComparer.OrdinalIgnoreCase);
        _snapshots = snapshots;
        _set = set;
        _approvals = approvals;
        _pause = pause;
        _builds = builds;
        // Quién decide el ámbito de la compilación es el USUARIO, no el agente (H9.1 §2): esto se
        // lee en cada petición porque el interruptor de la vista puede cambiar a mitad de sesión.
        _fullSolution = fullSolution ?? (() => false);
        // El commit del clon al empezar. Con el árbol limpio como precondición, es la otra mitad
        // de la clave de la línea base: lo que la solución hacía ANTES de tocar nada.
        string head = GitInfo.HeadSha(_cloneRoot);
        // «unknown» no es un commit: usarlo como clave mezclaría clones distintos en la misma
        // línea base. Sin commit no hay caché, que es lento pero nunca miente.
        _commit = head is { Length: > 0 } && head != "unknown" ? head : null;
        _ct = ct;
        _readsLeft = Math.Max(1, readBudget);
    }

    /// <summary>
    /// Se ha leído un fichero. La vista lo narra. El tercer argumento es el rango, cuando lo hubo:
    /// seis líneas «Ha leído FormOptions.Designer.cs» seguidas parecen un bucle, y son el fichero
    /// entero leído por trozos (BUGFIX-LECTURA).
    /// </summary>
    public event Action<string, bool, string>? FileRead;

    /// <summary>Se ha aplicado una edición. Lleva las dos versiones para el diff.</summary>
    public event Action<FixEditApplied>? Edited;

    /// <summary>El agente intentó tocar un fichero fuera de ámbito y el usuario dijo que no.</summary>
    public event Action<string, string>? EditDenied;

    /// <summary>Empieza una compilación por encargo. La vista enseña que está corriendo.</summary>
    public event Action? BuildStarted;

    /// <summary>
    /// El veredicto de la compilación, ya atribuido: cuántos errores son NUEVOS y cuántos ya
    /// estaban. La vista y el informe leen de aquí; el agente recibe el mismo texto.
    /// </summary>
    public event Action<BuildVerdict>? BuildFinished;

    /// <summary>El agente cerró con <c>fix_done</c>.</summary>
    public event Action<FixDoneArgs>? Done;

    /// <summary>Ya se ha cerrado: la conversación no necesita otro turno.</summary>
    public bool IsDone { get; private set; }

    /// <summary>Los ficheros tocados, en orden de primera edición.</summary>
    public IReadOnlyList<string> Touched => _set.Files;

    // ------------------------------------------------------------------ read_file

    public ReadFileResult ReadFile(string path, int? startLine = null, int? endLine = null)
    {
        if (!TryResolve(path, out string relative, out string full, out string? error))
        {
            return new ReadFileResult(false, Error: error, Remaining: _readsLeft);
        }

        lock (_gate)
        {
            if (_readsLeft <= 0)
            {
                return new ReadFileResult(false,
                    Error: "Presupuesto de lecturas agotado. Arregla con lo que ya sabes, o "
                        + "declara en tu resumen qué te faltó por mirar.",
                    Remaining: 0);
            }

            _readsLeft--;
        }

        if (!File.Exists(full))
        {
            FileRead?.Invoke(relative, false, string.Empty);
            return new ReadFileResult(false, Error: $"{relative} no existe en el clon.", Remaining: _readsLeft);
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            return new ReadFileResult(false, Error: $"No se pudo leer {relative}: {ex.Message}",
                Remaining: _readsLeft);
        }

        return Slice(relative, text, startLine, endLine);
    }

    /// <summary>
    /// El trozo pedido, y lo que falta DICHO (BUGFIX-LECTURA).
    /// <para>
    /// <b>Se corta por líneas enteras, nunca a mitad.</b> Lo que había antes se llevaba los
    /// primeros 120.000 caracteres y ahí acababa —a media línea, con una coletilla que decía
    /// cuántos caracteres tenía el fichero y ninguna forma de pedir el resto—. Cortar por línea es
    /// lo que hace que los trozos se puedan volver a pegar: la concatenación de los rangos
    /// consecutivos es el fichero, carácter por carácter, que es el mismo criterio con el que se
    /// restauran los snapshots (D-560).
    /// </para>
    /// </summary>
    private ReadFileResult Slice(string relative, string text, int? startLine, int? endLine)
    {
        // El comienzo de cada línea, y el final del texto como centinela: con esto un rango es una
        // resta de índices y no hay que recomponer separadores —que es donde se pierde el byte a
        // byte cuando un fichero mezcla \r\n y \n—.
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i + 1 < text.Length)
            {
                starts.Add(i + 1);
            }
        }

        int total = starts.Count;
        starts.Add(text.Length);

        int first = Math.Max(1, startLine ?? 1);
        if (first > total)
        {
            return new ReadFileResult(false,
                Error: $"{relative} tiene {total} líneas y has pedido desde la {first}. "
                    + "Pide un startLine dentro del fichero.",
                Remaining: _readsLeft, TotalLines: total);
        }

        int last = Math.Min(total, endLine is > 0 ? endLine.Value : total);
        if (last < first)
        {
            return new ReadFileResult(false,
                Error: $"endLine ({endLine}) es anterior a startLine ({first}). Manda el rango al derecho.",
                Remaining: _readsLeft, TotalLines: total);
        }

        int from = starts[first - 1];
        string content = text[from..starts[last]];
        string? notice = null;

        if (content.Length > MaxFileChars)
        {
            // Cuántas líneas ENTERAS caben en el tope. Si no cabe ni una, el fichero tiene una
            // línea más larga que el tope y se dice con esas palabras en vez de fingir un rango.
            int fits = last;
            while (fits > first && starts[fits] - from > MaxFileChars)
            {
                fits--;
            }

            if (fits == first && starts[fits] - from > MaxFileChars)
            {
                content = content[..MaxFileChars];
                return new ReadFileResult(true, content, Remaining: _readsLeft,
                    TotalLines: total, FirstLine: first, LastLine: first,
                    Notice: $"fichero de {total} líneas; la línea {first} tiene "
                        + $"{starts[first] - from} caracteres y no cabe entera: van sus primeros "
                        + $"{MaxFileChars}. El resto de esa línea no se puede pedir por rango.");
            }

            last = fits;
            content = text[from..starts[last]];
        }

        if (last < total)
        {
            notice = $"fichero de {total} líneas; devueltas {first}–{last}; pide el resto con "
                + $"read_file(path, startLine, endLine) — el siguiente trozo empieza en "
                + $"startLine {last + 1}.";
        }

        FileRead?.Invoke(relative, true, first == 1 && last == total ? string.Empty : $"líneas {first}–{last} de {total}");
        return new ReadFileResult(true, content, Remaining: _readsLeft,
            TotalLines: total, FirstLine: first, LastLine: last, Notice: notice);
    }

    // ------------------------------------------------------------------ apply_edit

    public ApplyEditResult ApplyEdit(string path, string reason, FixEdit[] edits)
    {
        try
        {
            _pause.Wait(_ct);
        }
        catch (OperationCanceledException)
        {
            return new ApplyEditResult(false, "La sesión se ha detenido.");
        }

        if (!TryResolve(path, out string relative, out string full, out string? error))
        {
            return new ApplyEditResult(false, error);
        }

        if (edits is null || edits.Length == 0)
        {
            return new ApplyEditResult(false, "No has enviado ninguna edición.");
        }

        bool inScope = _inScope.Contains(relative) || IsTestOf(relative);
        if (!inScope)
        {
            // Fichero fuera del hallazgo: lo autoriza el usuario, fichero a fichero. La pregunta
            // lleva el porqué del agente, que es lo único con lo que se puede decidir.
            bool approved;
            try
            {
                approved = _approvals
                    .ApproveFileAsync(relative, string.IsNullOrWhiteSpace(reason) ? "no lo ha dicho" : reason, _ct)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return new ApplyEditResult(false, "La sesión se ha detenido.");
            }

            if (!approved)
            {
                EditDenied?.Invoke(relative, reason ?? string.Empty);
                return new ApplyEditResult(false,
                    $"El usuario NO autoriza modificar {relative}. No vuelvas a pedirlo: replantea "
                    + "el arreglo sin tocar ese fichero, o declara que no se puede hacer sin él.",
                    Denied: true);
            }

            // Autorizado: a partir de aquí es ámbito de esta sesión y no se vuelve a preguntar por
            // él. Preguntar dos veces por el mismo fichero convierte el permiso en un peaje.
            _inScope.Add(relative);
            inScope = false;   // se registra como «fuera de ámbito, autorizado»
        }

        string? before;
        lock (_gate)
        {
            before = _snapshots.Capture(_set, relative);
        }

        // Lo que se EDITA es el fichero de AHORA; lo que se guarda en `before` es el estado
        // previo a la sesión, que es lo que necesita el diff y el descarte. Confundirlos hacía que
        // la segunda edición del mismo fichero se aplicara sobre el texto original y fallara al no
        // encontrar lo que la primera acababa de escribir.
        string current = File.Exists(full) ? SafeRead(full) : string.Empty;
        string next = current;
        int changed = 0;

        foreach (FixEdit edit in edits)
        {
            if (string.IsNullOrEmpty(edit.OldText))
            {
                // Crear o reemplazar entero. Solo se permite cuando el fichero no existía: pisar
                // un fichero entero «sin querer» es el accidente más caro que puede haber aquí.
                if (before is not null || next.Length > 0)
                {
                    return new ApplyEditResult(false,
                        $"{relative} ya existe: oldText vacío solo vale para crear un fichero nuevo. "
                        + "Manda el fragmento exacto que quieres sustituir.");
                }

                next = edit.NewText ?? string.Empty;
                changed++;
                continue;
            }

            int occurrences = Count(next, edit.OldText);
            if (occurrences == 0)
            {
                return new ApplyEditResult(false,
                    $"No se encontró el fragmento en {relative}. Léelo otra vez y manda el texto "
                    + "EXACTO, con su indentación.");
            }

            if (occurrences > 1 && !edit.ReplaceAll)
            {
                return new ApplyEditResult(false,
                    $"El fragmento aparece {occurrences} veces en {relative}. Amplía el contexto "
                    + "para que sea único, o marca replaceAll si de verdad quieres cambiarlas todas.");
            }

            next = edit.ReplaceAll
                ? next.Replace(edit.OldText, edit.NewText ?? string.Empty, StringComparison.Ordinal)
                : ReplaceFirst(next, edit.OldText, edit.NewText ?? string.Empty);
            changed += occurrences > 1 && edit.ReplaceAll ? occurrences : 1;
        }

        if (string.Equals(next, current, StringComparison.Ordinal))
        {
            return new ApplyEditResult(false,
                $"La edición dejaría {relative} exactamente como está. Revisa qué querías cambiar.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // BUGFIX-F32-2 — SE ESCRIBE CON LA CODIFICACIÓN QUE EL FICHERO YA TENÍA. Un
            // `File.WriteAllText(full, next)` a secas escribe UTF-8 SIN marca de orden, así que
            // cada edición de un fichero con BOM se llevaba sus tres bytes por delante y el diff
            // enseñaba la línea 1 quitada y puesta, idéntica. Medido en el clon de xblast:
            // `Hull.cs` pasó de 48 a 45 bytes en la cabecera, y el diff mostraba
            // `-M-oM-;M-?#region INFORMATION` / `+#region INFORMATION`.
            File.WriteAllText(full, next, EncodingOf(full));
        }
        catch (Exception ex)
        {
            return new ApplyEditResult(false, $"No se pudo escribir {relative}: {ex.Message}");
        }

        Edited?.Invoke(new FixEditApplied(relative, before, next, reason ?? string.Empty, inScope));
        return new ApplyEditResult(true, Changed: changed);
    }

    // ------------------------------------------------------------------ run_build_and_tests

    /// <summary>
    /// Compila lo que corresponde a lo tocado y devuelve un veredicto ATRIBUIBLE (H9.1 §2).
    /// <para>
    /// El ámbito sale de los ficheros que el agente lleva tocados, no de una decisión suya: el
    /// proyecto de esos ficheros, o la solución entera si el usuario lo ha pedido. Y el árbol
    /// intacto —ninguna edición todavía— es lo que permite medir la línea base contra la que se
    /// restan los errores que ya estaban.
    /// </para>
    /// </summary>
    public BuildAndTestResult RunBuildAndTests()
    {
        try
        {
            _pause.Wait(_ct);
        }
        catch (OperationCanceledException)
        {
            return new BuildAndTestResult(false, "La sesión se ha detenido antes de compilar.");
        }

        BuildStarted?.Invoke();
        BuildVerdict verdict;
        try
        {
            verdict = _builds.Run(
                new BuildRequest(
                    _cloneRoot,
                    _set.Files,
                    _fullSolution(),
                    _commit,
                    PristineTree: _set.Entries.Count == 0),
                _ct);
        }
        catch (OperationCanceledException)
        {
            verdict = new BuildVerdict(false, "La compilación se canceló al detener la sesión.");
        }
        catch (Exception ex)
        {
            verdict = new BuildVerdict(false, $"No se pudo compilar: {ex.Message}");
        }

        BuildFinished?.Invoke(verdict);
        return verdict.ToAgentResult();
    }

    // ------------------------------------------------------------------ fix_done

    public void FixDone(FixDoneArgs done)
    {
        IsDone = true;
        Done?.Invoke(done);
    }

    // ------------------------------------------------------------------ límites

    /// <summary>
    /// Resuelve una ruta del agente contra el clon y se niega a salir de él. La comprobación es
    /// sobre la ruta CANÓNICA, no sobre el texto: <c>..\..\otra-cosa</c> y un enlace simbólico se
    /// ven igual una vez normalizados, y por texto no.
    /// </summary>
    internal bool TryResolve(string? path, out string relative, out string full, out string? error)
    {
        relative = string.Empty;
        full = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Falta la ruta.";
            return false;
        }

        try
        {
            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path!)
                : Path.GetFullPath(Path.Combine(_cloneRoot, path!));

            string root = _cloneRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                error = "Esa ruta está fuera del clon de la aplicación. Solo puedes leer y editar "
                    + "dentro del repositorio que se está arreglando.";
                return false;
            }

            full = candidate;
            relative = FixSnapshotStore.Normalize(Path.GetRelativePath(_cloneRoot, candidate));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Ruta no válida: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Un fichero de test de algo que sí está en el ámbito. Se acepta sin preguntar porque el
    /// encargo PIDE añadir la prueba del defecto: exigir una autorización para escribir el test de
    /// lo que se acaba de arreglar convertiría la regla en un trámite.
    /// </summary>
    internal bool IsTestOf(string relative)
    {
        string name = Path.GetFileNameWithoutExtension(relative);
        string extension = Path.GetExtension(relative);
        if (name.Length == 0)
        {
            return false;
        }

        foreach (string scoped in _inScope)
        {
            string target = Path.GetFileNameWithoutExtension(scoped);
            if (target.Length == 0 || !string.Equals(extension, Path.GetExtension(scoped), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals(target + "Tests", StringComparison.OrdinalIgnoreCase)
                || name.Equals(target + "Test", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Test" + target, StringComparison.OrdinalIgnoreCase)
                || name.Equals(target + "Spec", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <b>La codificación con la que hay que volver a escribir un fichero: la que ya tenía</b>
    /// (BUGFIX-F32-2).
    /// <para>
    /// <b>La regla es conservar, no normalizar.</b> Los bytes que están fuera del fragmento
    /// sustituido tienen que salir iguales — es el mismo criterio byte a byte con el que se
    /// restauran los snapshots (D-560)—, y la marca de orden es uno de ellos. Los fines de línea
    /// ya se conservaban solos: <c>ReadAllText</c> los deja dentro de la cadena y
    /// <c>WriteAllText</c> los devuelve tal cual; lo único que se perdía era el preámbulo.
    /// </para>
    /// <para>
    /// Se mira el preámbulo <b>real</b> y no la extensión: un fichero <b>sin</b> BOM se escribe
    /// sin BOM, y ponérselo «por consistencia» sería el mismo defecto al revés. Un fichero nuevo
    /// —que no existe todavía— sale sin BOM, que es lo que hacía antes y lo correcto.
    /// </para>
    /// <para>
    /// Y se reconocen los cuatro preámbulos de Unicode, no solo el de UTF-8: <c>ReadAllText</c>
    /// ya sabe decodificar un UTF-16, así que si al escribir se le pusiera UTF-8 el fichero se
    /// transcodificaría entero. Un fichero sin preámbulo se lee y se escribe como UTF-8, que es
    /// lo que se venía haciendo.
    /// </para>
    /// </summary>
    internal static Encoding EncodingOf(string full)
    {
        Span<byte> head = stackalloc byte[4];
        int read = 0;
        try
        {
            using FileStream file = File.OpenRead(full);
            read = file.Read(head);
        }
        catch (Exception)
        {
            return NoPreamble;   // No poder mirarlo no puede impedir escribirlo.
        }

        ReadOnlySpan<byte> start = head[..read];

        // El de UTF-32 LE empieza por los mismos dos bytes que el de UTF-16 LE, así que se mira
        // antes: al revés, ningún UTF-32 LE se reconocería nunca.
        if (start.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (start.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (start.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (start.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (start.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        return NoPreamble;
    }

    /// <summary>UTF-8 a secas. Es lo que se le pone a un fichero que no traía preámbulo.</summary>
    private static readonly Encoding NoPreamble =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string SafeRead(string full)
    {
        try
        {
            return File.ReadAllText(full);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? haystack : haystack[..at] + replacement + haystack[(at + needle.Length)..];
    }
}
