using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// Reconciliar los costes de una aplicación (F29 §1). Toda la lógica —qué sesiones están sin
/// coste, qué se puede cerrar y qué se escribe— vive en <see cref="ReconcileCostsViewModel"/> y en
/// <c>CostReconciliationService</c>; aquí solo se enlaza y se cierra.
/// </summary>
public partial class ReconcileCostsDialog : FluentWindow
{
    public ReconcileCostsDialog(ReconcileCostsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre el diálogo de reconciliar. Se inyecta por la misma razón que
/// <see cref="IThresholdsDialog"/>: el inventario lo ofrece y no puede depender de que haya una
/// ventana para poder probarse.
/// </summary>
public interface IReconcileCostsDialog
{
    /// <summary>Muestra el diálogo y devuelve el mismo view-model, ya usado.</summary>
    ReconcileCostsViewModel Show(ReconcileCostsViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="ReconcileCostsDialog"/> como modal.</summary>
public sealed class ReconcileCostsDialogHost : IReconcileCostsDialog
{
    public ReconcileCostsViewModel Show(ReconcileCostsViewModel viewModel)
    {
        var dialog = new ReconcileCostsDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
