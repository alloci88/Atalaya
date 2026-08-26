using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// La gestión de reglas excluidas de una aplicación (F5.10). Toda la lógica —qué se lista, qué
/// dice cada estado, des-excluir y editar caducidad— vive en <see cref="RuleExclusionsViewModel"/>;
/// aquí solo se enlaza y se cierra.
/// </summary>
public partial class RuleExclusionsDialog : FluentWindow
{
    public RuleExclusionsDialog(RuleExclusionsViewModel viewModel)
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
public interface IRuleExclusionsDialog
{
    /// <summary>Muestra la gestión de la app y devuelve el mismo view-model, ya usado.</summary>
    RuleExclusionsViewModel Show(RuleExclusionsViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="RuleExclusionsDialog"/> como modal.</summary>
public sealed class RuleExclusionsDialogHost : IRuleExclusionsDialog
{
    public RuleExclusionsViewModel Show(RuleExclusionsViewModel viewModel)
    {
        var dialog = new RuleExclusionsDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
