using System.Windows;
using System.Windows.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class FindingsView : UserControl
{
    /// <summary>
    /// Por debajo de esto no hay sitio para lista y vista rápida a la vez (F26 §B, D-974): la lista
    /// necesita unos 620 px para que el título de un hallazgo no se parta en cuatro líneas, y el
    /// panel unos 460 para que su código se lea. Por debajo, el panel se va y la fila vuelve a
    /// abrir la ficha entera — reorganizar, no encoger.
    /// </summary>
    public const double SplitBelow = 1080;

    public FindingsView() => InitializeComponent();

    /// <summary>
    /// Lo decide el ancho DISPONIBLE, no el de la ventana: el raíl se pliega y se despliega, y con
    /// él cambia lo que le queda al contenido sin que la ventana se mueva.
    /// </summary>
    private void OnSplitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && DataContext is FindingsViewModel vm)
        {
            vm.Wide = e.NewSize.Width >= SplitBelow;
        }
    }
}
