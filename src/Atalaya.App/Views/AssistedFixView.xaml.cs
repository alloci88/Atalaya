using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Atalaya.App.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class AssistedFixView : UserControl
{
    public AssistedFixView() => InitializeComponent();

    /// <summary>
    /// Enter envía la orden al agente. El campo de entrada está siempre a mano y el gesto que se
    /// espera de él es el de cualquier chat: escribir y pulsar Enter. Obligar a apuntar al botón
    /// cada vez convierte «interrumpir» en algo que no se hace.
    /// </summary>
    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            return;
        }

        if (DataContext is AssistedFixViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// <b>Al cerrar, se aterriza (H9.1 §4).</b> Cuando el agente llama a <c>fix_done</c> aparece la
    /// pantalla de cierre, y lo que el usuario necesita en ese momento —el resumen y la sugerencia
    /// de commit— tiene que quedar delante sin que él pelee con la rueda. Dos gestos:
    /// <list type="number">
    /// <item>la pantalla de cierre se pone <b>arriba del todo</b>, se llegue como se llegue;</item>
    /// <item>la conversación se va <b>al final</b>, ignorando a propósito la pausa del autoscroll:
    /// esa pausa protege una lectura EN CURSO, y aquí la sesión ha terminado.</item>
    /// </list>
    /// Va por el aviso de visibilidad del panel —no por <c>PropertyChanged</c>— porque ese evento
    /// solo se levanta cuando la visibilidad CAMBIA de verdad: repintar la vista mil veces no
    /// vuelve a mover el scroll bajo los dedos del usuario.
    /// </summary>
    private void OnClosingShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        if (DataContext is AssistedFixViewModel vm)
        {
            vm.AutoScroll = true;
        }

        // En Loaded: al levantarse el aviso, el panel todavía no tiene medidas y un ScrollToTop
        // sobre un contenido sin extensión no hace nada.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                ClosingScroll.ScrollToTop();
                ConversationScroll.ScrollToEnd();
            }));
    }

    /// <summary>
    /// Un scroll interno deja de robarle la rueda a la pantalla de cierre (H9.1 §4).
    /// <para>
    /// Es el patrón de <see cref="SnippetScroll"/>, el mismo que arregló la ficha en F5.6: el
    /// contenedor interno se queda la rueda solo mientras pueda desplazarse en esa dirección, y en
    /// su tope la re-emite hacia el padre —que es lo que WPF habría hecho si el interno no la
    /// hubiera marcado como tratada—. Sin esto, la salida del build y la descripción del commit
    /// atrapaban la rueda y la tarjeta de commit quedaba fuera de alcance.
    /// </para>
    /// </summary>
    private void OnInnerScroll(object sender, MouseWheelEventArgs e)
    {
        (double offset, double viewport, double extent) = sender switch
        {
            ScrollViewer sv => (sv.VerticalOffset, sv.ViewportHeight, sv.ExtentHeight),
            TextBoxBase tb => (tb.VerticalOffset, tb.ViewportHeight, tb.ExtentHeight),
            _ => (0d, 0d, 0d),
        };

        if (sender is not UIElement element
            || !SnippetScroll.ShouldBubble(e.Delta, offset, viewport, extent))
        {
            return;
        }

        e.Handled = true;
        if (element is FrameworkElement { Parent: UIElement parent })
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = element,
            });
        }
    }

    /// <summary>
    /// El mismo autoscroll respetuoso de V5 (F5.2, Hito 2): se mantiene al final mientras nadie
    /// toque la conversación, y se pausa en cuanto alguien sube a leer.
    /// </summary>
    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || DataContext is not AssistedFixViewModel vm)
        {
            return;
        }

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
