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
public sealed record UpdateAvailability(SemanticVersion? Version, string? Url, string Reason)
{
    public static UpdateAvailability None(string reason) => new(null, null, reason);

    public bool HasUpdate => Version is not null;
}

/// <summary>
/// Mira si hay una versión de Atalaya más nueva que la que se está ejecutando (F8 §3).
/// <para>
/// <b>Cero credenciales nuevas.</b> Pregunta a la API de GitHub con el token de la cuenta que la
/// aplicación ya tiene: el repositorio es privado y ese token ya entra. Un token de servicio para
/// esto habría sido un secreto más que repartir, rotar y perder.
/// </para>
/// <para>
/// <b>Y no descarga nada.</b> El aviso lleva al navegador y ahí se acaba su trabajo: el usuario
/// descarga el zip y reemplaza su carpeta. Sin auto-instalación ni descargas en segundo plano —
/// una aplicación que se reescribe sola mientras alguien la usa es un problema, no una comodidad.
/// La actualización asistida (Velopack) está en el backlog como nivel 3.
/// </para>
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>Como mucho una consulta cada 24 h. Nadie tiene prisa por enterarse de esto.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly DeployConfig _deploy;
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string> _currentVersion;

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
    /// Salta el límite de 24 h. Lo usa quien pregunta a mano; el arranque nunca.
    /// </param>
    public async Task<UpdateAvailability> CheckAsync(CancellationToken ct, bool force = false)
    {
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

        // Throttled: se contesta con lo último que se vio. El banner no puede parpadear cada 24 h.
        if (!force && !DueAt(_now(), settings.LastUpdateCheckUtc))
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

        return new UpdateAvailability(theirs, url, $"hay versión nueva: {theirs} (tienes {mine})");
    }

    /// <summary>¿Toca preguntar? Sin sello previo, siempre — es el primer arranque.</summary>
    private static bool DueAt(DateTimeOffset now, DateTimeOffset? last)
        => last is null || now - last.Value >= CheckInterval || last.Value > now;

    /// <summary>
    /// Guarda lo que se acaba de ver y sella la hora. Los ajustes se escriben aquí y no en el
    /// view-model porque el sello es parte de la consulta: si se olvidara, el límite de 24 h no
    /// existiría.
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
