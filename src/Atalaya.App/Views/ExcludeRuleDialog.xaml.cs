using System.Windows;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// La pregunta que acompaña a excluir una regla en toda una aplicación (F5.10): qué se hace con
/// los hallazgos que ya existen. Las tres salidas son botones distintos y ninguna es la de por
/// defecto — cerrar la ventana equivale a cancelar, que es lo único que no toca el hub.
/// </summary>
public partial class ExcludeRuleDialog : FluentWindow
{
    public ExcludeRuleDialog(ExcludeRuleConfirmation confirmation)
    {
        InitializeComponent();
        DataContext = confirmation;
    }

    /// <summary>Lo elegido. Cerrar con la X deja el valor inicial: cancelar.</summary>
    public ExcludeRuleChoice Choice { get; private set; } = ExcludeRuleChoice.Cancel;

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(ExcludeRuleChoice.Cancel);

    private void OnExcludeOnly(object sender, RoutedEventArgs e) => Finish(ExcludeRuleChoice.ExcludeOnly);

    private void OnExcludeAndSilence(object sender, RoutedEventArgs e) => Finish(ExcludeRuleChoice.ExcludeAndSilence);

    private void Finish(ExcludeRuleChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ExcludeRuleChoice.Cancel;
        Close();
    }
}

/// <summary>
/// Quién pregunta. Existe para que <c>FindingDetailViewModel</c> no dependa de una ventana: los
/// tests sustituyen esta pieza y ejercitan las tres salidas sin interfaz gráfica.
/// </summary>
public interface IExcludeRuleConfirmer
{
    ExcludeRuleChoice Ask(ExcludeRuleConfirmation confirmation);
}

/// <summary>La implementación real: abre <see cref="ExcludeRuleDialog"/> como modal.</summary>
public sealed class ExcludeRuleDialogConfirmer : IExcludeRuleConfirmer
{
    public ExcludeRuleChoice Ask(ExcludeRuleConfirmation confirmation)
    {
        var dialog = new ExcludeRuleDialog(confirmation);
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }
}
