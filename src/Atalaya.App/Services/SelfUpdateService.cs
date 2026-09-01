using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Atalaya.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.App.Services;

/// <summary>En qué anda la actualización. Es lo que se enseña mientras dura.</summary>
public enum UpdatePhase
{
    Preparando,
    Descargando,
    Verificando,
    Descomprimiendo,
    Sustituyendo,
}

/// <param name="Phase">La fase.</param>
/// <param name="Text">Lo que se lee en pantalla.</param>
/// <param name="Percent">0–100 durante la descarga; null cuando no hay porcentaje que dar.</param>
public sealed record UpdateProgress(UpdatePhase Phase, string Text, double? Percent = null);

/// <summary>
/// ¿Se puede ofrecer «Actualizar»? Y si no, por qué — la razón se enseña, no se calla: un botón
/// que desaparece sin explicación se lee como un fallo.
/// </summary>
/// <param name="CanUpdate">Si el botón se ofrece.</param>
/// <param name="Reason">Por qué no, cuando no. Vacío cuando sí.</param>
/// <param name="Warning">Aviso que acompaña al botón sin impedirlo (un arreglo abierto).</param>
public sealed record UpdateReadiness(bool CanUpdate, string Reason = "", string? Warning = null)
{
    public static readonly UpdateReadiness Ready = new(true);
}

/// <param name="HandedOff">El relevo está en marcha: quien llame debe cerrar la aplicación YA.</param>
/// <param name="Message">Qué contar. En el fallo, la causa concreta y qué hacer.</param>
/// <param name="ReleaseUrl">La página de la Release: el camino manual de siempre cuando falla.</param>
public sealed record UpdateStart(bool HandedOff, string Message, string? ReleaseUrl = null);

/// <summary>Lo que se cuenta al arrancar después de una actualización.</summary>
/// <param name="Ok">Si la instalación acabó con la versión nueva.</param>
/// <param name="Message">La frase para el aviso.</param>
public sealed record UpdateAftermath(bool Ok, string Message);

/// <summary>
/// Actualizar Atalaya desde la propia Atalaya (F11).
/// <para>
/// <b>El orden importa y es el orden de la seguridad</b>: comprobar que se puede escribir →
/// descargar → <b>verificar el SHA-256</b> → descomprimir aparte → y solo entonces ceder el
/// relevo. Nada de la instalación se toca hasta que lo descargado está verificado y
/// descomprimido: una descarga corrupta o cortada se queda en un fichero que se borra, no en una
/// aplicación a medio sustituir.
/// </para>
/// <para>
/// <b>Y actualizar es una decisión humana.</b> Este servicio no se llama solo, ni al arrancar, ni
/// al cerrar, ni en segundo plano. Lo dispara un botón que alguien pulsa.
/// </para>
/// </summary>
public sealed class SelfUpdateService
{
    /// <summary>Lo que el workflow publica junto al zip para poder verificarlo.</summary>
    public const string ChecksumSuffix = ".sha256";

    private readonly AppPaths _paths;
    private readonly DeployConfig _deploy;
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly AgentBusyGate _busy;
    private readonly FixSnapshotStore _fixes;
    private readonly UpdateJournal _journal;
    private readonly ILogger _log;
    private readonly Func<string> _currentVersion;
    private readonly string _appDir;

    /// <summary>
    /// Cómo se lanza el relevo. Se inyecta para que los tests puedan ejercitar la entrega
    /// completa —descarga, verificación, descompresión y traspaso— sin arrancar un proceso de
    /// verdad ni cerrar el que corre los tests.
    /// </summary>
    private readonly Action<ProcessStartInfo> _launch;

    /// <summary>
    /// El nombre del ejecutable de Atalaya. Sale del proceso vivo —que en una instalación de
    /// verdad ES Atalaya.exe— y no de una constante, para que renombrar el ejecutable no deje la
    /// actualización buscando un fichero que ya no se llama así.
    /// </summary>
    private readonly string _mainExe;

