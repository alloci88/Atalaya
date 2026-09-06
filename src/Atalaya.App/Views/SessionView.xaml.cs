using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class SessionView : UserControl
{
    public SessionView() => InitializeComponent();

    /// <summary>
    /// Autoscroll que respeta al usuario (F5.2, Hito 2): la conversación se mantiene al final
    /// mientras nadie la toque, y se PAUSA en cuanto alguien sube a leer — nada peor que perder el
    /// sitio cada vez que el agente escribe una línea. El botón «Volver al final» lo reanuda.
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

    /// <summary>
    /// <b>Pulsar una unidad de la cola lleva a su sección del hilo</b> (F30 §3).
    /// <para>
    /// Con el árbol de expanders retirado, el hilo es una sola columna que puede medir varias
    /// pantallas; la columna de la izquierda es el índice, y un índice que no lleva a ninguna parte
    /// es una lista. Se abre su tramo —saltar a una sección plegada dejaría al usuario mirando el
    /// separador de otra— y se suelta el autoscroll: quien salta a mirar algo no quiere que la
    /// entrada siguiente le devuelva al final.
    /// </para>
    /// <para>
    /// Va en diferido, en <c>Loaded</c>: abrir el tramo cambia la altura del contenido, y un
    /// <c>BringIntoView</c> sobre una geometría que todavía no se ha vuelto a colocar apunta al
    /// sitio de antes.
    /// </para>
    /// </summary>
    private void OnUnitPicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UnitProgress unit })
        {
            return;
        }

        unit.IsExpanded = true;
        if (DataContext is SessionViewModel vm)
        {
            vm.AutoScroll = false;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (ActivityUnits.ItemContainerGenerator.ContainerFromItem(unit) is FrameworkElement section)
                {
                    section.BringIntoView();
                }
            }));
    }
}
