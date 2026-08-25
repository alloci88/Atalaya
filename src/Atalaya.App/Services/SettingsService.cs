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

    /// <summary>Feature flag for the assisted-fix flow (§5.7, H9).</summary>
    public bool EnableAssistedFix { get; set; }

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
    /// solo guarda el id elegido. Vacío = el que el runtime decida por defecto.
    /// </summary>
    public string CopilotModel { get; set; } = "gpt-5";
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
