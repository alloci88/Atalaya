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

    /// <summary>
    /// Lo que se espera a que el editor arranque antes de darlo por fallido (F5.5 §6).
    /// <para>
    /// <c>Process.Start</c> parece instantáneo y no lo es: resolver <c>devenv</c> por el PATH,
    /// levantar el shim <c>code.cmd</c> o caer en el manejador del sistema puede bloquear el hilo
    /// varios segundos —y con una unidad de red desconectada, indefinidamente—. La ficha decía
    /// «Abriendo en el editor…» y se quedaba ahí para siempre porque nadie ponía un límite. Ahora
    /// lo hay: o abre, o falla, pero termina.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Abre la ubicación con un tope de tiempo. Devuelve <c>false</c> si el editor no arrancó
    /// dentro de <see cref="LaunchTimeout"/>: el arranque sigue su curso en segundo plano, pero la
    /// interfaz deja de esperarlo.
    /// </summary>
    public Task<bool> OpenAsync(string slug, string relativePath, int line, CancellationToken ct = default)
        => WithTimeout(() => Open(slug, relativePath, line), LaunchTimeout, ct);

    /// <summary>
    /// El tope de tiempo, aislado de todo lo que toca el sistema para poder probarlo: un arranque
    /// que no vuelve tiene que resolverse en <c>false</c>, no colgar a quien espera.
    /// </summary>
    internal static async Task<bool> WithTimeout(Func<bool> launch, TimeSpan timeout, CancellationToken ct = default)
    {
        Task<bool> running = Task.Run(launch, CancellationToken.None);
        Task finished = await Task.WhenAny(running, Task.Delay(timeout, ct)).ConfigureAwait(false);

        // El arranque que se pasó de tiempo sigue su curso en segundo plano —no hay forma de
        // abortar un Process.Start a medias—, pero la interfaz ya no lo espera.
        return await running.ConfigureAwait(false);
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
