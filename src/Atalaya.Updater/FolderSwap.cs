namespace Atalaya.Updater;

/// <summary>Lo que hay que hacer: qué carpeta, qué contenido nuevo y qué ejecutable relanzar.</summary>
public sealed class SwapPlan
{
    /// <summary>La carpeta de la instalación, la que hay que dejar con la versión nueva dentro.</summary>
    public required string AppDir { get; init; }

    /// <summary>La carpeta con el contenido ya descargado, verificado y descomprimido.</summary>
    public required string StagedDir { get; init; }

    /// <summary>
    /// Adónde va lo viejo mientras dura el cambio, y hasta que la nueva arranque bien. Es el
    /// nombre <b>preferido</b>: si una copia residual de un intento anterior lo ocupa y no se
    /// deja retirar, se usa el siguiente libre (<c>…-2</c>, <c>…-3</c>) antes que fallar.
    /// </summary>
    public required string BackupDir { get; init; }

    /// <summary>El nombre del ejecutable, para comprobar que el paquete nuevo lo trae.</summary>
    public string MainExe { get; init; } = "Atalaya.exe";

    /// <summary>
    /// La receta que acompaña a un fallo cuando la instalación cuelga de una carpeta sincronizada
    /// (BUGFIX-SYNC). <b>La escribe quien lanza el relevo, no el relevo</b>: detectar el cliente
    /// de sincronización es cosa de la aplicación, y hacérselo saber por argumento es lo mismo
    /// que ya se hace con los nombres de las carpetas — el relevo sobrevive a la versión que lo
    /// lanzó justamente porque no comparte nada con ella salvo argumentos.
    /// </summary>
    public string SyncedAdvice { get; init; } = string.Empty;

    /// <summary>
    /// Ficheros de la instalación anterior que sobreviven al cambio. Hoy solo uno: el
    /// <c>appsettings.deploy.json</c> que un despliegue corporativo edita a mano junto al
    /// ejecutable. Reemplazar la carpeta sin esto borraría en silencio la configuración del
    /// despliegue en cada actualización.
    /// </summary>
    public IReadOnlyList<string> Preserved { get; init; } = new[] { "appsettings.deploy.json" };
}

/// <summary>Cómo acabó el cambio. Los tres finales posibles, y ninguno más.</summary>
public enum SwapOutcome
{
    /// <summary>La carpeta tiene la versión nueva.</summary>
    Actualizada,

    /// <summary>Falló a mitad y se deshizo: la carpeta tiene la versión de antes, entera.</summary>
    Restaurada,

    /// <summary>Falló antes de tocar nada: la instalación no se llegó a modificar.</summary>
    Intacta,
}

/// <param name="Outcome">Cómo acabó.</param>
/// <param name="Message">Qué contarle a quien lo lea, en una frase.</param>
/// <param name="Detail">La causa técnica, para el registro. Vacío si no hubo fallo.</param>
public sealed record SwapResult(SwapOutcome Outcome, string Message, string Detail = "")
{
    public bool Ok => Outcome == SwapOutcome.Actualizada;

    /// <summary>
    /// La copia de seguridad que de verdad se usó, que puede no ser la que se pidió. Viaja en el
    /// parte porque quien la borra es la versión nueva al arrancar, y borrar la que no es sería
    /// peor que no borrar ninguna.
    /// </summary>
    public string BackupDir { get; init; } = string.Empty;

    /// <summary>
    /// Copias de intentos anteriores que no se dejaron retirar. No son un fallo —la actualización
    /// las esquiva— pero alguien tiene que acordarse de ellas: se las lleva la versión nueva para
    /// reintentarlo en cada arranque, hasta que se pueda.
    /// </summary>
    public IReadOnlyList<string> Orphans { get; init; } = Array.Empty<string>();
}

/// <summary>
/// El cambio de carpeta, y su vuelta atrás (F11).
/// <para>
/// <b>Todo son renombrados dentro de la misma carpeta</b>, nunca copias. La carpeta nueva se
/// prepara DENTRO de la instalación (<c>.atalaya-nuevo</c>) precisamente para eso: mover 460 MB
/// entre volúmenes tarda minutos y puede quedarse a medias, mientras que un renombrado en el
/// mismo volumen es instantáneo y o pasa o no pasa. Cuanto más corta es la ventana en la que la
/// instalación no está entera, menos probable es el desastre.
/// </para>
/// <para>
/// <b>Y nada se borra hasta que la nueva arranca.</b> Lo viejo se aparta a <c>.atalaya-anterior</c>
/// y se queda ahí; quien lo borra es la versión nueva, en su primer arranque con éxito. Un
/// borrado antes de esa prueba convierte cualquier fallo en una pérdida.
/// </para>
/// <para>
/// <b>Cada movimiento se reintenta</b> (<see cref="RetryPolicy"/>). En una carpeta sincronizada
/// —Escritorio o Documentos redirigidos a OneDrive, que es el entorno corporativo normal— el
/// cliente de sincronización retiene ficheros durante segundos y cualquier renombrado puede
/// fallar por eso. Lo que no cambia es el final: agotados los reintentos, se deshace o no se toca
/// nada, nunca una instalación a medias.
/// </para>
/// </summary>
public sealed class FolderSwap
{
    /// <summary>
    /// Cuántos nombres de copia se prueban antes de rendirse. Llegar al quinto significa cuatro
    /// actualizaciones anteriores que dejaron residuos imborrables: a esas alturas el problema ya
    /// no es el nombre, y seguir inventándolos sería llenar la carpeta de basura.
    /// </summary>
    internal const int MaxBackupNames = 5;

