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

    /// <summary>Tope de lo que se le devuelve de un fichero. Un fichero enorme no cabe en un turno.</summary>
    public const int MaxFileChars = 120_000;

    private readonly string _cloneRoot;
    private readonly HashSet<string> _inScope;
    private readonly FixSnapshotStore _snapshots;
    private readonly FixSnapshotSet _set;
    private readonly IFixApprovals _approvals;
    private readonly FixPauseGate _pause;
    private readonly BuildRunner _builds;
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
        int readBudget = DefaultReadBudget)
    {
        _cloneRoot = Path.GetFullPath(cloneRoot);
        _inScope = new HashSet<string>(
            inScopePaths.Select(FixSnapshotStore.Normalize), StringComparer.OrdinalIgnoreCase);
        _snapshots = snapshots;
        _set = set;
        _approvals = approvals;
        _pause = pause;
        _builds = builds;
        _ct = ct;
        _readsLeft = Math.Max(1, readBudget);
    }

    /// <summary>Se ha leído un fichero. La vista lo narra.</summary>
    public event Action<string, bool>? FileRead;

    /// <summary>Se ha aplicado una edición. Lleva las dos versiones para el diff.</summary>
    public event Action<FixEditApplied>? Edited;

    /// <summary>El agente intentó tocar un fichero fuera de ámbito y el usuario dijo que no.</summary>
    public event Action<string, string>? EditDenied;

    /// <summary>Empieza una compilación por encargo. La vista enseña que está corriendo.</summary>
    public event Action? BuildStarted;

    public event Action<BuildAndTestResult>? BuildFinished;

    /// <summary>El agente cerró con <c>fix_done</c>.</summary>
    public event Action<FixDoneArgs>? Done;

    /// <summary>Ya se ha cerrado: la conversación no necesita otro turno.</summary>
    public bool IsDone { get; private set; }

    /// <summary>Los ficheros tocados, en orden de primera edición.</summary>
    public IReadOnlyList<string> Touched => _set.Files;

    // ------------------------------------------------------------------ read_file

    public ReadFileResult ReadFile(string path)
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
            FileRead?.Invoke(relative, false);
            return new ReadFileResult(false, Error: $"{relative} no existe en el clon.", Remaining: _readsLeft);
        }

        try
        {
            string text = File.ReadAllText(full);
            bool trimmed = text.Length > MaxFileChars;
            if (trimmed)
            {
                text = text[..MaxFileChars]
                    + $"\n\n… [recortado: el fichero tiene {text.Length} caracteres] …";
            }

            FileRead?.Invoke(relative, true);
            return new ReadFileResult(true, text, Remaining: _readsLeft);
        }
        catch (Exception ex)
        {
            return new ReadFileResult(false, Error: $"No se pudo leer {relative}: {ex.Message}",
                Remaining: _readsLeft);
        }
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
            File.WriteAllText(full, next);
        }
        catch (Exception ex)
        {
            return new ApplyEditResult(false, $"No se pudo escribir {relative}: {ex.Message}");
        }

        Edited?.Invoke(new FixEditApplied(relative, before, next, reason ?? string.Empty, inScope));
        return new ApplyEditResult(true, Changed: changed);
    }

    // ------------------------------------------------------------------ run_build_and_tests

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
        BuildAndTestResult result;
        try
        {
            result = _builds.Run(_cloneRoot, _ct);
        }
        catch (OperationCanceledException)
        {
            result = new BuildAndTestResult(false, "La compilación se canceló al detener la sesión.");
        }
        catch (Exception ex)
        {
            result = new BuildAndTestResult(false, $"No se pudo compilar: {ex.Message}");
        }

        BuildFinished?.Invoke(result);
        return result;
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