    public SelfUpdateService(
        AppPaths paths,
        DeployConfig deploy,
        GitHubAccountService account,
        GitHubApiClient api,
        AgentBusyGate busy,
        FixSnapshotStore fixes,
        UpdateJournal journal,
        ILogger<SelfUpdateService>? log = null,
        Func<string>? currentVersion = null,
        string? appDirectory = null,
        Action<ProcessStartInfo>? launch = null,
        string? mainExeName = null)
    {
        _launch = launch ?? (info => Process.Start(info)?.Dispose());
        _mainExe = mainExeName
                   ?? (Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } exe ? exe : "Atalaya.exe");
        _paths = paths;
        _deploy = deploy;
        _account = account;
        _api = api;
        _busy = busy;
        _fixes = fixes;
        _journal = journal;
        _log = log ?? NullLogger<SelfUpdateService>.Instance;
        _currentVersion = currentVersion ?? AboutInfo.CurrentVersion;
        _appDir = appDirectory ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>El nombre del relevo, tal y como se despliega junto a Atalaya.</summary>
    public const string RunnerExe = "AtalayaUpdater.exe";

    /// <summary>
    /// Las dos carpetas de trabajo, DENTRO de la instalación. Los nombres se deciden aquí y
    /// viajan al relevo como argumentos: el relevo no sabe cómo se llaman ni tiene por qué —
    /// hacerle compartir una constante con la aplicación sería atarle a la versión que le lanza,
    /// y él sobrevive precisamente para cuando esa versión ya no está.
    /// </summary>
    public const string StagedName = ".atalaya-nuevo";

    public const string BackupName = ".atalaya-anterior";

    public string StagedDir => Path.Combine(_appDir, StagedName);

    public string BackupDir => Path.Combine(_appDir, BackupName);

    // ------------------------------------------------------------------ ¿se puede ofrecer?

    /// <summary>
    /// Si el botón «Actualizar a X.Y.Z» aparece, y con qué aviso.
    /// <para>
    /// Se consulta al pintar el aviso Y otra vez al pulsar: entre lo uno y lo otro puede haber
    /// arrancado una auditoría, y lo que decide es el estado del momento en que se va a actuar.
    /// </para>
    /// </summary>
    public UpdateReadiness CanOffer()
    {
        // Un build local no se actualiza: no sabe de qué release viene y su carpeta es un `dist`
        // de trabajo. Es la misma regla que ya aplica «Acerca de» (D-727), dicha una sola vez.
        if (new AboutInfo(null, _currentVersion(), null).IsDevelopmentBuild)
        {
            return new UpdateReadiness(false,
                "Esto es un build local: se actualiza recompilando, no descargando.");
        }

        if (!_deploy.ChecksForUpdates)
        {
            return new UpdateReadiness(false,
                $"Este despliegue no declara «appRepoUrl» en {DeployConfig.FileName}.");
        }

        if (_account.Token is not { Length: > 0 })
        {
            return new UpdateReadiness(false,
                "Conecta tu cuenta de GitHub: el repositorio es privado y hace falta para descargar.");
        }

        // Interrumpir una sesión en marcha tira trabajo YA PAGADO a Copilot. Ninguna comodidad
        // vale eso, así que aquí no se avisa ni se pregunta: no se ofrece.
        if (_busy.IsBusy)
        {
            return new UpdateReadiness(false, _busy.BusyMessage);
        }

        if (!File.Exists(Path.Combine(_appDir, RunnerExe)))
        {
            return new UpdateReadiness(false,
                $"A esta instalación le falta {RunnerExe}, así que no puede sustituirse sola.");
        }

        // Un arreglo abierto NO impide actualizar —sus cambios están en el clon, fuera de la
        // carpeta de la aplicación, y no los toca nadie—, pero callarlo sería dejar que alguien
        // lo descubriera después y se preguntara si se los hemos comido.
        int pending = _fixes.ListPending().Count;
        string? fixes = pending == 0
            ? null
            : pending == 1
                ? "Tienes un arreglo abierto. Sus cambios están en el clon y la actualización no "
                  + "los toca, pero acuérdate de cerrarlo."
                : $"Tienes {pending} arreglos abiertos. Sus cambios están en los clones y la "
                  + "actualización no los toca, pero acuérdate de cerrarlos.";

        // El aviso preventivo (BUGFIX-SYNC): si la instalación cuelga de OneDrive, se dice ANTES
        // de pulsar. No impide nada —la mayoría de los días funciona— pero convierte un fallo
        // futuro en algo que ya se esperaba y con la salida escrita.
        string synced = SyncedFolders.Note(_appDir);

        string warning = string.Join(" ", new[] { fixes, synced.Length == 0 ? null : synced }
            .Where(w => w is { Length: > 0 })!);

        return new UpdateReadiness(true, string.Empty, warning.Length == 0 ? null : warning);
    }

