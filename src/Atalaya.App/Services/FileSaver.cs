namespace Atalaya.App.Services;

/// <summary>
/// Quién pregunta dónde guardar un fichero (F6.3 §2).
/// <para>
/// Existe por la misma razón que <see cref="IFolderPicker"/> y <see cref="IFileOpener"/>: un
/// <c>SaveFileDialog</c> suelto dentro de un view-model convierte «descargar el informe con este
/// nombre» en algo que solo se puede comprobar abriendo una ventana — y el NOMBRE por defecto es
/// justo lo que hay que poder comprobar.
/// </para>
/// </summary>
public interface IFileSaver
{
    /// <summary>La ruta elegida, o null si se canceló.</summary>
    string? Pick(string title, string suggestedFileName, string filter);
}

/// <summary>El diálogo real: <c>Microsoft.Win32.SaveFileDialog</c>, de serie con WPF.</summary>
public sealed class SystemFileSaver : IFileSaver
{
    public string? Pick(string title, string suggestedFileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            Filter = filter,
            DefaultExt = Path.GetExtension(suggestedFileName),
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
