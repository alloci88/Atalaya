namespace Atalaya.App.Services;

/// <summary>Well-known local paths under <c>%LOCALAPPDATA%/Atalaya</c> (§1, §2, §4).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atalaya");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The audit-hub clone (§2).</summary>
    public string Hub => Path.Combine(Root, "hub");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Copilot SDK base directory (§6.1).</summary>
    public string Copilot => Path.Combine(Root, "copilot");

    /// <summary>Machine-local config: clone paths per app (§4).</summary>
    public string MachinesJson => Path.Combine(Root, "machines.json");

    /// <summary>Machine-local settings: preferences and thresholds (never secrets, never the hub).</summary>
    public string SettingsJson => Path.Combine(Root, "settings.json");

    /// <summary>The connected GitHub account, DPAPI-encrypted (D2.2).</summary>
    public string AuthDat => Path.Combine(Root, "auth.dat");

    /// <summary>
    /// Copias de seguridad de las sesiones de arreglo asistido (F6.9). Fuera del clon a propósito:
    /// lo que hace posible descartar no puede ser, a su vez, un cambio sin commitear en el clon.
    /// </summary>
    public string Fixes => Path.Combine(Root, "fixes");

    /// <summary>
    /// Líneas base de compilación por (objetivo, commit) (H9.1). Fuera del clon por lo mismo que
    /// los snapshots: lo que sirve para juzgar el clon no puede vivir dentro de él.
    /// </summary>
    public string BuildBaselines => Path.Combine(Root, "builds");
}
