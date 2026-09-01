using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.App.Services;

/// <summary>
/// El resultado de mirar si hay versión nueva (F8 §3). Cuando no la hay —o no se ha podido
/// mirar— es <see cref="None"/> y la interfaz no enseña nada.
/// </summary>
/// <param name="Version">La versión de la Release, ya parseada. Null si no hay aviso que dar.</param>
/// <param name="Url">La página de la Release, adonde lleva «Ver novedades».</param>
/// <param name="Reason">
/// Por qué no hay aviso, para el log. Nunca se enseña: un chequeo de cortesía que explica sus
/// fallos en pantalla es un chequeo que molesta por fallar, que es exactamente lo que no puede
/// hacer.
/// </param>
/// <param name="Current">
/// La versión que se está ejecutando, <b>la misma con la que se comparó</b>. Viaja con el
/// resultado y no se vuelve a leer del ensamblado en la interfaz: si el aviso preguntara por su
/// cuenta, el número que enseña y el que usó la decisión podrían no ser el mismo — que es
/// exactamente la avería que trajo aquí (BUGFIX-AVISO).
/// </param>
/// <param name="Tag">
/// El tag EXACTO de la Release, tal y como lo escribió GitHub. La versión parseada sirve para
/// comparar y para enseñar; para volver a pedirle a GitHub esa misma Release hace falta la
/// cadena literal, que puede llevar «v» o no llevarla (F11).
/// </param>
public sealed record UpdateAvailability(
    SemanticVersion? Version,
    string? Url,
    string Reason,
    string? Tag = null,
    SemanticVersion? Current = null)
{
    public static UpdateAvailability None(string reason) => new(null, null, reason);

    public bool HasUpdate => Version is not null;

    /// <summary>
    /// La frase del aviso, con <b>las dos</b> versiones (BUGFIX-AVISO).
    /// <para>
    /// Antes decía solo una —«Atalaya 1.0 disponible»— y eso es lo que hizo invisible el defecto
    /// durante una release entera: un único número, sin nada con lo que contrastarlo, se lee como
    /// verdadero. Con las dos delante, cualquier incoherencia salta a la vista sin tener que
    /// abrir «Acerca de».
    /// </para>
    /// <para>
    /// <b>Y la construye el resultado del chequeo, no la interfaz.</b> El banner no vuelve a
    /// calcular ni a formatear nada: enseña esto. Que quien decide sea quien redacta es lo que
    /// impide que el texto y la decisión puedan discrepar.
    /// </para>
    /// </summary>
    public string Headline => Version is null
        ? string.Empty
        : Current is null
            ? $"Disponible la {Version}"
            : $"Tienes la {Current} · disponible la {Version}";
}

