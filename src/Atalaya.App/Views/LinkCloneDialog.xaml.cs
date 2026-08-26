using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// El diálogo modal de «Vincular clon local» (F5.8 §2). Todo el flujo —los dos caminos, la
/// validación del remoto, el progreso del clonado y la oferta de re-escanear— vive en
/// <see cref="LinkCloneViewModel"/>; aquí solo se enlaza y se cierra.
/// </summary>
public partial class LinkCloneDialog : FluentWindow
{
    public LinkCloneDialog(LinkCloneViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Quién abre el diálogo. Se inyecta por la misma razón que <see cref="IDeleteAppConfirmer"/>:
/// el portafolio y el inventario piden vincular, y ninguno de los dos puede depender de que haya
/// una ventana para poder probarse.
/// </summary>
public interface ILinkCloneDialog
{
    /// <summary>Muestra el diálogo y devuelve el mismo view-model, ya con su desenlace.</summary>
    LinkCloneViewModel Show(LinkCloneViewModel viewModel);
}

/// <summary>La implementación real: abre <see cref="LinkCloneDialog"/> como modal.</summary>
public sealed class LinkCloneDialogHost : ILinkCloneDialog
{
    public LinkCloneViewModel Show(LinkCloneViewModel viewModel)
    {
        var dialog = new LinkCloneDialog(viewModel);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return viewModel;
    }
}
