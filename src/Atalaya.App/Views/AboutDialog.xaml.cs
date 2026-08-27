using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// El «Acerca de» (F6.4 §3): icono, nombre, versión real, organización con su logotipo y los dos
/// enlaces. Es el único sitio donde la identidad puede lucirse sin estorbar a nadie.
/// </summary>
public partial class AboutDialog : FluentWindow
{
    public AboutDialog(AboutInfo info)
    {
        InitializeComponent();
        DataContext = info;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién enseña el diálogo. Mismo patrón que <c>IFactoryResetConfirmer</c>: <c>SettingsViewModel</c>
/// no depende de una ventana, así que el gesto se puede probar sin abrir nada.
/// </summary>
public interface IAboutDialog
{
    void Show(AboutInfo info);
}

/// <summary>La implementación real: abre <see cref="AboutDialog"/> como modal.</summary>
public sealed class AboutDialogHost : IAboutDialog
{
    public void Show(AboutInfo info)
    {
        var dialog = new AboutDialog(info);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }
}
