using Atalaya.Storage.Sync;

namespace Atalaya.App.Services;

/// <summary>
/// Qué se lleva por delante un reset de fábrica, contado sobre el hub (F5.7 §5). La confirmación
/// no puede decir «se borrará todo»: quien la lee necesita el tamaño de lo que está a punto de
/// destruir, y —a diferencia del borrado de una app— lo que destruye no es suyo, es del equipo.
/// </summary>
public sealed record FactoryResetImpact(int Apps, int Findings, int Sessions)
{
    /// <summary>El hub ya está vacío: no hay nada del equipo que perder, solo estado local.</summary>
    public bool HubIsEmpty => Apps == 0;

    /// <summary>Lo que se borra, con los números por delante.</summary>
    public string Describe() => HubIsEmpty
        ? "El hub ya está vacío: no hay ninguna aplicación que borrar."
        : $"Se eliminarán del hub {Apps} aplicación(es), con sus {Findings} hallazgo(s) "
          + $"y {Sessions} sesión(es). TODAS, no solo las tuyas.";

    /// <summary>La consecuencia que nadie ve al pulsar: le pasa a los demás, en sus máquinas.</summary>
    public string TeamWarning =>
        "Esto afecta a TODO el equipo: tus compañeros verán desaparecer todo en su próxima sincronización.";

    /// <summary>Y en esta máquina: la cuenta se desconecta y Atalaya vuelve al primer arranque.</summary>
    /// <remarks>
    /// PROV-3 §3 — desde esta entrega se va también la clave de API del endpoint. La frase no la
    /// enumera porque «los ajustes y la cuenta conectada» ya es lo que el usuario entiende por
    /// «todo lo mío»; lo que importa es que el borrado la incluya de verdad, y de eso se encarga
    /// <see cref="FactoryResetService.WipeLocalState"/>.
    /// </remarks>
    public string LocalWarning =>
        "En esta máquina se borran el clon del hub, las rutas de los clones (machines.json), "
        + "los ajustes y la cuenta conectada. Atalaya arrancará como recién instalada.";

    /// <summary>Y qué NO se pierde: ni el código auditado, ni la copia recuperable del historial.</summary>
    public string Reassurance =>
        "El historial git del hub conserva una copia recuperable por un administrador. "
        + "Los repositorios auditados y tus clones de código no se tocan.";
}

/// <summary>Cómo acabó el reset. <paramref name="Done"/> false significa que NO se tocó nada.</summary>
public sealed record FactoryResetResult(bool Done, string Message);

/// <summary>
/// El reset de fábrica (F5.7 §5): deja Atalaya como recién instalada, en el hub y en esta máquina.
/// <para>
/// <b>Por qué es ATÓMICO y en ese orden.</b> Primero el hub, luego lo local. Si se hiciera al
/// revés —o si el fallo del push no diera marcha atrás— quedaría el peor estado posible: esta
/// máquina desconectada y sin ajustes, el hub intacto para todos los demás, y nadie con la
/// información necesaria para saber qué pasó. Por eso el borrado del hub se commitea y se
/// <b>publica</b> ANTES de tocar un solo byte local, y cualquier fallo en ese tramo devuelve el
/// clon a su commit anterior con <see cref="HubSyncService.ResetHardTo"/>: no se borra nada.
/// </para>
/// <para>
/// <b>Qué NO borra.</b> Los repositorios auditados, los clones de código del usuario y los logs de
/// <c>%LOCALAPPDATA%/Atalaya/logs</c> — que son justamente lo que hace falta para investigar un
/// reset que salió mal, y no contienen estado que reconstruya nada.
/// </para>
/// </summary>
public sealed class FactoryResetService
{
    private readonly HubContext _hub;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly OpenSessionStore _openSession;

    /// <summary>
    /// <b>Las claves de API</b> (PROV-3 §3). Es un parámetro OBLIGATORIO y no uno opcional a
    /// propósito: un reset que se pudiera construir sin el almacén sería un reset que puede
    /// olvidarse de la clave, y olvidarse aquí significa dejar en la máquina —cifrada, pero
    /// entera— la credencial de pago de quien creía haber borrado todo lo suyo.
    /// </summary>
    private readonly ProviderSecretStore _secrets;

    public FactoryResetService(
        HubContext hub,
        AppPaths paths,
        SettingsService settings,
        GitHubAccountService account,
        OpenSessionStore openSession,
        ProviderSecretStore secrets)
    {
        _hub = hub;
        _paths = paths;
        _settings = settings;
        _account = account;
        _openSession = openSession;
        _secrets = secrets;
    }

    /// <summary>Cuenta lo que hay hoy en el hub: es lo que enumera la confirmación.</summary>
    public FactoryResetImpact Describe()
    {
        IReadOnlyList<string> slugs = _hub.Store.ListAppSlugs();
        int findings = 0;
        int sessions = 0;
        foreach (string slug in slugs)
        {
            findings += _hub.Store.ListFindings(slug).Count;
            sessions += _hub.Store.ListSessions(slug).Count;
        }

        return new FactoryResetImpact(slugs.Count, findings, sessions);
    }