    /// <summary>
    /// Punto de inyección de fallos para los tests. Se llama al entrar en cada paso con su
    /// nombre; un test lo usa para lanzar justo a mitad y comprobar que la vuelta atrás deja la
    /// instalación entera. No es una opción de línea de órdenes: no existe fuera de los tests.
    /// </summary>
    internal Action<string>? Fault { get; set; }

    /// <summary>Cuánto se insiste ante un bloqueo. La de producción, salvo en los tests.</summary>
    public RetryPolicy Retries { get; init; } = RetryPolicy.Default;

    public SwapResult Apply(SwapPlan plan)
    {
        // ---- Antes de tocar nada: que lo que vamos a instalar exista y sea una Atalaya.
        if (!Directory.Exists(plan.StagedDir))
        {
            return new SwapResult(SwapOutcome.Intacta,
                "El paquete nuevo no está donde se esperaba; no se ha modificado nada.",
                $"no existe {plan.StagedDir}");
        }

        if (!File.Exists(Path.Combine(plan.StagedDir, plan.MainExe)))
        {
            return new SwapResult(SwapOutcome.Intacta,
                $"El paquete descargado no contiene {plan.MainExe}; no se ha modificado nada.",
                $"falta {plan.MainExe} en {plan.StagedDir}");
        }

        if (!Directory.Exists(plan.AppDir))
        {
            return new SwapResult(SwapOutcome.Intacta,
                "La carpeta de la aplicación ya no existe; no se ha modificado nada.",
                $"no existe {plan.AppDir}");
        }

        // Un intento anterior pudo dejar una copia. Se retira ANTES de empezar —dos «anteriores»
        // no se distinguen, y quedarse con la equivocada es peor que no tener ninguna— y, si no
        // se deja retirar, se usa un nombre libre en vez de fallar contra ella.
        var orphans = new List<string>();
        (string? backupDir, string problem) = PrepareBackup(plan.BackupDir, orphans);
        if (backupDir is null)
        {
            return new SwapResult(SwapOutcome.Intacta,
                Prescribe("No se pudo preparar la copia de seguridad; no se ha modificado nada.", plan),
                problem)
            {
                Orphans = orphans,
            };
        }

        // ---- Paso 1: apartar lo viejo. Desde aquí ya hay que saber deshacer.
        var movedOut = new List<string>();
        var movedIn = new List<string>();

        try
        {
            Fault?.Invoke("apartar");
            foreach (string name in TopLevel(plan))
            {
                Retries.Move(Path.Combine(plan.AppDir, name), Path.Combine(backupDir, name));
                movedOut.Add(name);
            }

            // ---- Paso 2: meter lo nuevo.
            Fault?.Invoke("instalar");
            foreach (string name in Directory.EnumerateFileSystemEntries(plan.StagedDir)
                         .Select(Path.GetFileName)
                         .Where(n => n is { Length: > 0 })
                         .Select(n => n!))
            {
                Retries.Move(Path.Combine(plan.StagedDir, name), Path.Combine(plan.AppDir, name));
                movedIn.Add(name);
            }

            // ---- Paso 3: lo que el despliegue había editado vuelve a su sitio.
            Fault?.Invoke("conservar");
            foreach (string name in plan.Preserved)
            {
                string kept = Path.Combine(backupDir, name);
                if (File.Exists(kept))
                {
                    Retries.Do(() => File.Copy(kept, Path.Combine(plan.AppDir, name), overwrite: true));
                }
            }

            // La carpeta de preparación, ya vacía, deja de tener sentido. Si tampoco se deja
            // borrar, se anota: la próxima actualización descomprimiría encima de ella.
            if (!Retries.TryDelete(plan.StagedDir))
            {
                orphans.Add(plan.StagedDir);
            }

            return new SwapResult(SwapOutcome.Actualizada, "Atalaya se ha actualizado.")
            {
                BackupDir = backupDir,
                Orphans = orphans,
            };
        }
        catch (Exception ex)
        {
            // ---- Vuelta atrás. Primero se retira lo nuevo que hubiéramos llegado a poner, y
            //      después vuelve lo viejo a su sitio: al revés, lo viejo chocaría con lo nuevo.
            string undo = Undo(plan, backupDir, movedOut, movedIn);
            return new SwapResult(
                SwapOutcome.Restaurada,
                Prescribe(
                    "La sustitución falló a mitad y se ha restaurado la versión anterior. "
                    + "Atalaya sigue siendo la de antes.",
                    plan),
                undo.Length == 0 ? Describe(ex) : $"{Describe(ex)} · {undo}")
            {
                BackupDir = backupDir,
                Orphans = orphans,
            };
        }
    }