    // ------------------------------------------------------------------ actualizar

    /// <summary>
    /// Descarga la Release del tag, la verifica y cede el relevo. Nunca lanza: cualquier fallo
    /// sale como un <see cref="UpdateStart"/> con su causa y el camino manual.
    /// </summary>
    /// <param name="tag">El tag de la Release que el aviso prometió.</param>
    /// <param name="releaseUrl">Su página, para el camino manual si algo falla.</param>
    public async Task<UpdateStart> UpdateAsync(
        string tag, string? releaseUrl, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        string from = AboutInfo.BaseVersion(_currentVersion());
        string to = tag.TrimStart('v', 'V');

        try
        {
            return await UpdateCoreAsync(tag, releaseUrl, from, to, progress, ct);
        }
        catch (OperationCanceledException)
        {
            Cleanup();
            _journal.Record(new UpdateAttempt(DateTimeOffset.UtcNow, from, to, UpdateOutcome.Abortada,
                "cancelada por el usuario"));
            return new UpdateStart(false, "Actualización cancelada. Nada se ha modificado.", releaseUrl);
        }
        catch (Exception ex)
        {
            Cleanup();
            _log.LogWarning(ex, "La actualización a {Tag} falló antes de tocar la instalación.", tag);
            _journal.Record(new UpdateAttempt(DateTimeOffset.UtcNow, from, to, UpdateOutcome.Abortada,
                $"{ex.GetType().Name}: {ex.Message}"));
            return Failed(Explain(ex), releaseUrl);
        }
    }

    private async Task<UpdateStart> UpdateCoreAsync(
        string tag,
        string? releaseUrl,
        string from,
        string to,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        UpdateReadiness readiness = CanOffer();
        if (!readiness.CanUpdate)
        {
            _journal.Record(new UpdateAttempt(
                DateTimeOffset.UtcNow, from, to, UpdateOutcome.NoOfrecida, readiness.Reason));
            return new UpdateStart(false, readiness.Reason, releaseUrl);
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Preparando, "Comprobando la instalación…"));

        // Lo primero que puede fallar y lo más barato de comprobar: en un equipo corporativo con
        // Atalaya bajo «Archivos de programa» no hay permiso de escritura, y descubrirlo tras
        // bajar 200 MB sería una tomadura de pelo.
        if (NotWritable() is { } permissionProblem)
        {
            _journal.Record(new UpdateAttempt(
                DateTimeOffset.UtcNow, from, to, UpdateOutcome.Abortada, permissionProblem));
            return Failed(
                Prescribe(
                    $"No se puede escribir en la carpeta de Atalaya ({_appDir}). "
                    + "Actualiza a mano descargando el zip, o pide permisos sobre esa carpeta."),
                releaseUrl);
        }

        (string Owner, string Repo) repo = GitHubApiClient.ParseRepositoryUrl(_deploy.AppRepoUrl)
            ?? throw new InvalidOperationException($"appRepoUrl no es un repo de GitHub: {_deploy.AppRepoUrl}");
        string token = _account.Token!;

        // La Release DEL TAG que el aviso prometió, no «la última»: entre el aviso y el clic
        // puede haber salido otra, y bajar algo distinto de lo que decía el botón es una sorpresa.
        GitHubRelease? release = await _api.GetReleaseByTagAsync(token, repo.Owner, repo.Repo, tag, ct);
        if (release is null)
        {
            return Failed($"La Release {tag} ya no está publicada.", releaseUrl);
        }

        GitHubReleaseAsset? zip = release.Find(".zip");
        if (zip is null)
        {
            return Failed($"La Release {tag} no trae paquete zip que instalar.", release.HtmlUrl ?? releaseUrl);
        }

