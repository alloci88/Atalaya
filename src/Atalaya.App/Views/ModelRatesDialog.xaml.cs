using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// Las tarifas por modelo de la organización (F15). Toda la lógica —sembrar, validar y guardar en
/// el hub— vive en <see cref="ModelRatesViewModel"/>; aquí solo se enlaza y se cierra.
/// </summary>
public partial class ModelRatesDialog : FluentWindow
{
    public ModelRatesDialog(ModelRatesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre las tarifas. Se inyecta por la misma razón que <see cref="IThresholdsDialog"/>:
/// Métricas las ofrece y no puede depender de que haya una ventana para poder probarse.
/// </summary>
public interface IModelRatesDialog
{
    /// <summary>Muestra las tarifas y devuelve el mismo view-model, ya usado.</summary>
    ModelRatesViewModel Show(ModelRatesViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="ModelRatesDialog"/> como modal.</summary>
public sealed class ModelRatesDialogHost : IModelRatesDialog
{
    public ModelRatesViewModel Show(ModelRatesViewModel viewModel)
    {
        var dialog = new ModelRatesDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
