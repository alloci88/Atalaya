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

/// <summary>
/// Compila y pasa los tests del clon <b>por encargo del agente</b> (F6.9 §3).
/// <para>
/// <b>El agente pide; la aplicación ejecuta.</b> Es la línea que separa este flujo de darle una
/// shell: <c>run_build_and_tests</c> no lleva argumentos, no acepta un comando y no puede apuntar
/// a otro sitio. Lo que se ejecuta lo decide Atalaya —<c>dotnet build</c> y <c>dotnet test</c>
/// sobre la solución del clon—, con tope de tiempo y con la salida recortada antes de volver.
/// </para>
/// <para>
/// <b>Y no poder compilar es un RESULTADO, no una excepción.</b> Un clon sin solución SDK-style
/// (mucho .NET Framework por ahí) o una máquina sin <c>dotnet</c> devuelven un resumen que lo dice
/// con todas las letras. Un agente que lee «no hay forma de compilar esto desde aquí» declara el
/// riesgo; uno que ve reventar la tool se queda mudo.
/// </para>
/// </summary>
public sealed class BuildRunner
{
    /// <summary>Cuánto se espera como mucho a cada uno de los dos comandos.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Líneas que se conservan de cada extremo de la salida.</summary>
    public const int HeadLines = 40;

    /// <summary>Igual por el final, que es donde están los errores y el recuento de tests.</summary>
    public const int TailLines = 60;

    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public BuildRunner(IProcessRunner? runner = null, TimeSpan? timeout = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _timeout = timeout is { TotalSeconds: > 0 } ? timeout.Value : DefaultTimeout;
    }

    /// <summary>Qué se compila: la solución del clon, o el motivo por el que no hay nada que compilar.</summary>
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

    public Atalaya.Copilot.BuildAndTestResult Run(string cloneRoot, CancellationToken ct)
    {
        string? solution = FindSolution(cloneRoot);
        if (solution is null)
        {
            return new Atalaya.Copilot.BuildAndTestResult(
                false,
                "No se encontró ninguna solución (.sln) en el clon, así que Atalaya no puede "
                + "compilarlo por ti. Declara en tu resumen que el cambio NO se ha compilado.");
        }

        string relative = Relative(cloneRoot, solution);
        var report = new StringBuilder();
        report.AppendLine($"Solución: {relative}");

        ProcessOutcome build = _runner.Run(
            "dotnet", $"build \"{solution}\" --nologo -v minimal", cloneRoot, _timeout, ct);
        if (build.TimedOut)
        {
            report.AppendLine($"BUILD: agotó el tiempo ({_timeout.TotalMinutes:0} min) y se abortó.");
            report.Append(Truncate(build.Output));
            return new Atalaya.Copilot.BuildAndTestResult(false, report.ToString(), TimedOut: true);
        }

        report.AppendLine($"BUILD: {(build.ExitCode == 0 ? "OK" : $"FALLÓ (código {build.ExitCode})")}");
        report.AppendLine(Truncate(build.Output));

        if (build.ExitCode != 0)
        {
            // Sin build no hay tests: ejecutarlos igualmente solo añade ruido a un resumen que ya
            // dice lo único que importa — que no compila.
            report.AppendLine("TESTS: no se ejecutaron porque la compilación falló.");
            return new Atalaya.Copilot.BuildAndTestResult(false, report.ToString());
        }

        ProcessOutcome test = _runner.Run(
            "dotnet", $"test \"{solution}\" --nologo -v minimal --no-build", cloneRoot, _timeout, ct);
        if (test.TimedOut)
        {
            report.AppendLine($"TESTS: agotaron el tiempo ({_timeout.TotalMinutes:0} min) y se abortaron.");
            report.Append(Truncate(test.Output));
            return new Atalaya.Copilot.BuildAndTestResult(false, report.ToString(), TimedOut: true);
        }

        report.AppendLine($"TESTS: {(test.ExitCode == 0 ? "OK" : $"FALLARON (código {test.ExitCode})")}");
        report.AppendLine(Truncate(test.Output));

        return new Atalaya.Copilot.BuildAndTestResult(test.ExitCode == 0, report.ToString());
    }

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

    private static string Relative(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch
        {
            return file;
        }
    }
}
