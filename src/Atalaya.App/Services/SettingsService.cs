using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Machine-local application settings (§8 Ajustes). Never stored in the hub.</summary>
public sealed class AppSettings
{
    public string? GitUserName { get; set; }
    public string? GitUserEmail { get; set; }
    public string? HubRepoUrl { get; set; }

    /// <summary>DPAPI-protected Personal Access Token (base64). Decrypt via the service.</summary>
    public string? ProtectedPat { get; set; }

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
