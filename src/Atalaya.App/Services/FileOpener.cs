using System.Diagnostics;

namespace Atalaya.App.Services;

/// <summary>
/// Abrir un fichero con la aplicación que el sistema le asocie.
/// <para>
/// Existe como costura por la razón de siempre en esta aplicación (D-260, D-287, D-304): un
/// <c>Process.Start</c> suelto dentro de un view-model convierte «al pulsar aquí se abre el
/// informe» en algo que solo se puede comprobar abriendo una ventana.
/// </para>
/// </summary>
public interface IFileOpener
{
    /// <summary>Abre el fichero. Devuelve false si no existe o si el sistema no pudo abrirlo.</summary>
    bool Open(string path);
}

/// <inheritdoc cref="IFileOpener"/>
public sealed class ShellFileOpener : IFileOpener
{
    public bool Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