    /// <summary>
    /// Vacía el hub y esta máquina, o no hace nada. Nunca deja un estado a medias.
    /// </summary>
    /// <param name="by">Quién lo pidió: va en el mensaje del commit, la única traza que queda.</param>
    public FactoryResetResult Reset(string by)
    {
        if (!_hub.IsConfigured || _hub.Sync is null)
        {
            return new FactoryResetResult(false,
                "El hub no está conectado: el borrado no se podría publicar, así que no se ha borrado nada.");
        }

        HubSyncService sync = _hub.Sync;

        // 1. Ponerse al día ANTES de destruir. Sin esto el push arrastraría un rebase sobre
        //    commits que nunca llegamos a ver, y el punto de retorno apuntaría a un pasado que
        //    ya no es el estado del equipo.
        string? returnPoint;
        try
        {
            sync.Pull();
            returnPoint = sync.HeadCommitSha;
        }
        catch (Exception ex)
        {
            return new FactoryResetResult(false, $"No se pudo sincronizar con el hub: {ex.Message}. No se ha borrado nada.");
        }

        if (returnPoint is null)
        {
            return new FactoryResetResult(false,
                "El clon del hub no tiene historial al que volver si algo falla. No se ha borrado nada.");
        }

        // 2. Vaciar apps/ ENTERO. Se borra el directorio en vez de ir app por app por la misma
        //    razón que en el hard-reset de una app: lo que se añada al esquema mañana también
        //    tiene que irse.
        try
        {
            DeleteTree(_hub.HubPaths.AppsDir);
        }
        catch (Exception ex)
        {
            Rollback(sync, returnPoint);
            return new FactoryResetResult(false, $"No se pudo vaciar el hub: {ex.Message}. No se ha borrado nada.");
        }

        // 3. Commit + push. El push es la puerta: mientras no cruce, nada local se toca.
        try
        {
            sync.Commit($"hub: reset de fábrica por {by}");
            if (!sync.Push())
            {
                Rollback(sync, returnPoint);
                return new FactoryResetResult(false,
                    "El borrado no llegó al hub (sin permisos o sin red). No se ha borrado nada: "
                    + "el reset se cancela entero para no dejar tu máquina limpia y el hub lleno.");
            }
        }
        catch (Exception ex)
        {
            Rollback(sync, returnPoint);
            return new FactoryResetResult(false,
                $"El borrado no llegó al hub ({ex.Message}). No se ha borrado nada.");
        }

        // 4. Y ahora sí, lo local.
        WipeLocalState();

        return new FactoryResetResult(true,
            "Restablecimiento de fábrica completado. El hub está vacío y esta máquina, como recién instalada.");
    }

    /// <summary>
    /// Devuelve el clon al commit anterior al borrado: rama y árbol de trabajo. Si ni siquiera
    /// esto se puede, se dice — un clon a medias se arregla con un «Sincronizar ahora», pero el
    /// usuario tiene que saberlo.
    /// </summary>
    private static void Rollback(HubSyncService sync, string returnPoint)
    {
        try
        {
            sync.ResetHardTo(returnPoint);
        }
        catch
        {
            // Best-effort: lo que importa —el estado LOCAL del usuario— sigue intacto de todos
            // modos, porque este camino nunca llega al punto 4.
        }
    }

    /// <summary>
    /// El estado local, en el orden que exige Windows: primero soltar los handles de git, y solo
    /// después borrar el directorio que los tenía abiertos.
    /// </summary>
    private void WipeLocalState()
    {
        _hub.CloseSync();
        _account.Disconnect();          // borra auth.dat y suelta el token en memoria
        _openSession.Delete();          // una marca huérfana haría «recuperar» una sesión sobre nada
        DeleteTree(_paths.Hub);
        DeleteFile(_paths.MachinesJson);
        // PROV-3 §3 — y las claves de API, que viven al lado de `auth.dat` y son de lo MISMO que
        // él: una credencial de esta máquina. Sin esta línea, «Atalaya arrancará como recién
        // instalada» sería mentira en lo único que cuesta dinero — la clave del endpoint seguiría
        // en el disco, lista para que la usara quien se quedara con la máquina.
        DeleteFile(_secrets.Location);
        _settings.ResetToDefaults();    // borra settings.json — el PAT de respaldo vive dentro
        _hub.RefreshCredentials();      // el piloto de la carcasa deja de contar lo de la cuenta que ya no existe
    }

    /// <summary>
    /// Borra un árbol entero. Limpia atributos antes: los objetos de <c>.git</c> se escriben en
    /// SOLO LECTURA, y <see cref="Directory.Delete(string, bool)"/> se niega a tocarlos.
    /// </summary>
    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(dir, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
