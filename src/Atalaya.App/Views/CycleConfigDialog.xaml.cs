using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// «Configurar ciclo» (F17 §4). Toda la regla vive en <see cref="CycleConfigViewModel"/>; aquí
/// solo se enlaza y se devuelve el sí o el no.
/// </summary>
public partial class CycleConfigDialog : FluentWindow
{
    public CycleConfigDialog(CycleConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => ApplyButton.Focus();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ((CycleConfigViewModel)DataContext).Accepted = true;
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
/// Quién abre el diálogo. Se inyecta por lo mismo que los demás: el inventario, la carcasa y el
/// alta lo piden en tres momentos distintos y ninguno puede depender de que haya una ventana.
/// </summary>
public interface ICycleConfigDialog
{
    /// <summary>True si el usuario aplicó; el view-model trae entonces la configuración elegida.</summary>
    bool Show(CycleConfigViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="CycleConfigDialog"/> como modal.</summary>
public sealed class CycleConfigDialogHost : ICycleConfigDialog
{
    public bool Show(CycleConfigViewModel viewModel)
    {
        var dialog = new CycleConfigDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true && viewModel.Accepted;
    }
}
