using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// La política de tamaño de una aplicación (F13). Toda la lógica —leer, validar mínimos, guardar y
/// publicar— vive en <see cref="ThresholdsViewModel"/> y en <c>ThresholdPolicyService</c>; aquí
/// solo se enlaza y se cierra.
/// </summary>
public partial class ThresholdsDialog : AtalayaDialog
{
    public ThresholdsDialog(ThresholdsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre los umbrales. Se inyecta por la misma razón que <see cref="IDirectivesDialog"/>: el
/// inventario los ofrece y no puede depender de que haya una ventana para poder probarse.
/// </summary>
public interface IThresholdsDialog
{
    /// <summary>Muestra la política y devuelve el mismo view-model, ya usado.</summary>
    ThresholdsViewModel Show(ThresholdsViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="ThresholdsDialog"/> como modal.</summary>
public sealed class ThresholdsDialogHost : IThresholdsDialog
{
    public ThresholdsViewModel Show(ThresholdsViewModel viewModel)
    {
        var dialog = new ThresholdsDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
