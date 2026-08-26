using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// La gestión de patrones silenciados de una aplicación (F5.12). Toda la lógica —qué se lista, qué
/// dice cada estado, cuánto trabaja cada patrón, editar el ejemplar, des-silenciar y cambiar
/// caducidad— vive en <see cref="PatternSilencesViewModel"/>; aquí solo se enlaza y se cierra.
/// </summary>
public partial class PatternSilencesDialog : FluentWindow
{
    public PatternSilencesDialog(PatternSilencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre la gestión. Se inyecta por la misma razón que <see cref="ILinkCloneDialog"/>: el
/// inventario la pide y no puede depender de que haya una ventana para poder probarse.
/// </summary>
public interface IPatternSilencesDialog
{
    /// <summary>Muestra la gestión de la app y devuelve el mismo view-model, ya usado.</summary>
    PatternSilencesViewModel Show(PatternSilencesViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="PatternSilencesDialog"/> como modal.</summary>
public sealed class PatternSilencesDialogHost : IPatternSilencesDialog
{
    public PatternSilencesViewModel Show(PatternSilencesViewModel viewModel)
    {
        var dialog = new PatternSilencesDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
