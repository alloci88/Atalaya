using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// La gestión de directivas del proyecto de una aplicación (F7 §1). Toda la lógica —qué se lista,
/// qué propone el catálogo, qué presupuesto consume lo activado, activar, ordenar, previsualizar y
/// añadir a mano— vive en <see cref="DirectivesViewModel"/>; aquí solo se enlaza y se cierra.
/// </summary>
public partial class DirectivesDialog : FluentWindow
{
    public DirectivesDialog(DirectivesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre la gestión de directivas. Se inyecta por lo mismo que
/// <see cref="IPatternSilencesDialog"/>: el inventario la pide y no puede depender de que haya una
/// ventana para poder probarse.
/// </summary>
public interface IDirectivesDialog
{
    /// <summary>Muestra la gestión de la app y devuelve el mismo view-model, ya usado.</summary>
    DirectivesViewModel Show(DirectivesViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="DirectivesDialog"/> como modal.</summary>
public sealed class DirectivesDialogHost : IDirectivesDialog
{
    public DirectivesViewModel Show(DirectivesViewModel viewModel)
    {
        var dialog = new DirectivesDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
