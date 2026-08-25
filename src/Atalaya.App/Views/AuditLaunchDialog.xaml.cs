using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// El diálogo previo a gastar (F5.6 §4). Toda la regla —qué se enseña, cuándo se advierte de que
/// el dato es flojo— vive en <see cref="AuditLaunchConfirmation"/>; aquí solo se enlaza y se
/// devuelve el sí o el no.
/// <para>
/// El foco arranca en «Confirmar» porque el camino habitual es seguir: quien abre esto ya ha
/// elegido las unidades. Lo que el diálogo aporta no es fricción, es el número.
/// </para>
/// </summary>
public partial class AuditLaunchDialog : FluentWindow
{
    public AuditLaunchDialog(AuditLaunchConfirmation confirmation)
    {
        InitializeComponent();
        DataContext = confirmation;
        Loaded += (_, _) => ConfirmButton.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>La implementación real: abre <see cref="AuditLaunchDialog"/> como modal.</summary>
public sealed class AuditLaunchDialogConfirmer : IAuditLaunchConfirmer
{
    public bool Confirm(AuditLaunchConfirmation confirmation)
    {
        var dialog = new AuditLaunchDialog(confirmation);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
