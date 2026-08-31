namespace Atalaya.Updater;

/// <summary>Lo que hay que hacer: qué carpeta, qué contenido nuevo y qué ejecutable relanzar.</summary>
public sealed class SwapPlan
{
    /// <summary>La carpeta de la instalación, la que hay que dejar con la versión nueva dentro.</summary>
    public required string AppDir { get; init; }

    /// <summary>La carpeta con el contenido ya descargado, verificado y descomprimido.</summary>
    public required string StagedDir { get; init; }

    /// <summary>Adónde va lo viejo mientras dura el cambio, y hasta que la nueva arranque bien.</summary>
    public required string BackupDir { get; init; }

    /// <summary>El nombre del ejecutable, para comprobar que el paquete nuevo lo trae.</summary>
    public string MainExe { get; init; } = "Atalaya.exe";

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
/// </summary>
public sealed class FolderSwap
{
    /// <summary>
    /// Punto de inyección de fallos para los tests. Se llama al entrar en cada paso con su
    /// nombre; un test lo usa para lanzar justo a mitad y comprobar que la vuelta atrás deja la
    /// instalación entera. No es una opción de línea de órdenes: no existe fuera de los tests.
    /// </summary>
    internal Action<string>? Fault { get; set; }

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

        // Un intento anterior pudo dejar una copia. Se retira ANTES de empezar: dos «anteriores»
        // no se distinguen, y quedarse con la equivocada es peor que no tener ninguna.
        try
        {
            if (Directory.Exists(plan.BackupDir))
            {
                Directory.Delete(plan.BackupDir, recursive: true);
            }

            Directory.CreateDirectory(plan.BackupDir);
        }
        catch (Exception ex)
        {
            return new SwapResult(SwapOutcome.Intacta,
                "No se pudo preparar la copia de seguridad; no se ha modificado nada.",
                Describe(ex));
        }

        // ---- Paso 1: apartar lo viejo. Desde aquí ya hay que saber deshacer.
        var movedOut = new List<string>();
        var movedIn = new List<string>();

        try
        {
            Fault?.Invoke("apartar");
            foreach (string name in TopLevel(plan.AppDir, plan))
            {
                Move(Path.Combine(plan.AppDir, name), Path.Combine(plan.BackupDir, name));
                movedOut.Add(name);
            }

            // ---- Paso 2: meter lo nuevo.
            Fault?.Invoke("instalar");
            foreach (string name in Directory.EnumerateFileSystemEntries(plan.StagedDir)
                         .Select(Path.GetFileName)
                         .Where(n => n is { Length: > 0 })
                         .Select(n => n!))
            {
                Move(Path.Combine(plan.StagedDir, name), Path.Combine(plan.AppDir, name));
                movedIn.Add(name);
            }

            // ---- Paso 3: lo que el despliegue había editado vuelve a su sitio.
            Fault?.Invoke("conservar");
            foreach (string name in plan.Preserved)
            {
                string kept = Path.Combine(plan.BackupDir, name);
                if (File.Exists(kept))
                {
                    File.Copy(kept, Path.Combine(plan.AppDir, name), overwrite: true);
                }
            }

            // La carpeta de preparación, ya vacía, deja de tener sentido.
            TryDelete(plan.StagedDir);

            return new SwapResult(SwapOutcome.Actualizada, "Atalaya se ha actualizado.");
        }
        catch (Exception ex)
        {
            // ---- Vuelta atrás. Primero se retira lo nuevo que hubiéramos llegado a poner, y
            //      después vuelve lo viejo a su sitio: al revés, lo viejo chocaría con lo nuevo.
            string undo = Undo(plan, movedOut, movedIn);
            return new SwapResult(
                SwapOutcome.Restaurada,
                "La sustitución falló a mitad y se ha restaurado la versión anterior. "
                + "Atalaya sigue siendo la de antes.",
                undo.Length == 0 ? Describe(ex) : $"{Describe(ex)} · {undo}");
        }
    }

    /// <summary>
    /// Deshace lo hecho. Devuelve la descripción de lo que NO se pudo deshacer, o vacío si todo
    /// volvió a su sitio — porque una vuelta atrás que también falla es lo único peor que el
    /// fallo original, y hay que decirlo en vez de callarlo.
    /// </summary>
    private static string Undo(SwapPlan plan, List<string> movedOut, List<string> movedIn)
    {
        var failures = new List<string>();

        foreach (string name in movedIn)
        {
            try
            {
                Move(Path.Combine(plan.AppDir, name), Path.Combine(plan.StagedDir, name));
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
                    Move(Path.Combine(plan.BackupDir, name), back);
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
    /// Lo que hay dentro de la instalación y SÍ es parte de la versión. Las dos carpetas
    /// reservadas se quedan donde están: mover la copia de seguridad dentro de sí misma, o el
    /// paquete nuevo a la copia de lo viejo, sería un bucle con la instalación dentro.
    /// </summary>
    private static IEnumerable<string> TopLevel(string appDir, SwapPlan plan)
    {
        string staged = Path.GetFileName(plan.StagedDir.TrimEnd(Path.DirectorySeparatorChar));
        string backup = Path.GetFileName(plan.BackupDir.TrimEnd(Path.DirectorySeparatorChar));

        foreach (string path in Directory.EnumerateFileSystemEntries(appDir))
        {
            string name = Path.GetFileName(path);
            if (name.Length == 0
                || string.Equals(name, staged, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, backup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>Renombra, sea fichero o carpeta. Mismo volumen: es instantáneo.</summary>
    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Una carpeta de preparación que sobra no es un fallo de la actualización.
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
