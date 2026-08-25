using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Qué se va a borrar exactamente, contado sobre el hub (F5.3 §4). La confirmación NO puede decir
/// «se borrará todo»: quien la lee necesita el tamaño real de lo que va a perder.
/// </summary>
public sealed record AppDeletionImpact(
    string Slug,
    string Name,
    int Findings,
    int Sessions,
    int Reports,
    int Silences)
{
    /// <summary>La frase de la confirmación, con los números delante.</summary>
    public string Describe() =>
        $"Se eliminará «{Name}» y toda su traza en el hub: "
        + $"{Findings} hallazgo(s), {Sessions} sesión(es), {Reports} informe(s), {Silences} silencio(s).";
}

/// <summary>Cómo acabó el borrado: qué se quitó y si llegó al remoto.</summary>
/// <param name="Removed">La carpeta de la app estaba y se borró.</param>
/// <param name="Pushed">El commit del borrado llegó al hub remoto.</param>
public sealed record AppDeletionResult(bool Removed, bool Pushed, string Message);

/// <summary>
/// Hard-reset de una aplicación (F5.3 §4): la saca del hub y de esta máquina.
/// <para>
/// <b>Qué NO hace.</b> No toca el repositorio auditado ni el clon local del usuario: borra lo que
/// Atalaya sabe de esa app, no el código. Y no es un borrado irrecuperable en sentido estricto —
/// el historial de git del hub conserva una copia que un administrador puede rescatar—, cosa que
/// la confirmación dice en voz alta en vez de prometer una destrucción que no ocurre.
/// </para>
/// <para>
/// <b>Por qué el commit y el push van juntos y aquí.</b> Borrar solo en local dejaría la app viva
/// para todos los demás y la haría reaparecer en el siguiente pull. El commit se nombra con quién
/// lo hizo porque es la única traza que queda de la decisión.
/// </para>
/// </summary>
public sealed class AppDeletionService
{
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly OpenSessionStore _openSession;

    public AppDeletionService(HubContext hub, MachineConfigStore machines, OpenSessionStore openSession)
    {
        _hub = hub;
        _machines = machines;
        _openSession = openSession;
    }

    /// <summary>Cuenta lo que se perdería, o null si la app ya no está en el hub.</summary>
    public AppDeletionImpact? Describe(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return null;
        }

        return new AppDeletionImpact(
            slug,
            app.Name,
            _hub.Store.ListFindings(slug).Count,
            _hub.Store.ListSessions(slug).Count,
            _hub.Store.ListReports(slug).Count,
            _hub.Store.ListSilences(slug).Count);
    }

    /// <summary>
    /// Borra <c>apps/{slug}/</c> del hub, limpia el estado local de esa app y publica el borrado.
    /// <para>
    /// El orden importa: primero el árbol de trabajo, luego lo local, y el commit+push al final.
    /// Si el push fracasa (sin red, credenciales caídas) el commit se queda pendiente y sale con
    /// el siguiente «Sincronizar ahora» — pero la app ya no está aquí, que es lo que el usuario
    /// pidió, y el resultado lo dice en vez de fingir que se publicó.
    /// </para>
    /// </summary>
    public AppDeletionResult Delete(string slug, string by)
    {
        bool removed = _hub.Store.DeleteApp(slug);
        CleanLocalState(slug);

        if (!removed)
        {
            return new AppDeletionResult(false, true, $"«{slug}» ya no estaba en el hub.");
        }

        if (_hub.Sync is null)
        {
            return new AppDeletionResult(true, false,
                $"«{slug}» se eliminó en local, pero el hub no está conectado: no se ha publicado.");
        }

        _hub.Sync.Commit($"app: hard-reset de {slug} por {by}");
        bool pushed = _hub.Sync.Push();

        return new AppDeletionResult(true, pushed, pushed
            ? $"«{slug}» eliminada y publicada. Los demás la verán desaparecer en su siguiente sync."
            : $"«{slug}» eliminada aquí, pero el borrado no llegó al hub. Usa «Sincronizar ahora».");
    }

    /// <summary>
    /// El rastro local de la app: su ruta de clon en <c>machines.json</c> y, si la hubiera, la
    /// marca de sesión abierta que la nombra — una marca huérfana de una app que ya no existe haría
    /// que el siguiente arranque intentara «recuperar» una sesión sobre nada.
    /// </summary>
    private void CleanLocalState(string slug)
    {
        MachineConfig config = _machines.Load();
        if (config.ClonePaths.Remove(slug))
        {
            _machines.Save(config);
        }

        if (_openSession.TryRead() is { } marker && marker.Slug == slug)
        {
            _openSession.Delete();
        }
    }
}
