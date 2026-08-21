using System.Diagnostics;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>Opens a finding location in the configured editor (§8 V3): Visual Studio or VS Code.</summary>
public sealed class EditorLauncher
{
    private readonly SettingsService _settings;
    private readonly MachineConfigStore _machines;

    public EditorLauncher(SettingsService settings, MachineConfigStore machines)
    {
        _settings = settings;
        _machines = machines;
    }

    public bool Open(string slug, string relativePath, int line)
    {
        string? clone = _machines.Load().ClonePathFor(slug);
        if (string.IsNullOrWhiteSpace(clone))
        {
            return false;
        }

        string abs = Path.Combine(clone, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string editor = _settings.Current.Editor;

        try
        {
            if (string.Equals(editor, "vscode", StringComparison.OrdinalIgnoreCase))
            {
                // VS Code: -g file:line (§8).
                Start("code", $"-g \"{abs}:{line}\"");
            }
            else
            {
                // Visual Studio: open the file (best-effort; VS picks up the line via its own nav).
                Start("devenv", $"/edit \"{abs}\"");
            }

            return true;
        }
        catch
        {
            // Fall back to the OS default handler.
            try
            {
                Start(abs, string.Empty, shell: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void Start(string file, string args, bool shell = false)
        => Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = shell });
}