        GitHubReleaseAsset? sum = release.Find(zip.Name + ChecksumSuffix)
                                  ?? release.Find(ChecksumSuffix);
        if (sum is null)
        {
            // Sin checksum no se instala. Una versión publicada antes de F11 no lo trae, y bajarla
            // «confiando» sería justo lo que este trabajo vino a impedir.
            return Failed(
                $"La Release {tag} no publica el checksum del paquete, así que no se puede "
                + "verificar lo descargado. Descárgala a mano si te fías de ella.",
                release.HtmlUrl ?? releaseUrl);
        }

        Directory.CreateDirectory(_paths.Update);
        string zipPath = Path.Combine(_paths.Update, zip.Name);
        TryDeleteFile(zipPath);

        // ---- El checksum primero: son 100 bytes y define contra qué se compara.
        string expected = await ReadChecksumAsync(token, repo, sum, ct);

        // ---- La descarga.
        long total = zip.Size > 0 ? zip.Size : 1;

        // Un punto por CADA PUNTO PORCENTUAL, y no por cada trozo leído. Medido en la prueba real
        // sobre el paquete de 221 MB: sin este freno son 14.021 avisos —uno por cada 80 KB—, cada
        // uno saltando al hilo de la interfaz para mover una barra que no se mueve. Con él son 100.
        int lastPercent = -1;
        var reporter = new Progress<long>(read =>
        {
            int percent = (int)Math.Min(100d, read * 100d / total);
            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;
            progress?.Report(new UpdateProgress(
                UpdatePhase.Descargando,
                $"Descargando {Megabytes(read)} de {Megabytes(total)} MB…",
                percent));
        });

        await _api.DownloadAssetAsync(token, repo.Owner, repo.Repo, zip.Id, zipPath, reporter, ct);

