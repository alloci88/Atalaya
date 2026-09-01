using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Machine-local application settings (§8 Ajustes). Never stored in the hub.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Legacy explicit git identity. Since F2 the identity is derived from the connected GitHub
    /// profile (D2.2) and this is only a fallback for users configured before F2.
    /// </summary>
    public string? GitUserName { get; set; }

    /// <summary>Legacy explicit git identity email. See <see cref="GitUserName"/>.</summary>
    public string? GitUserEmail { get; set; }

    /// <summary>
    /// Legacy per-user hub URL. Since F2 the hub comes from <c>appsettings.deploy.json</c> (D1);
    /// this is migrated once into <see cref="HubUrlOverride"/> when it differs, then cleared.
    /// </summary>
    public string? HubRepoUrl { get; set; }

    /// <summary>
    /// Advanced / development only: overrides the deployment hub URL. Shown collapsed in Ajustes.
    /// </summary>
    public string? HubUrlOverride { get; set; }

    /// <summary>
    /// DPAPI-protected Personal Access Token (base64). Since F2 this is the HIDDEN FALLBACK for
    /// teams whose organization blocks OAuth Apps; the account token always wins when present.
    /// </summary>
    public string? ProtectedPat { get; set; }

    /// <summary>True once the pre-F2 → F2 settings migration has run (D4).</summary>
    public bool ConnectionMigrated { get; set; }

    /// <summary>
    /// Restore libgit2's hard failure when a certificate's revocation status cannot be checked.
    /// Default false: Atalaya soft-fails that single condition (as browsers do) while still
    /// requiring a trusted, in-date certificate that matches the host, because corporate networks
    /// routinely block the CRL/OCSP responders. Advanced option.
    /// </summary>
    public bool RequireTlsRevocationCheck { get; set; }

    /// <summary>Preferred editor for "open in editor" (§8): "vs" or "vscode".</summary>
    public string Editor { get; set; } = "vs";

    /// <summary>"dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

    public int PollingSeconds { get; set; } = 60;

    public Thresholds DefaultThresholds { get; set; } = new();

    /// <summary>
    /// Interruptor del arreglo asistido (§5.7, H9 — entregado en F6.9).
    /// <para>
    /// <b>Encendido por defecto.</b> El flujo es supervisado por construcción: el agente narra lo
    /// que va a hacer antes de hacerlo, pide permiso fichero a fichero fuera del hallazgo, no
    /// tiene shell ni git ni red, y exige un árbol de trabajo limpio para que descartar devuelva
    /// el clon byte a byte. Nacer apagado escondería una capacidad segura detrás de un ajuste que
    /// nadie iba a encontrar. Y como el valor por defecto es <c>true</c>, las máquinas con un
    /// <c>settings.json</c> anterior a F6.9 —que no traen la clave— lo estrenan encendido.
    /// </para>
    /// </summary>
    public bool EnableAssistedFix { get; set; } = true;

    /// <summary>
    /// True una vez que la promoción única de <see cref="EnableAssistedFix"/> ya se ha aplicado
    /// en esta máquina (F6.9 §7). Misma forma que <see cref="ConnectionMigrated"/> y por la misma
    /// razón: una migración de ajustes tiene que dejar constancia de que corrió, o corre siempre.
    /// <para>
    /// Sin esta marca, apagar el interruptor a mano no duraría un reinicio: la promoción volvería
    /// a encenderlo. Con ella, la promoción ocurre exactamente una vez y a partir de ahí manda lo
    /// que diga el usuario.
    /// </para>
    /// </summary>
    public bool AssistedFixDefaultApplied { get; set; }

    /// <summary>
    /// Optional Copilot SDK BaseDirectory. Leave empty (default): the SDK uses its standard location,
    /// which is where the `copilot` CLI stores the login, so UseLoggedInUser finds it. Only set this
    /// if you deliberately want the SDK isolated to a custom directory.
    /// </summary>
    public string? CopilotBaseDirectory { get; set; }

    /// <summary>
    /// Max minutes to wait for the agent to finish auditing ONE unit before timing out (§5.1).
    /// The SDK default is 1 minute, which is too short for a real audit. Default here: 15.
    /// </summary>
    public int CopilotTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Tope de pasadas del barrido por unidad (F4.1), editable en Ajustes desde F5.1.
    /// <para>
    /// Vive aquí, en la configuración de la máquina, y NO en <c>app.json</c>: el barrido gasta los
    /// tokens del asiento de quien lanza la sesión, así que es una preferencia del operador, no una
    /// propiedad de la app auditada. Un tope de 1 equivale a una pasada única, que es por lo que no
    /// hace falta ningún selector de «modo» por lanzamiento.
    /// </para>
    /// <para>
    /// Por defecto 5 (D-095). El valor vigente se registra en cada sesión y en su informe, para que
    /// «cobertura posiblemente incompleta» siempre se pueda leer contra el tope que había.
    /// </para>
    /// </summary>
    public int MaxPassesPerUnit { get; set; } = 5;

    /// <summary>
    /// Modelo de Copilot con el que se lanzan las sesiones nuevas (<c>SessionConfig.Model</c>).
    /// La lista de opciones se pide al SDK (<c>ListModelsAsync</c>), nunca se codifica a mano; esto
    /// solo guarda el id elegido.
    /// <para>
    /// <b>Vacío por defecto, y es importante que lo sea (F5.15).</b> Aquí ponía <c>"gpt-5"</c>
    /// escrito a mano. El día que GitHub retiró ese modelo, toda máquina con ajustes vírgenes nació
    /// rota: la primera auditoría moría en <c>session.create</c>. Un nombre de modelo es un dato del
    /// proveedor con fecha de caducidad y no puede vivir como constante. Vacío significa
    /// «pregúntaselo al runtime», y de eso se encarga <c>ModelResolver</c> en el primer lanzamiento.
    /// </para>
    /// </summary>
    public string CopilotModel { get; set; } = string.Empty;

    // ---- Aviso de versión nueva (F8 §3) ----

    /// <summary>
    /// Cuándo se preguntó por última vez a GitHub por la última Release. Se comprueba en cada
    /// arranque, con un suelo anti-bucle de 15 minutos (<c>UpdateCheckService.MinimumInterval</c>):
    /// reiniciar tras publicar una release basta para ver el aviso, y abrir y cerrar diez veces
    /// seguidas no dispara diez consultas.
    /// <para>
    /// Solo se sella tras una consulta que SALIÓ BIEN. Si falla —sin red, sin permisos, API
    /// caída—, no se sella: quien arrancó offline no tiene por qué quedarse sin enterarse hasta
    /// que venza nada. Y no puede degenerar en machaqueo porque la consulta se hace una vez por
    /// arranque, no en bucle; el re-chequeo de las instancias que llevan abiertas cuenta desde su
    /// último INTENTO, y va cada 24 h.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// El tag de la última Release que se llegó a ver (<c>v1.2.3</c>). Se guarda para que el
    /// banner se pueda pintar en los arranques en los que el chequeo no toca: sin esto, la versión
    /// nueva desaparecería de la vista y volvería sola, que es justo el tipo de intermitencia que
    /// hace desconfiar de un aviso.
    /// </summary>
    public string? LastSeenReleaseTag { get; set; }

    /// <summary>La página de esa Release, para que «Ver novedades» funcione sin volver a preguntar.</summary>
    public string? LastSeenReleaseUrl { get; set; }

    /// <summary>
    /// La versión que el usuario descartó. No vuelve a molestar con ESA; con la siguiente sí.
    /// Descartar es «ya me he enterado», no «no me avises nunca más»: un interruptor permanente
    /// para un aviso que aparece una vez por versión sería más ajuste del que la función merece.
    /// </summary>
    public string? DismissedUpdateVersion { get; set; }
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/>, protecting the PAT with Windows DPAPI (§3 — no
/// custom secret store). Reuses the git global identity as a fallback (§3).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsService(AppPaths paths) => _path = paths.SettingsJson;

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                          ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings();
            }
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// Vuelve a los valores de fábrica: borra <c>settings.json</c> y deja
    /// <see cref="Current"/> en sus valores por defecto (F5.7 §5).
    /// <para>
    /// Las dos mitades importan. Borrar solo el fichero dejaría los ajustes viejos vivos en
    /// memoria, y el primer <see cref="Save"/> —lo hace hasta la migración de conexión— los
    /// volvería a escribir; poner solo <see cref="Current"/> a nuevo dejaría el fichero en disco
    /// para el siguiente arranque. Con el PAT pasa lo mismo: vive DENTRO de este fichero, así que
    /// borrarlo es también lo que se lleva la credencial de respaldo.
    /// </para>
    /// </summary>
    public void ResetToDefaults()
    {
        Current = new AppSettings();
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best-effort: lo que manda es el estado en memoria, que ya es el de fábrica.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// One-time, silent migration of pre-F2 settings (D4). Users who already had a hub URL and a
    /// PAT must keep working untouched: their PAT stays (it is now the hidden fallback), and their
    /// hub URL is kept as an advanced override **only when it differs** from the deployment's —
    /// otherwise it is simply dropped, so those users follow the deployment from now on.
    /// </summary>
    public void MigrateConnection(DeployConfig deploy)
    {
        if (Current.ConnectionMigrated)
        {
            return;
        }

        string? legacy = Current.HubRepoUrl;
        if (!string.IsNullOrWhiteSpace(legacy)
            && !SameRepo(legacy, deploy.HubUrl)
            && string.IsNullOrWhiteSpace(Current.HubUrlOverride))
        {
            Current.HubUrlOverride = legacy!.Trim();
        }

        Current.HubRepoUrl = null;
        Current.ConnectionMigrated = true;
        Save(Current);
    }

    /// <summary>
    /// Promoción única del interruptor del arreglo asistido (F6.9, D-563).
    /// <para>
    /// El flag nació en v1 sin valor por defecto —es decir, <c>false</c>— conectado a nada
    /// (D-275), y <see cref="Save"/> escribe TODAS las propiedades: cualquier máquina que guardara
    /// ajustes antes de F6.9 tiene un <c>"enableAssistedFix": false</c> escrito con todas las
    /// letras. Cambiar el valor por defecto de la propiedad a <c>true</c> no alcanza a esas
    /// máquinas: el defecto solo se aplica cuando la clave FALTA, y ahí no falta. Por eso el
    /// botón «Arreglar con agente» no apareció el día de la entrega.
    /// </para>
    /// <para>
    /// Se promociona una sola vez y se deja constancia. A partir de esa vez, apagar el
    /// interruptor en Ajustes es una decisión del usuario y se respeta para siempre.
    /// </para>
    /// </summary>
    public void MigrateAssistedFixDefault()
    {
        if (Current.AssistedFixDefaultApplied)
        {
            return;
        }

        Current.AssistedFixDefaultApplied = true;
        Current.EnableAssistedFix = true;
        Save(Current);
    }

    /// <summary>Compares two remote URLs ignoring case, a trailing slash and a trailing ".git".</summary>
    internal static bool SameRepo(string? a, string? b)
    {
        static string Normalize(string? url) => (url ?? string.Empty)
            .Trim()
            .TrimEnd('/')
            .TrimEnd()
            is var u && u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? u[..^4].TrimEnd('/')
                : u;

        string na = Normalize(a);
        string nb = Normalize(b);
        return na.Length > 0 && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Encrypts and stores a PAT with DPAPI (current-user scope).</summary>
    public void SetPat(string? plainTextPat)
    {
        if (string.IsNullOrEmpty(plainTextPat))
        {
            Current.ProtectedPat = null;
        }
        else
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainTextPat), null, DataProtectionScope.CurrentUser);
            Current.ProtectedPat = Convert.ToBase64String(cipher);
        }

        Save(Current);
    }

    /// <summary>Decrypts the stored PAT, or null if none / undecryptable.</summary>
    public string? GetPat()
    {
        if (string.IsNullOrEmpty(Current.ProtectedPat))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(Current.ProtectedPat), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