/// <summary>
/// Mira si hay una versión de Atalaya más nueva que la que se está ejecutando (F8 §3).
/// <para>
/// <b>Cero credenciales nuevas.</b> Pregunta a la API de GitHub con el token de la cuenta que la
/// aplicación ya tiene: el repositorio es privado y ese token ya entra. Un token de servicio para
/// esto habría sido un secreto más que repartir, rotar y perder.
/// </para>
/// <para>
/// <b>Y aquí no se descarga nada.</b> Este servicio solo MIRA. Quien descarga y sustituye es
/// <see cref="SelfUpdateService"/>, y solo cuando alguien pulsa el botón (F11): ni al arrancar, ni
/// al cerrar, ni en segundo plano. Una aplicación que se reescribe sola mientras alguien la usa
/// es un problema, no una comodidad.
/// </para>
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>
    /// El <b>suelo anti-bucle</b>: dos consultas nunca se pisan a menos de 15 minutos.
    /// <para>
    /// No es una cuota diaria, y por eso son 15 minutos y no 24 horas: se comprueba <b>en cada
    /// arranque</b>, así que reiniciar tras publicar una release basta para ver el aviso. Lo único
    /// que este suelo impide es el caso degenerado —abrir y cerrar la aplicación diez veces
    /// seguidas— que convertiría una cortesía en machaqueo. Contra el coste real no hay nada que
    /// racionar: es <b>una</b> llamada REST por arranque, y la aplicación ya sondea el hub cada
    /// minuto.
    /// </para>
    /// <para>
    /// Y sella la diferencia entre poder probar la función y no poder: con la cuota de un día,
    /// ver el aviso exigía editar la caché a mano.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Cada cuánto vuelve a mirar una instancia que <b>lleva abierta sin reiniciarse</b>. Ahí no
    /// hay arranque que dispare el chequeo, y 24 h siguen bastando: nadie tiene prisa por
    /// enterarse de esto, y menos quien no ha cerrado la aplicación en un día.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly DeployConfig _deploy;
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string> _currentVersion;

    /// <summary>
    /// Cuándo lo INTENTÓ este proceso, esté o no en los ajustes. El re-chequeo periódico cuenta
    /// desde aquí y no desde el último acierto: un sello que solo avanza cuando hay red
    /// convertiría el re-chequeo de una instancia offline en un reintento por cada tick.
    /// </summary>
    private DateTimeOffset? _lastAttemptUtc;

    public UpdateCheckService(
        DeployConfig deploy,
        GitHubAccountService account,
        GitHubApiClient api,
        SettingsService settings,
        ILogger<UpdateCheckService>? log = null,
        Func<DateTimeOffset>? now = null,
        Func<string>? currentVersion = null)
    {
        _deploy = deploy;
        _account = account;
        _api = api;
        _settings = settings;
        _log = log ?? NullLogger<UpdateCheckService>.Instance;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        // La versión propia se inyecta para poder probar el «hay una más nueva» sin recompilar el
        // ensamblado de tests con otro número.
        _currentVersion = currentVersion ?? ViewModels.AboutInfo.CurrentVersion;
    }

    /// <summary>
    /// ¿Hay versión nueva? Nunca lanza: cualquier fallo se registra y sale
    /// <see cref="UpdateAvailability.None"/>.
    /// </summary>
    /// <param name="force">
    /// Salta el suelo de <see cref="MinimumInterval"/>. Lo usa quien pregunta a mano; el arranque
    /// nunca — para eso está el suelo.
    /// </param>
    public async Task<UpdateAvailability> CheckAsync(CancellationToken ct, bool force = false)
    {
        _lastAttemptUtc = _now();
        try
        {
            return await CheckCoreAsync(ct, force);
        }
        catch (OperationCanceledException)
        {
            return UpdateAvailability.None("chequeo cancelado");
        }
        catch (Exception ex)
        {
            // La red, los permisos y la API son cosas de fuera: que fallen no es un defecto de
            // Atalaya y no puede tener consecuencias visibles.
            _log.LogInformation(ex, "No se pudo comprobar si hay una versión nueva de Atalaya.");
            return UpdateAvailability.None($"error: {ex.Message}");
        }
    }

    private async Task<UpdateAvailability> CheckCoreAsync(CancellationToken ct, bool force)
    {
        // BUGFIX-VERSION: se compara con la versión BASE, sin la marca de desarrollo.
        //
        // Un build local se estampa «1.0.3-dev+sha», y en SemVer un pre-release es ANTERIOR a su
        // versión final: sin recortarlo, a quien va por delante de la 1.0.3 se le anunciaría que
        // «existe la 1.0.3» y se le mandaría a descargar lo que ya tiene. Con la base, un
        // 1.0.3-dev calla ante la 1.0.3 y avisa en cuanto salga la 1.0.4, que es lo que se quiere.
        string raw = _currentVersion();
        SemanticVersion? mine = SemanticVersion.TryParse(ViewModels.AboutInfo.BaseVersion(raw));
        if (mine is null)
        {
            return Log(UpdateAvailability.None($"la versión propia no se puede interpretar: «{raw}»"));
        }

        if (!_deploy.ChecksForUpdates)
        {
            return Log(UpdateAvailability.None("el despliegue no declara appRepoUrl"));
        }

        if (GitHubApiClient.ParseRepositoryUrl(_deploy.AppRepoUrl) is not { } repo)
        {
            return Log(UpdateAvailability.None($"appRepoUrl no es un repo de GitHub: {_deploy.AppRepoUrl}"));
        }

        AppSettings settings = _settings.Current;

        // Dentro del suelo: se contesta con lo último que se vio. El banner no puede desaparecer
        // solo porque esta consulta no tocara.
        if (!force && !DueAt(_now(), settings.LastUpdateCheckUtc, MinimumInterval))
        {
            return Log(Decide(mine, settings.LastSeenReleaseTag, settings.LastSeenReleaseUrl, settings,
                cached: true));
        }

        if (_account.Token is not { Length: > 0 } token)
        {
            return Log(UpdateAvailability.None("no hay cuenta conectada"));
        }

        GitHubRelease? release = await _api.GetLatestReleaseAsync(token, repo.Owner, repo.Repo, ct);
        if (release is null)
        {
            // Sí se sella: «no hay ninguna Release» es una respuesta, no un fallo.
            Stamp(settings, tag: null, url: null);
            return Log(UpdateAvailability.None("el repositorio todavía no ha publicado ninguna Release"));
        }

        Stamp(settings, release.TagName, release.HtmlUrl);
        return Log(Decide(mine, release.TagName, release.HtmlUrl, settings, cached: false));
    }

    /// <summary>
    /// La decisión, con la versión propia, la de la Release y lo que el usuario haya descartado.
    /// Es pura a propósito: es la regla que los tests tienen que poder ejercitar sin red.
    /// </summary>
    private static UpdateAvailability Decide(
        SemanticVersion mine, string? tag, string? url, AppSettings settings, bool cached)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return UpdateAvailability.None(cached ? "todavía no se ha visto ninguna Release" : "Release sin tag");
        }

        SemanticVersion? theirs = SemanticVersion.TryParse(tag);
        if (theirs is null)
        {
            return UpdateAvailability.None($"el tag «{tag}» no es una versión SemVer");
        }

        if (!theirs.IsNewerThan(mine))
        {
            return UpdateAvailability.None($"al día: {mine} frente a {theirs}");
        }

        // Descartada por su VERSIÓN, no por su tag: «v1.2.0» y «1.2.0» son la misma versión, y una
        // Release re-etiquetada no puede resucitar un aviso que alguien ya cerró.
        SemanticVersion? dismissed = SemanticVersion.TryParse(settings.DismissedUpdateVersion);
        if (dismissed is not null && !theirs.IsNewerThan(dismissed))
        {
            return UpdateAvailability.None($"{theirs} descartada por el usuario");
        }

        return new UpdateAvailability(
            theirs, url, $"hay versión nueva: {theirs} (tienes {mine})", tag, mine);
    }

    /// <summary>
    /// ¿Le toca a una instancia que lleva abierta? Se pregunta desde el temporizador de la
    /// ventana, que solo sabe que el tiempo pasa; la regla —24 h desde el último intento de este
    /// proceso— vive aquí, junto al resto de la política de frecuencia.
    /// </summary>
    public bool PeriodicRecheckDue() => DueAt(_now(), _lastAttemptUtc, CheckInterval);

    /// <summary>
    /// ¿Toca preguntar? Sin sello previo, siempre — es el primer arranque. Un sello del futuro
    /// (reloj movido hacia atrás) también cuenta como que toca: si no, un solo cambio de hora
    /// podría dejar la comprobación dormida para siempre.
    /// </summary>
    private static bool DueAt(DateTimeOffset now, DateTimeOffset? last, TimeSpan interval)
        => last is null || now - last.Value >= interval || last.Value > now;

    /// <summary>
    /// Guarda lo que se acaba de ver y sella la hora. Los ajustes se escriben aquí y no en el
    /// view-model porque el sello es parte de la consulta: si se olvidara, el suelo de
    /// <see cref="MinimumInterval"/> no existiría.
    /// </summary>
    private void Stamp(AppSettings settings, string? tag, string? url)
    {
        settings.LastUpdateCheckUtc = _now();
        settings.LastSeenReleaseTag = tag;
        settings.LastSeenReleaseUrl = url;
        Save(settings);
    }

    /// <summary>
    /// Descarta ESTA versión: no se vuelve a avisar de ella, sí de la siguiente. Se guarda
    /// normalizada para que el mismo número escrito de dos formas no cuente como dos versiones.
    /// </summary>
    public void Dismiss(SemanticVersion? version)
    {
        if (version is null)
        {
            return;
        }

        AppSettings settings = _settings.Current;
        settings.DismissedUpdateVersion = version.ToString();
        Save(settings);
        _log.LogInformation("Aviso de la versión {Version} descartado por el usuario.", version);
    }

    /// <summary>Guardar ajustes nunca puede tumbar un chequeo de cortesía.</summary>
    private void Save(AppSettings settings)
    {
        try
        {
            _settings.Save(settings);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "No se pudo guardar el estado del chequeo de versión.");
        }
    }

    private UpdateAvailability Log(UpdateAvailability result)
    {
        _log.LogInformation("Chequeo de versión: {Reason}.", result.Reason);
        return result;
    }
}
