using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// El diálogo modal del reset de fábrica (F5.7 §5). Toda la regla —cuándo se habilita el botón
/// rojo— vive en <see cref="FactoryResetConfirmation"/>; aquí solo se enlaza y se devuelve el sí
/// o el no.
/// </summary>
public partial class FactoryResetDialog : AtalayaDialog
{
    public FactoryResetDialog(FactoryResetConfirmation confirmation)
    {
        InitializeComponent();
        DataContext = confirmation;
        Loaded += (_, _) => WordBox.Focus();
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
/// Quién pregunta. Existe para que <c>SettingsViewModel</c> no dependa de una ventana: los tests
/// sustituyen esta pieza y ejercitan el flujo entero —incluido «cancelar»— sin interfaz gráfica.
/// Mismo patrón que <see cref="IDeleteAppConfirmer"/>.
/// </summary>
public interface IFactoryResetConfirmer
{
    /// <summary>True si el usuario confirmó habiendo escrito la palabra.</summary>
    bool Confirm(FactoryResetConfirmation confirmation);
}

/// <summary>La implementación real: abre <see cref="FactoryResetDialog"/> como modal.</summary>
public sealed class FactoryResetDialogConfirmer : IFactoryResetConfirmer
{
    public bool Confirm(FactoryResetConfirmation confirmation)
    {
        var dialog = new FactoryResetDialog(confirmation);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
