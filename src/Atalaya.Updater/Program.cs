using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Atalaya.Updater;

/// <summary>
/// El relevo (F11). Espera a que Atalaya cierre, sustituye la carpeta y la vuelve a lanzar.
/// <para>
/// Enseña una consola a propósito. Sustituir una carpeta de 460 MB tarda un momento y en ese
/// momento la aplicación no está: sin ventana, el usuario ve Atalaya desaparecer y no vuelve
/// nada durante unos segundos, que es indistinguible de un cuelgue. Con ventana, ve qué pasa.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Actualizando Atalaya";

        Options? options = Options.Parse(args, out string error);
        if (options is null)
        {
            Console.Error.WriteLine($"Argumentos: {error}");
            return 2;
        }

        Say("Actualizando Atalaya");
        Say($"  de la {options.FromVersion} a la {options.ToVersion}");
        Say(string.Empty);

        SwapResult result = Run(options);

        WriteResult(options, result);

        Say(string.Empty);
        Say(result.Message);
        if (result.Detail.Length > 0)
        {
            Say($"  ({result.Detail})");
        }

        // Se relanza en los dos finales en los que hay algo que relanzar: con la versión nueva si
        // el cambio salió, y con la de antes si hubo que deshacerlo. Quedarse sin aplicación
        // porque la actualización falló sería el peor resultado posible.
        if (result.Outcome != SwapOutcome.Intacta)
        {
            Relaunch(options);
        }
        else
        {
            Say("Atalaya no se ha modificado. Ábrela como siempre.");
            Pause();
        }

        return result.Ok ? 0 : 1;
    }

    private static SwapResult Run(Options options)
    {
        // ---- Esperar. Mientras el proceso viva, sus ficheros están bloqueados y no hay nada
        //      que hacer salvo esperar; forzar el cierre sería tirar el trabajo de alguien.
        Say("Esperando a que Atalaya termine de cerrarse…");
        if (!WaitForExit(options.Pid, TimeSpan.FromSeconds(90)))
        {
            return new SwapResult(SwapOutcome.Intacta,
                "Atalaya sigue abierta pasado el tiempo de espera; no se ha modificado nada. "
                + "Ciérrala y vuelve a intentarlo.",
                $"el proceso {options.Pid} no terminó");
        }

        // Windows suelta los ficheros un instante DESPUÉS de que el proceso muera. Sin esta
        // espera corta, el primer renombrado falla por «en uso» en una máquina lenta.
        if (!WaitUnlocked(Path.Combine(options.AppDir, options.MainExe), TimeSpan.FromSeconds(20)))
        {
            string message = "El ejecutable de Atalaya sigue bloqueado por otro programa "
                             + "(¿un antivirus, un cliente de sincronización?); no se ha modificado nada.";
            return new SwapResult(SwapOutcome.Intacta,
                options.SyncNote.Length == 0 ? message : $"{message} {options.SyncNote}",
                $"{options.MainExe} bloqueado tras cerrar el proceso");
        }

        Say("Sustituyendo la carpeta… no cierres esta ventana.");
        return new FolderSwap().Apply(new SwapPlan
        {
            AppDir = options.AppDir,
            StagedDir = options.StagedDir,
            BackupDir = options.BackupDir,
            MainExe = options.MainExe,
            SyncedAdvice = options.SyncNote,
        });
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true; // Ya no existe: es exactamente lo que esperábamos.
        }

        using (process)
        {
            try
            {
                return process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>¿Se puede ya escribir en el fichero? Es la señal de que el sistema lo soltó.</summary>
    private static bool WaitUnlocked(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!File.Exists(path))
            {
                return true;
            }

            try
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false; // No es un bloqueo temporal: no hay permiso, y esperar no lo arregla.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(400);
        }
    }

    private static void Relaunch(Options options)
    {
        string exe = Path.Combine(options.AppDir, options.MainExe);
        Say("Volviendo a abrir Atalaya…");
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = options.AppDir,
            });
        }
        catch (Exception ex)
        {
            Say($"No se ha podido abrir Atalaya sola: ábrela desde {exe}.");
            Say($"  ({ex.GetType().Name}: {ex.Message})");
            Pause();
        }
    }

    /// <summary>
    /// El parte, para que la versión que arranque pueda contar qué pasó. Se escribe a mano y no
    /// con un serializador para que el relevo no dependa de nada recortable: si esto fallara, la
    /// actualización quedaría hecha pero muda.
    /// </summary>
    private static void WriteResult(Options options, SwapResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.ResultPath)!);
            var json = new StringBuilder();
            json.Append("{\n");
            Field(json, "fromVersion", options.FromVersion, comma: true);
            Field(json, "toVersion", options.ToVersion, comma: true);
            Field(json, "outcome", result.Outcome.ToString(), comma: true);
            Field(json, "message", result.Message, comma: true);
            Field(json, "detail", result.Detail, comma: true);
            Field(json, "appDir", options.AppDir, comma: true);
            // La copia que DE VERDAD se usó, que puede no llamarse como la pedida: la nueva
            // versión borrará ésta al arrancar, y borrar otra sería peor que no borrar ninguna.
            Field(json, "backupDir", result.Outcome == SwapOutcome.Actualizada ? result.BackupDir : "", comma: true);
            // Y lo que quedó sin poder borrarse, para que la nueva versión lo reintente en cada
            // arranque en vez de olvidarlo — que es lo que dejó el residuo que trajo aquí.
            ArrayField(json, "orphanBackups", result.Orphans, comma: true);
            Field(json, "whenUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), comma: false);
            json.Append("}\n");
            File.WriteAllText(options.ResultPath, json.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Say($"(no se pudo dejar el parte de la actualización: {ex.Message})");
        }
    }

    private static void Field(StringBuilder json, string name, string value, bool comma)
        => json.Append("  \"").Append(name).Append("\": \"").Append(Escape(value)).Append('"')
               .Append(comma ? ",\n" : "\n");

    private static void ArrayField(StringBuilder json, string name, IReadOnlyList<string> values, bool comma)
    {
        json.Append("  \"").Append(name).Append("\": [");
        for (int i = 0; i < values.Count; i++)
        {
            json.Append(i == 0 ? string.Empty : ", ").Append('"').Append(Escape(values[i])).Append('"');
        }

        json.Append(']').Append(comma ? ",\n" : "\n");
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static void Say(string text) => Console.WriteLine(text);

    private static void Pause()
    {
        Say(string.Empty);
        Say("Pulsa una tecla para cerrar esta ventana.");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // Sin consola interactiva (lanzado desde un script): no hay a quién esperar.
        }
    }

    /// <summary>Lo que hace falta saber, y nada opcional: un relevo a medio configurar no arranca.</summary>
    internal sealed class Options
    {
        public required int Pid { get; init; }

        public required string AppDir { get; init; }

        public required string StagedDir { get; init; }

        public required string BackupDir { get; init; }

        public required string ResultPath { get; init; }

        public string MainExe { get; init; } = "Atalaya.exe";

        public string FromVersion { get; init; } = "?";

        public string ToVersion { get; init; } = "?";

        /// <summary>
        /// La receta para un fallo de bloqueo, cuando la instalación cuelga de una carpeta
        /// sincronizada. Opcional: quien detecta OneDrive es la aplicación, y una versión vieja
        /// que lance a este relevo sin esta opción sigue funcionando — solo dará el mensaje seco.
        /// </summary>
        public string SyncNote { get; init; } = string.Empty;

        public static Options? Parse(string[] args, out string error)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    error = $"«{args[i]}» no es una opción con valor.";
                    return null;
                }

                map[args[i][2..]] = args[++i];
            }

            foreach (string required in new[] { "pid", "app-dir", "staged", "backup", "result" })
            {
                if (!map.ContainsKey(required))
                {
                    error = $"falta --{required}.";
                    return null;
                }
            }

            if (!int.TryParse(map["pid"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                error = "--pid no es un número.";
                return null;
            }

            error = string.Empty;
            return new Options
            {
                Pid = pid,
                AppDir = map["app-dir"],
                StagedDir = map["staged"],
                BackupDir = map["backup"],
                ResultPath = map["result"],
                MainExe = map.TryGetValue("exe", out string? exe) ? exe : "Atalaya.exe",
                FromVersion = map.TryGetValue("from", out string? from) ? from : "?",
                ToVersion = map.TryGetValue("to", out string? to) ? to : "?",
                SyncNote = map.TryGetValue("sync-note", out string? note) ? note : string.Empty,
            };
        }
    }
}
