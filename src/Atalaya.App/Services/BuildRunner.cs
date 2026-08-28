using System.Diagnostics;
using System.Text;

namespace Atalaya.App.Services;

/// <summary>Lo que devuelve ejecutar un proceso: código, salida ya unida y si se agotó el tiempo.</summary>
public sealed record ProcessOutcome(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// El seam del proceso externo. Existe para que la delegación de compilar sea comprobable sin
/// compilar nada: los tests inyectan un doble y ejercitan el resumen, el truncado y los fallos.
/// </summary>
public interface IProcessRunner
{
    ProcessOutcome Run(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Ejecuta de verdad, capturando salida estándar y de error.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public ProcessOutcome Run(
        string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool finished;
        try
        {
            finished = process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        }
        catch (Exception)
        {
            finished = false;
        }

        if (!finished)
        {
            Kill(process);
            return new ProcessOutcome(-1, output.ToString(), TimedOut: true);
        }

        // WaitForExit(int) no garantiza que los lectores asíncronos hayan drenado; el sin
        // argumentos sí. Sin esto, la salida llega a medias justo en las compilaciones largas.
        process.WaitForExit();
        return new ProcessOutcome(process.ExitCode, output.ToString(), TimedOut: false);
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is not null)
        {
            lock (sb)
            {
                sb.AppendLine(line);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Se ha hecho lo posible: un proceso que no se deja matar no puede tumbar el arreglo.
        }
    }
}

/// <summary>Lo que se le pide a una compilación por encargo (H9.1 §2).</summary>
/// <param name="CloneRoot">El clon donde se compila. Nunca se sale de ahí.</param>
/// <param name="TouchedFiles">Lo que el agente lleva tocado, en rutas relativas. Decide el ámbito.</param>
/// <param name="FullSolution">El usuario ha pedido explícitamente la solución entera.</param>
/// <param name="Commit">Commit del clon: es la mitad de la clave de la línea base.</param>
/// <param name="PristineTree">
/// El árbol sigue como al empezar (ninguna edición aplicada todavía). Solo entonces se puede
/// MEDIR una línea base nueva: después, lo que se compile ya lleva el cambio dentro.
/// </param>
public sealed record BuildRequest(
    string CloneRoot,
    IReadOnlyList<string> TouchedFiles,
    bool FullSolution = false,
    string? Commit = null,
    bool PristineTree = false)
{
    public static BuildRequest ForClone(string cloneRoot) => new(cloneRoot, Array.Empty<string>());
}

/// <summary>
/// El veredicto de una compilación, ya atribuido (H9.1 §2).
/// <para>
/// La diferencia con el resumen de antes está en <see cref="NewErrors"/>: los errores que la
/// compilación de ahora tiene y la línea base NO tenía. Lo demás —lo preexistente y lo que dotnet
/// ni siquiera puede compilar— se nombra aparte, porque no es del cambio y presentarlo como si lo
/// fuera es lo que convertía cada arreglo en un rojo inmerecido.
/// </para>
/// </summary>
public sealed record BuildVerdict(
    bool Ok,
    string Summary,
    bool TimedOut = false,
    string TargetLabel = "",
    int NewErrors = 0,
    int PreexistingErrors = 0,
    IReadOnlyList<string>? NewErrorLines = null,
    IReadOnlyList<string>? PreexistingErrorLines = null,
    IReadOnlyList<string>? ExcludedProjects = null,
    string? BaselineNote = null,
    bool HasBaseline = false,
    bool TestsRun = false,
    bool TestsOk = false)
{
    public IReadOnlyList<string> New => NewErrorLines ?? Array.Empty<string>();

    public IReadOnlyList<string> Preexisting => PreexistingErrorLines ?? Array.Empty<string>();

    public IReadOnlyList<string> Excluded => ExcludedProjects ?? Array.Empty<string>();

    /// <summary>Lo que ve el agente. Mismo texto que el usuario lee en la vista y en el informe.</summary>
    public Atalaya.Copilot.BuildAndTestResult ToAgentResult()
        => new(Ok, Summary, TimedOut);

    /// <summary>«0 errores nuevos · 18 preexistentes». La línea que resume el veredicto.</summary>
    public string Headline
    {
        get
        {
            var parts = new List<string> { $"{NewErrors} error(es) nuevo(s)" };
            if (PreexistingErrors > 0)
            {
                parts.Add($"{PreexistingErrors} preexistente(s)");
            }

            if (Excluded.Count > 0)
            {
                parts.Add($"{Excluded.Count} proyecto(s) fuera del alcance de dotnet");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Compila y pasa los tests del clon <b>por encargo del agente</b> (F6.9 §3, reescrito en H9.1).
/// <para>
/// <b>El agente pide; la aplicación ejecuta.</b> Es la línea que separa este flujo de darle una
/// shell: <c>run_build_and_tests</c> no lleva argumentos, no acepta un comando y no puede apuntar
/// a otro sitio. Lo que se ejecuta lo decide Atalaya, con tope de tiempo y con la salida recortada
/// antes de volver.
/// </para>
/// <para>
/// <b>Y el veredicto tiene que pertenecer al cambio.</b> Se compila el PROYECTO de lo tocado, no
/// la solución entera; y cuando el ámbito es mayor, lo que se reporta es el DELTA contra una línea
/// base medida sobre el mismo commit con el árbol limpio. Los proyectos que <c>dotnet</c> no sabe
/// compilar —C++ y compañía— se nombran, no se cuentan como fallo. Ningún rojo sin causa
/// atribuible.
/// </para>
/// <para>
/// <b>No poder compilar sigue siendo un RESULTADO, no una excepción.</b> Un clon sin nada
/// compilable devuelve un resumen que lo dice con todas las letras: un agente que lee «no hay
/// forma de compilar esto desde aquí» declara el riesgo; uno que ve reventar la tool se queda
/// mudo.
/// </para>
/// </summary>
public sealed class BuildRunner
{
    /// <summary>Cuánto se espera como mucho a cada uno de los comandos.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Líneas que se conservan de cada extremo de la salida.</summary>
    public const int HeadLines = 40;

    /// <summary>Igual por el final, que es donde están los errores y el recuento de tests.</summary>
    public const int TailLines = 60;

    /// <summary>Cuántos errores se listan en el resumen. El resto se cuenta.</summary>
    public const int MaxListedErrors = 12;

    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;
    private readonly BuildBaselineStore? _baselines;

    public BuildRunner(
        IProcessRunner? runner = null, TimeSpan? timeout = null, BuildBaselineStore? baselines = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _timeout = timeout is { TotalSeconds: > 0 } ? timeout.Value : DefaultTimeout;
        _baselines = baselines;
    }

    /// <summary>Qué se compila cuando el ámbito es la solución: la del clon, o nada.</summary>
    public static string? FindSolution(string cloneRoot)
    {
        if (string.IsNullOrWhiteSpace(cloneRoot) || !Directory.Exists(cloneRoot))
        {
            return null;
        }

        // La raíz primero y en orden ordinal: dos ejecuciones tienen que elegir la MISMA solución.
        string? atRoot = Directory.EnumerateFiles(cloneRoot, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        if (atRoot is not null)
        {
            return atRoot;
        }

        return Directory.EnumerateFiles(cloneRoot, "*.sln", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public BuildVerdict Run(BuildRequest request, CancellationToken ct)
    {
        BuildPlan plan = BuildScopeResolver.Resolve(
            request.CloneRoot, request.TouchedFiles, request.FullSolution);

        if (plan.Target.Kind == BuildTargetKind.None)
        {
            return new BuildVerdict(
                false,
                "No hay nada que Atalaya pueda compilar en este clon"
                + (plan.Note is null ? " (no se encontró ninguna solución ni proyecto de .NET)" : $": {plan.Note}")
                + ". Declara en tu resumen que el cambio NO se ha compilado.",
                ExcludedProjects: plan.ExcludedProjects);
        }

        BuildBaseline? baseline = Baseline(request, plan, ct);

        var report = new StringBuilder();
        report.AppendLine($"Ámbito: {plan.Target.Label}"
            + (plan.Note is null ? string.Empty : $" — {plan.Note}"));

        ProcessOutcome build = _runner.Run(
            "dotnet", $"build \"{plan.Target.FullPath}\" --nologo -v minimal", request.CloneRoot, _timeout, ct);

        if (build.TimedOut)
        {
            report.AppendLine($"BUILD: agotó el tiempo ({_timeout.TotalMinutes:0} min) y se abortó.");
            report.Append(Truncate(build.Output));
            return new BuildVerdict(
                false, report.ToString(), TimedOut: true, TargetLabel: plan.Target.Label,
                ExcludedProjects: plan.ExcludedProjects);
        }

        IReadOnlyList<BuildError> errors = BuildErrorParser.Parse(build.Output, request.CloneRoot);
        var foreign = errors.Where(e => e.Foreign).ToList();
        var own = errors.Where(e => !e.Foreign).ToList();

        HashSet<string> known = baseline is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(baseline.Signatures, StringComparer.Ordinal);

        var fresh = own.Where(e => !known.Contains(e.Signature)).ToList();
        var old = own.Where(e => known.Contains(e.Signature)).ToList();

        bool buildOk = fresh.Count == 0 && (build.ExitCode == 0 || old.Count > 0 || foreign.Count > 0);

        AppendVerdict(report, buildOk, fresh, old, foreign, baseline, plan, build);

        if (!buildOk)
        {
            // Sin build no hay tests: ejecutarlos igualmente solo añade ruido a un resumen que ya
            // dice lo único que importa — que el cambio no compila.
            report.AppendLine("TESTS: no se ejecutaron porque la compilación falló por el cambio.");
            return Verdict(false, report, plan, fresh, old, foreign, baseline, testsRun: false, testsOk: false);
        }

        (bool testsRun, bool testsOk, bool timedOut) = RunTests(plan, request, report, ct);
        if (timedOut)
        {
            return new BuildVerdict(
                false, report.ToString(), TimedOut: true, TargetLabel: plan.Target.Label,
                NewErrors: fresh.Count, PreexistingErrors: old.Count,
                ExcludedProjects: plan.ExcludedProjects, HasBaseline: baseline is not null);
        }

        return Verdict(testsOk || !testsRun, report, plan, fresh, old, foreign, baseline, testsRun, testsOk);
    }

    /// <summary>La firma de antes: compila la solución del clon sin nada tocado. La usan los tests.</summary>
    public BuildVerdict Run(string cloneRoot, CancellationToken ct)
        => Run(BuildRequest.ForClone(cloneRoot), ct);

    // ------------------------------------------------------------------ línea base

    /// <summary>
    /// La línea base de este objetivo sobre este commit: de la caché si ya se midió, y si no —y
    /// solo si el árbol sigue limpio— midiéndola ahora. Con el árbol ya tocado no se puede medir
    /// nada: lo que se compilaría llevaría el cambio dentro, que es justo lo que se quiere separar.
    /// </summary>
    private BuildBaseline? Baseline(BuildRequest request, BuildPlan plan, CancellationToken ct)
    {
        if (_baselines is null || !plan.WantsBaseline || string.IsNullOrWhiteSpace(request.Commit))
        {
            return null;
        }

        BuildBaseline? cached = _baselines.TryGet(plan.Target.Relative, request.Commit);
        if (cached is not null || !request.PristineTree)
        {
            return cached;
        }

        ProcessOutcome probe = _runner.Run(
            "dotnet", $"build \"{plan.Target.FullPath}\" --nologo -v minimal", request.CloneRoot, _timeout, ct);
        if (probe.TimedOut)
        {
            return null;
        }

        IReadOnlyList<BuildError> errors = BuildErrorParser.Parse(probe.Output, request.CloneRoot);
        var measured = new BuildBaseline(
            plan.Target.Relative,
            request.Commit!,
            DateTimeOffset.UtcNow,
            probe.ExitCode == 0,
            errors.Where(e => !e.Foreign).Select(e => e.Signature).ToList());
        _baselines.Save(measured);
        return measured;
    }

    // ------------------------------------------------------------------ tests

    private (bool Run, bool Ok, bool TimedOut) RunTests(
        BuildPlan plan, BuildRequest request, StringBuilder report, CancellationToken ct)
    {
        if (plan.Target.Kind == BuildTargetKind.Solution)
        {
            ProcessOutcome all = _runner.Run(
                "dotnet", $"test \"{plan.Target.FullPath}\" --nologo -v minimal --no-build",
                request.CloneRoot, _timeout, ct);
            return Report(all, plan.Target.Relative, report);
        }

        if (plan.TestProjects.Count == 0)
        {
            // H9.1 §3: dato neutro. No hay tests en este proyecto —como en tantos de la casa— y
            // eso no es una carencia del arreglo ni una invitación a ponerse a buscarlos.
            report.AppendLine(
                $"TESTS: no hay proyecto de tests para {plan.Target.Relative}. Es un hecho del "
                + "proyecto, no un resultado del cambio: no los busques.");
            return (false, false, false);
        }

        bool ok = true;
        foreach (string project in plan.TestProjects)
        {
            ProcessOutcome outcome = _runner.Run(
                "dotnet", $"test \"{project}\" --nologo -v minimal --no-build",
                request.CloneRoot, _timeout, ct);
            (bool _, bool thisOk, bool timedOut) = Report(
                outcome, BuildScopeResolver.Relative(request.CloneRoot, project), report);
            if (timedOut)
            {
                return (true, false, true);
            }

            ok &= thisOk;
        }

        return (true, ok, false);
    }

    private (bool Run, bool Ok, bool TimedOut) Report(
        ProcessOutcome outcome, string label, StringBuilder report)
    {
        if (outcome.TimedOut)
        {
            report.AppendLine($"TESTS ({label}): agotaron el tiempo ({_timeout.TotalMinutes:0} min) y se abortaron.");
            report.Append(Truncate(outcome.Output));
            return (true, false, true);
        }

        report.AppendLine($"TESTS ({label}): {(outcome.ExitCode == 0 ? "OK" : $"FALLARON (código {outcome.ExitCode})")}");
        report.AppendLine(Truncate(outcome.Output));
        return (true, outcome.ExitCode == 0, false);
    }

    // ------------------------------------------------------------------ el texto del veredicto

    private static void AppendVerdict(
        StringBuilder report,
        bool ok,
        IReadOnlyList<BuildError> fresh,
        IReadOnlyList<BuildError> old,
        IReadOnlyList<BuildError> foreign,
        BuildBaseline? baseline,
        BuildPlan plan,
        ProcessOutcome build)
    {
        report.AppendLine($"BUILD: {(ok ? "OK" : "FALLÓ")} — {fresh.Count} error(es) NUEVO(S)"
            + (old.Count > 0 ? $", {old.Count} preexistente(s)" : string.Empty)
            + (build.ExitCode == 0 && fresh.Count == 0 && old.Count == 0 ? " (compilación limpia)" : string.Empty));

        if (fresh.Count > 0)
        {
            report.AppendLine("Errores NUEVOS (los ha traído este cambio):");
            foreach (BuildError e in fresh.Take(MaxListedErrors))
            {
                report.AppendLine($"  · {e.Line}");
            }

            if (fresh.Count > MaxListedErrors)
            {
                report.AppendLine($"  … y {fresh.Count - MaxListedErrors} más.");
            }
        }

        if (old.Count > 0)
        {
            report.AppendLine(
                $"Preexistentes ({old.Count}): ya fallaban en {baseline?.Target ?? plan.Target.Relative} "
                + "ANTES del cambio. No son tuyos y no se cuentan en el veredicto.");
            foreach (BuildError e in old.Take(MaxListedErrors))
            {
                report.AppendLine($"  · {e.Line}");
            }

            if (old.Count > MaxListedErrors)
            {
                report.AppendLine($"  … y {old.Count - MaxListedErrors} más.");
            }
        }

        if (foreign.Count > 0 || plan.ExcludedProjects.Count > 0)
        {
            IEnumerable<string> names = plan.ExcludedProjects.Count > 0
                ? plan.ExcludedProjects
                : foreign.Select(f => f.Code);
            report.AppendLine(
                $"Fuera del alcance de esta comprobación: {string.Join(", ", names.Take(MaxListedErrors))} "
                + "requieren el toolset C++ de Visual Studio; dotnet no puede compilarlos y no cuentan "
                + "como fallo.");
        }

        if (baseline is null && old.Count == 0 && build.ExitCode != 0 && fresh.Count > 0)
        {
            report.AppendLine(
                "Sin línea base para este commit: todos los errores se cuentan como nuevos. Si esta "
                + "solución ya fallaba antes, dilo en tu resumen en vez de intentar arreglarla.");
        }

        report.AppendLine(Truncate(build.Output));
    }

    private static BuildVerdict Verdict(
        bool ok,
        StringBuilder report,
        BuildPlan plan,
        IReadOnlyList<BuildError> fresh,
        IReadOnlyList<BuildError> old,
        IReadOnlyList<BuildError> foreign,
        BuildBaseline? baseline,
        bool testsRun,
        bool testsOk)
        => new(
            ok,
            report.ToString(),
            TimedOut: false,
            TargetLabel: plan.Target.Label,
            NewErrors: fresh.Count,
            PreexistingErrors: old.Count,
            NewErrorLines: fresh.Select(e => e.Line).ToList(),
            PreexistingErrorLines: old.Select(e => e.Line).ToList(),
            ExcludedProjects: plan.ExcludedProjects.Count > 0
                ? plan.ExcludedProjects
                : foreign.Select(f => f.Line).Distinct().ToList(),
            BaselineNote: baseline is null
                ? null
                : $"línea base medida el {baseline.CapturedUtc.ToLocalTime():dd/MM/yyyy HH:mm} "
                  + $"sobre el commit {Short(baseline.Commit)}",
            HasBaseline: baseline is not null,
            TestsRun: testsRun,
            TestsOk: testsOk);

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    /// <summary>
    /// Recorta la salida por el medio, conservando la cabeza y —sobre todo— la COLA, que es donde
    /// están los errores y el recuento de tests. Y dice cuántas líneas se ha comido: un resumen
    /// que oculta que oculta algo es peor que uno largo.
    /// </summary>
    public static string Truncate(string? output)
    {
        string[] lines = (output ?? string.Empty)
            .Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToArray();

        if (lines.Length <= HeadLines + TailLines)
        {
            return string.Join(Environment.NewLine, lines);
        }

        var sb = new StringBuilder();
        sb.AppendJoin(Environment.NewLine, lines.Take(HeadLines));
        sb.AppendLine();
        sb.AppendLine($"… [{lines.Length - HeadLines - TailLines} línea(s) omitida(s)] …");
        sb.AppendJoin(Environment.NewLine, lines.Skip(lines.Length - TailLines));
        return sb.ToString();
    }
}
