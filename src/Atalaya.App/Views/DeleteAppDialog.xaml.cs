using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// El diálogo modal del borrado de una aplicación (F5.3 §4). Toda la regla —cuándo se habilita el
/// botón rojo— vive en <see cref="DeleteAppConfirmation"/>; aquí solo se enlaza y se devuelve el
/// sí o el no.
/// </summary>
public partial class DeleteAppDialog : FluentWindow
{
    public DeleteAppDialog(DeleteAppConfirmation confirmation)
    {
        InitializeComponent();
        DataContext = confirmation;
        Loaded += (_, _) => NameBox.Focus();
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

/// <summary>
/// Quién pregunta. Existe para que <c>PortfolioViewModel</c> no dependa de una ventana: los tests
/// sustituyen esta pieza y ejercitan el flujo entero —incluido «cancelar»— sin interfaz gráfica.
/// </summary>
public interface IDeleteAppConfirmer
{
    /// <summary>True si el usuario confirmó habiendo escrito el nombre.</summary>
    bool Confirm(DeleteAppConfirmation confirmation);
}

/// <summary>La implementación real: abre <see cref="DeleteAppDialog"/> como modal.</summary>
public sealed class DeleteAppDialogConfirmer : IDeleteAppConfirmer
{
    public bool Confirm(DeleteAppConfirmation confirmation)
    {
        var dialog = new DeleteAppDialog(confirmation);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
