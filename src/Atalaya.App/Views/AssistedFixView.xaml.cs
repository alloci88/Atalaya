using System.Windows.Controls;
using System.Windows.Input;
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