    /// <summary>
    /// Deja lista una carpeta de copia vacía y devuelve cuál es. Si la preferida está ocupada por
    /// un residuo que no se deja borrar —lo que pasa cuando la limpieza de la actualización
    /// anterior se topó con el cliente de sincronización—, se anota como huérfana y se prueba el
    /// siguiente nombre: <b>un residuo de hace tres meses no puede impedir actualizar hoy</b>.
    /// </summary>
    private (string? Dir, string Problem) PrepareBackup(string preferred, List<string> orphans)
    {
        string problem = string.Empty;

        for (int n = 1; n <= MaxBackupNames; n++)
        {
            string candidate = n == 1 ? preferred : $"{preferred}-{n}";

            if (Directory.Exists(candidate) && !Retries.TryDelete(candidate))
            {
                orphans.Add(candidate);
                problem = $"«{Path.GetFileName(candidate)}» no se dejó retirar";
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);
                return (candidate, string.Empty);
            }
            catch (Exception ex)
            {
                problem = Describe(ex);
            }
        }

        return (null, problem.Length == 0 ? "no quedó ningún nombre de copia libre" : problem);
    }

    /// <summary>
    /// Deshace lo hecho. Devuelve la descripción de lo que NO se pudo deshacer, o vacío si todo
    /// volvió a su sitio — porque una vuelta atrás que también falla es lo único peor que el
    /// fallo original, y hay que decirlo en vez de callarlo.
    /// </summary>
    private string Undo(SwapPlan plan, string backupDir, List<string> movedOut, List<string> movedIn)
    {
        var failures = new List<string>();

        foreach (string name in movedIn)
        {
            try
            {
                Retries.Move(Path.Combine(plan.AppDir, name), Path.Combine(plan.StagedDir, name));
            }
            catch (Exception ex)
            {
                failures.Add($"no se pudo retirar «{name}» ({ex.GetType().Name})");
            }
        }

        foreach (string name in movedOut)
        {
            try
            {
                string back = Path.Combine(plan.AppDir, name);
                if (!File.Exists(back) && !Directory.Exists(back))
                {
                    Retries.Move(Path.Combine(backupDir, name), back);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"no se pudo restaurar «{name}» ({ex.GetType().Name})");
            }
        }

        return failures.Count == 0 ? string.Empty : string.Join("; ", failures);
    }

    /// <summary>
    /// Lo que hay dentro de la instalación y SÍ es parte de la versión. Las carpetas reservadas se
    /// quedan donde están: mover la copia de seguridad dentro de sí misma, o el paquete nuevo a la
    /// copia de lo viejo, sería un bucle con la instalación dentro. Se salta <b>toda</b> la
    /// familia de copias —<c>.atalaya-anterior</c>, <c>-2</c>, <c>-3</c>—, incluidas las huérfanas
    /// que precisamente no se pudieron borrar: meterlas en la copia buena sería enterrar dentro de
    /// ella justo lo que está bloqueado.
    /// </summary>
    private static IEnumerable<string> TopLevel(SwapPlan plan)
    {
        string staged = Path.GetFileName(plan.StagedDir.TrimEnd(Path.DirectorySeparatorChar));
        string backup = Path.GetFileName(plan.BackupDir.TrimEnd(Path.DirectorySeparatorChar));

        foreach (string path in Directory.EnumerateFileSystemEntries(plan.AppDir))
        {
            string name = Path.GetFileName(path);
            if (name.Length == 0
                || string.Equals(name, staged, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(backup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>
    /// El mensaje, con la receta detrás cuando la hay. Un «acceso denegado» a secas no le dice a
    /// nadie qué hacer; «estás dentro de OneDrive, pausa la sincronización» sí.
    /// </summary>
    private static string Prescribe(string message, SwapPlan plan)
        => plan.SyncedAdvice.Length == 0 ? message : $"{message} {plan.SyncedAdvice}";

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
