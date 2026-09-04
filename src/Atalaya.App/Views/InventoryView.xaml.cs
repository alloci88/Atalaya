using System.Windows;
using System.Windows.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class InventoryView : UserControl
{
    /// <summary>
    /// Por debajo de esto no caben la tabla y el resumen del ciclo a la vez (F26 §B, D-975): el
    /// árbol necesita unos 700 px para que una ruta de módulo no se parta, y el panel 320 para que
    /// su línea más larga quepa entera. Por debajo, el panel se pliega — reorganizar, no encoger.
    /// </summary>
    public const double SplitBelow = 1060;

    public InventoryView() => InitializeComponent();

    private void OnSplitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && DataContext is InventoryViewModel vm)
        {
            vm.Wide = e.NewSize.Width >= SplitBelow;
        }
    }
}
