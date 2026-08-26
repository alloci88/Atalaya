namespace Atalaya.App.Services;

/// <summary>
/// Quién enseña el selector de carpetas (F5.8 §2).
/// <para>
/// Existe por la misma razón que <c>IDeleteAppConfirmer</c>: el flujo de vincular —elegir carpeta,
/// validarla, ver el error, elegir otra— es exactamente lo que hay que poder probar, y no se
/// puede probar si abre un diálogo del sistema.
/// </para>
/// </summary>
public interface IFolderPicker
{
    /// <summary>La carpeta elegida, o null si se canceló.</summary>
    string? Pick(string title, string? initialDirectory = null);
}

/// <summary>
/// El selector real: <c>Microsoft.Win32.OpenFolderDialog</c>, que llega de serie con WPF en .NET 8
/// — sin arrastrar WinForms solo para pedir una carpeta.
/// </summary>
public sealed class SystemFolderPicker : IFolderPicker
{
    public string? Pick(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
