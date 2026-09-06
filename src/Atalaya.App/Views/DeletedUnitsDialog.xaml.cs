using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// Los hallazgos que se quedaron sin código (F9 §4). Toda la lógica —qué se lista, con qué
/// evidencia, y la resolución por código eliminado— vive en <see cref="DeletedUnitsViewModel"/>;
/// aquí solo se enlaza y se cierra.
/// </summary>
public partial class DeletedUnitsDialog : AtalayaDialog
{
    public DeletedUnitsDialog(DeletedUnitsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre la lista. Se inyecta por la misma razón que <see cref="IPatternSilencesDialog"/>: el
/// inventario la pide y no puede depender de que haya una ventana para poder probarse.
/// </summary>
public interface IDeletedUnitsDialog
{
    /// <summary>Muestra la lista y devuelve el mismo view-model, ya usado.</summary>
    DeletedUnitsViewModel Show(DeletedUnitsViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="DeletedUnitsDialog"/> como modal.</summary>
public sealed class DeletedUnitsDialogHost : IDeletedUnitsDialog
{
    public DeletedUnitsViewModel Show(DeletedUnitsViewModel viewModel)
    {
        var dialog = new DeletedUnitsDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
