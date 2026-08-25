using System.Windows.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class SessionView : UserControl
{
    public SessionView() => InitializeComponent();

    /// <summary>
    /// Autoscroll que respeta al usuario (F5.2, Hito 2): la columna de actividad se mantiene al
    /// final mientras nadie la toque, y se PAUSA en cuanto alguien sube a leer — nada peor que
    /// perder el sitio cada vez que el agente escribe una línea. El botón «Volver al final» lo
    /// reanuda.
    /// </summary>
    private void OnActivityScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || DataContext is not SessionViewModel vm)
        {
            return;
        }

        // Un cambio de altura del contenido no es un gesto del usuario: solo se reinterpreta la
        // intención cuando el desplazamiento cambia sin que crezca el contenido.
        bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
        if (Math.Abs(e.ExtentHeightChange) > 0.5)
        {
            if (vm.AutoScroll)
            {
                viewer.ScrollToEnd();
            }

            return;
        }

        if (Math.Abs(e.VerticalChange) > 0.5)
        {
            vm.AutoScroll = atBottom;
        }
    }
}