        // ---- La verificación. Aquí es donde se decide si esto sigue.
        progress?.Report(new UpdateProgress(UpdatePhase.Verificando, "Verificando el paquete…"));
        string actual = await Sha256Async(zipPath, ct);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(zipPath);
            _log.LogWarning("Checksum del paquete {Zip}: esperado {Expected}, obtenido {Actual}.",
                zip.Name, expected, actual);
            _journal.Record(new UpdateAttempt(DateTimeOffset.UtcNow, from, to, UpdateOutcome.Abortada,
                $"checksum: esperado {expected}, obtenido {actual}"));
            return Failed(
                "El paquete descargado no coincide con su checksum: la descarga llegó corrupta o "
                + "incompleta. No se ha modificado nada. Reintenta, o descárgala a mano.",
                release.HtmlUrl ?? releaseUrl);
        }

        // ---- Descomprimir APARTE, dentro de la propia carpeta. Dentro y no en %LOCALAPPDATA%
        //      para que la sustitución sea un renombrado en el mismo volumen: instantáneo, y no
        //      una copia de 460 MB que puede quedarse a medias.
        progress?.Report(new UpdateProgress(UpdatePhase.Descomprimiendo, "Descomprimiendo…"));

        // Una carpeta de preparación de un intento anterior tiene que irse ENTERA: descomprimir
        // encima mezclaría dos versiones, y `ExtractToDirectory` fallaría a mitad con un error que
        // no explica nada. Si ni con reintentos se deja borrar, se aborta con la receta.
        if (!TryDeleteDirectory(StagedDir))
        {
            return Failed(
                Prescribe(
                    $"No se pudo retirar la carpeta {StagedName} de un intento anterior, así que "
                    + "no hay dónde descomprimir. No se ha modificado nada."),
                release.HtmlUrl ?? releaseUrl);
        }

        ZipFile.ExtractToDirectory(zipPath, StagedDir);
        TryDeleteFile(zipPath);

        string mainExe = _mainExe;
        if (!File.Exists(Path.Combine(StagedDir, mainExe)))
        {
            TryDeleteDirectory(StagedDir);
            return Failed(
                $"El paquete descargado no contiene {mainExe}. No se ha modificado nada.",
                release.HtmlUrl ?? releaseUrl);
        }

        // ---- El relevo, copiado FUERA de lo que se va a sustituir.
        progress?.Report(new UpdateProgress(UpdatePhase.Sustituyendo, "Cerrando Atalaya para sustituirla…"));
        string runner = CopyRunnerOut();

        _journal.Record(new UpdateAttempt(DateTimeOffset.UtcNow, from, to, UpdateOutcome.Iniciada));
        TryDeleteFile(_paths.UpdateResultJson);

        var relay = new ProcessStartInfo(runner)
        {
            UseShellExecute = true,
            WorkingDirectory = _paths.Update,
            ArgumentList =
            {
                "--pid", Environment.ProcessId.ToString(),
                "--app-dir", _appDir,
                "--staged", StagedDir,
                "--backup", BackupDir,
                "--result", _paths.UpdateResultJson,
                "--exe", mainExe,
                "--from", from,
                "--to", to,
            },
        };

        // Quien detecta el cliente de sincronización es la aplicación, no el relevo: él solo
        // necesita la frase para poder recetarla si algo se bloquea (BUGFIX-SYNC).
        if (SyncedFolders.Advice(_appDir) is { Length: > 0 } advice)
        {
            relay.ArgumentList.Add("--sync-note");
            relay.ArgumentList.Add(advice);
        }

        _launch(relay);

        _log.LogInformation("Actualización {From} → {To}: relevo lanzado, cerrando.", from, to);
        return new UpdateStart(true, $"Actualizando a {to}. Atalaya se cerrará y volverá sola.");
    }

    // ------------------------------------------------------------------ el día después

    /// <summary>
    /// Lee el parte que dejó el relevo, lo apunta en el registro y lo retira. Se llama UNA vez al
    /// arrancar.
    /// <para>
    /// Aquí es donde se borra la copia de la versión anterior, y no antes: «se conserva hasta que
    /// la nueva arranca bien» solo significa algo si quien la borra es la nueva, ya arrancada.
    /// </para>
    /// </summary>
    public UpdateAftermath? TakeAftermath()
    {
        // Lo primero, y pase lo que pase: lo que un arranque anterior no pudo borrar. Va aquí y no
        // detrás del parte porque casi siempre no habrá parte que leer — y es justamente entonces
        // cuando hay que reintentarlo (BUGFIX-SYNC).
        SweepPendingCleanup();

        string path = _paths.UpdateResultJson;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            string outcome = Text(root, "outcome");
            string from = Text(root, "fromVersion");
            string to = Text(root, "toVersion");
            string message = Text(root, "message");
            string detail = Text(root, "detail");
            string backup = Text(root, "backupDir");

            bool ok = outcome == "Actualizada";

            // Lo que hay que retirar: la copia de la versión anterior —solo si la nueva llegó a
            // arrancar, que es esto— y las huérfanas que el relevo tuvo que esquivar. Lo que no se
            // deje ahora se apunta y se reintenta en el siguiente arranque, hasta que se pueda.
            var chores = new List<string>(Strings(root, "orphanBackups"));
            if (ok && backup.Length > 0)
            {
                chores.Add(backup);
            }

            RememberForCleanup(chores.Where(dir => !TryDeleteDirectory(dir)));

            _journal.Record(new UpdateAttempt(
                DateTimeOffset.UtcNow,
                from,
                to,
                outcome switch
                {
                    "Actualizada" => UpdateOutcome.Completada,
                    "Restaurada" => UpdateOutcome.Restaurada,
                    _ => UpdateOutcome.Abortada,
                },
                detail));

            _log.LogInformation("Actualización {From} → {To}: {Outcome}. {Detail}", from, to, outcome, detail);
            TryDeleteFile(path);
            if (!TryDeleteDirectory(StagedDir))
            {
                RememberForCleanup(new[] { StagedDir });
            }

            return new UpdateAftermath(ok, ok ? $"Atalaya se ha actualizado a la {to}." : message);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "No se pudo leer el parte de la actualización.");
            TryDeleteFile(path);
            return null;
        }
    }

    // ------------------------------------------------------------------ la limpieza que quedó a medias

    /// <summary>
    /// Reintenta los borrados que un arranque anterior no pudo hacer (BUGFIX-SYNC).
    /// <para>
    /// Antes, una copia que no se dejaba borrar se olvidaba para siempre — y ahí se quedaba,
    /// bloqueada, hasta que la actualización siguiente chocaba con ella y abortaba. Ahora consta
    /// por escrito y cada arranque lo vuelve a intentar: un cliente de sincronización suelta lo
    /// que retiene en segundos, y desde luego lo ha soltado al día siguiente.
    /// </para>
    /// <para>
    /// Es cortesía, no un requisito: si tampoco hoy se puede, se anota en el log y se sigue.
    /// </para>
    /// </summary>
    public void SweepPendingCleanup()
    {
        List<string> pending = ReadPending();
        if (pending.Count == 0)
        {
            return;
        }

        var left = new List<string>();
        foreach (string dir in pending)
        {
            if (!Directory.Exists(dir))
            {
                _log.LogInformation("La carpeta {Dir} de una actualización anterior ya no está.", dir);
            }
            else if (TryDeleteDirectory(dir))
            {
                _log.LogInformation("Retirada la carpeta {Dir} que quedó de una actualización anterior.", dir);
            }
            else
            {
                left.Add(dir);
            }
        }

        WritePending(left);

        if (left.Count > 0)
        {
            _log.LogWarning(
                "Quedan {Count} carpetas de actualizaciones anteriores que no se dejan borrar; se "
                + "reintentará al arrancar: {Dirs}",
                left.Count,
                string.Join(", ", left));
        }
    }

    /// <summary>Apunta lo que no se pudo borrar, sin duplicar lo que ya estaba apuntado.</summary>
    private void RememberForCleanup(IEnumerable<string> dirs)
    {
        var all = ReadPending();
        foreach (string dir in dirs.Where(d => d is { Length: > 0 }))
        {
            if (!all.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                all.Add(dir);
                _log.LogWarning(
                    "No se pudo borrar {Dir}; queda apuntado para reintentarlo al arrancar.", dir);
            }
        }

        WritePending(all);
    }

    private List<string> ReadPending()
    {
        try
        {
            return File.Exists(_paths.UpdateCleanupPending)
                ? File.ReadAllLines(_paths.UpdateCleanupPending)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
        catch (IOException)
        {
            return new List<string>();
        }
    }

    private void WritePending(IReadOnlyList<string> dirs)
    {
        try
        {
            if (dirs.Count == 0)
            {
                TryDeleteFile(_paths.UpdateCleanupPending);
                return;
            }

            Directory.CreateDirectory(_paths.Update);
            File.WriteAllLines(_paths.UpdateCleanupPending, dirs);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "No se pudo apuntar la limpieza pendiente.");
        }
    }

    // ------------------------------------------------------------------ piezas

    /// <summary>
    /// El mensaje, con la receta detrás cuando la instalación cuelga de una carpeta sincronizada.
    /// </summary>
    private string Prescribe(string message)
        => SyncedFolders.Advice(_appDir) is { Length: > 0 } advice ? $"{message} {advice}" : message;

    /// <summary>
    /// ¿Se puede escribir Y crear carpetas donde vive Atalaya? Se prueba HACIÉNDOLO: leer los ACL
    /// dice lo que el sistema cree, y escribir dice lo que de verdad pasa (antivirus incluido).
    /// </summary>
    private string? NotWritable()
    {
        string probe = Path.Combine(_appDir, $".atalaya-escritura-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probe);
            File.WriteAllText(Path.Combine(probe, "p"), "p");
            Directory.Delete(probe, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                if (Directory.Exists(probe))
                {
                    Directory.Delete(probe, recursive: true);
                }
            }
            catch
            {
                // La sonda no puede tapar el problema que vino a detectar.
            }

            return $"sin permiso de escritura en {_appDir} ({ex.GetType().Name})";
        }
    }

    private async Task<string> ReadChecksumAsync(
        string token, (string Owner, string Repo) repo, GitHubReleaseAsset asset, CancellationToken ct)
    {
        string path = Path.Combine(_paths.Update, asset.Name);
        TryDeleteFile(path);
        await _api.DownloadAssetAsync(token, repo.Owner, repo.Repo, asset.Id, path, null, ct);
        string text = await File.ReadAllTextAsync(path, ct);
        TryDeleteFile(path);
        return ParseChecksum(text);
    }

    /// <summary>
    /// El digest de un fichero <c>.sha256</c>. Se acepta tanto el hash a secas como el formato
    /// «hash  fichero» de <c>sha256sum</c>: se busca la primera palabra de 64 hexadecimales, que
    /// es lo único que hay en las dos formas.
    /// </summary>
    internal static string ParseChecksum(string text)
    {
        foreach (string word in (text ?? string.Empty)
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.Length == 64 && word.All(Uri.IsHexDigit))
            {
                return word.ToLowerInvariant();
            }
        }

        throw new InvalidOperationException("el fichero de checksum no contiene un SHA-256.");
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Copia el relevo a %LOCALAPPDATA%. Tiene que correr desde FUERA de la carpeta que va a
    /// sustituir: un programa no puede reemplazar el directorio del que se está ejecutando.
    /// </summary>
    private string CopyRunnerOut()
    {
        string dir = Path.Combine(_paths.Update, "runner");
        Directory.CreateDirectory(dir);
        string destination = Path.Combine(dir, RunnerExe);
        File.Copy(Path.Combine(_appDir, RunnerExe), destination, overwrite: true);
        return destination;
    }

    private void Cleanup()
    {
        TryDeleteDirectory(StagedDir);
        try
        {
            foreach (string file in Directory.EnumerateFiles(_paths.Update, "*.zip*"))
            {
                TryDeleteFile(file);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private UpdateStart Failed(string message, string? url)
    {
        _log.LogInformation("Actualización no realizada: {Message}", message);
        return new UpdateStart(false, message, url);
    }

    /// <summary>La causa, dicha para quien la va a leer y no para quien escribió el código.</summary>
    private string Explain(Exception ex) => ex switch
    {
        GitHubApiException { Problem: GitHubApiProblem.Offline } =>
            "No hay conexión con github.com, así que no se ha podido descargar nada. "
            + "Comprueba la red o el proxy y reintenta.",
        GitHubApiException { Problem: GitHubApiProblem.TokenRejected } =>
            "GitHub ha rechazado el token de la cuenta. Vuelve a conectar la cuenta y reintenta.",
        GitHubApiException api => api.Message,
        UnauthorizedAccessException => Prescribe(
            "El sistema ha denegado el acceso a la carpeta de Atalaya. Puede ser el antivirus, un "
            + "cliente de sincronización o los permisos de la carpeta. No se ha modificado nada."),
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) =>
            "No hay espacio en disco para el paquete. No se ha modificado nada.",
        IOException io => Prescribe(
            $"Fallo de disco durante la actualización: {io.Message}. No se ha modificado nada."),
        _ => $"La actualización no se ha podido preparar: {ex.Message}. No se ha modificado nada.",
    };

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Una lista de cadenas del parte. Ausente vale como vacía: un relevo de una versión anterior
    /// no escribe este campo, y eso no puede romper el arranque de la nueva.
    /// </summary>
    private static IEnumerable<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Limpiar es cortesía, no un requisito.
        }
    }

    /// <summary>
    /// Las esperas entre intentos de borrado, en milisegundos. Más cortas que las del relevo
    /// —1,3 s frente a 6— porque aquí nadie está esperando el resultado: lo que no salga hoy se
    /// apunta y se reintenta al arrancar, así que insistir más solo retrasaría la ventana.
    /// </summary>
    private static readonly int[] DeleteWaitsMs = { 100, 300, 900 };

    /// <summary>
    /// Borra la carpeta, insistiendo. Devuelve si al final ya no está — que es lo único que
    /// importa a quien llama: lo que quede se apunta para el siguiente arranque.
    /// </summary>
    private static bool TryDeleteDirectory(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (attempt < DeleteWaitsMs.Length
                                       && ex is IOException or UnauthorizedAccessException)
            {
                // Un cliente de sincronización que está subiendo la carpeta la suelta en segundos.
                Thread.Sleep(DeleteWaitsMs[attempt]);
            }
            catch
            {
                return false;
            }
        }
    }
}
