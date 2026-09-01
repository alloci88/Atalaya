using System.Runtime.InteropServices;

namespace Atalaya.ClaudeCode;

/// <summary>
/// Encuentra el CLI de <c>claude</c> en esta máquina (F14).
/// <para>
/// <b>Es el patrón inverso al de Copilot, y a propósito.</b> <c>CopilotCliLocator</c> resuelve
/// SIEMPRE el binario que el paquete del SDK empaqueta con la aplicación, y nunca uno del PATH:
/// allí el runtime es una dependencia de Atalaya. Aquí es al revés — Claude Code es un programa
/// del usuario, con SU suscripción y SU login, y Atalaya no lo empaqueta, no lo instala y no
/// guarda credenciales de Anthropic. Lo único que hace es encontrar el que ya está.
/// </para>
/// <para>
/// <b>La extensión importa en Windows, y se comprobó.</b> npm deja tres ficheros junto al nombre:
/// <c>claude</c> (script sh, sin extensión), <c>claude.ps1</c> y <c>claude.cmd</c>. De los tres,
/// el único que <c>Process.Start</c> sabe lanzar con <c>UseShellExecute=false</c> es el
/// <c>.cmd</c>; el que no tiene extensión falla con «no es una aplicación válida para esta
/// plataforma». Por eso se sondea por extensión y no por nombre a secas.
/// </para>
/// </summary>
public static class ClaudeCliLocator
{
    /// <summary>
    /// Los nombres a probar, en orden. En Windows el <c>.cmd</c> de npm primero y el <c>.exe</c>
    /// de la instalación nativa después; fuera de Windows, el nombre a secas.
    /// </summary>
    public static IReadOnlyList<string> CandidateNames
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "claude.cmd", "claude.exe", "claude.bat" }
            : new[] { "claude" };

    /// <summary>
    /// La ruta del CLI, o <c>null</c> si no está. Recorre el PATH a mano en vez de confiar en que
    /// <c>Process.Start</c> lo resuelva: para poder DECIR que no está —y decirlo antes de lanzar
    /// una sesión— hay que haber mirado, y un <c>Win32Exception</c> a mitad de una auditoría es
    /// justo el fallo mudo que no queremos.
    /// </summary>
    /// <param name="pathVariable">
    /// El PATH a recorrer. Se inyecta para poder probar el sondeo con un PATH de mentira, con y
    /// sin el CLI dentro, sin depender de lo que tenga instalada la máquina que corre los tests.
    /// </param>
    public static string? Resolve(string? pathVariable = null)
    {
        string path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string folder = directory.Trim().Trim('"');
            if (folder.Length == 0)
            {
                continue;
            }

            foreach (string name in CandidateNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder, name);
                }
                catch (ArgumentException)
                {
                    // Una entrada del PATH con caracteres imposibles no puede tumbar la búsqueda:
                    // se salta y se sigue mirando en las demás.
                    break;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
