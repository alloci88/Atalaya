using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Atalaya.App.Controls;

/// <summary>
/// UN SOLO CONTRATO PARA LO QUE FLOTA (P-03, UI-0001 y UI-0003).
/// <para>
/// <b>De dónde viene.</b> La auditoría midió el recorrido de teclado sobre el <c>dist</c> y
/// encontró dos superposiciones que se comportaban distinto porque cada una la escribió su vista.
/// El menú «···» de una tarjeta del Portafolio: abierto con el teclado, el foco se quedaba en el
/// botón, el siguiente Tab saltaba a «Abrir inventario» —que está <b>detrás</b> del menú abierto— y
/// su única entrada, «Eliminar la aplicación…», no recibía el foco nunca; era la única vía para
/// borrar una aplicación y no existía sin ratón. Y el cajón del resumen del ciclo del Inventario:
/// al abrirlo el foco se quedaba fuera, Tab seguía recorriendo la página que el cajón tapa
/// —buscador, filtro, «Auditar selección», y luego las novecientas filas de módulo— y Escape no lo
/// cerraba; sus seis controles estaban marcados como enfocables y no había ninguna secuencia de
/// Tab que llegara a ellos.
/// </para>
/// <para>
/// <b>Por qué un comportamiento y no dos arreglos.</b> Las dos son la misma pregunta —«¿dónde está
/// el foco mientras esto tapa lo de abajo?»— y las dos se contestaban por omisión. Un tercer panel
/// flotante volvería a contestarla por omisión. El contrato es uno: <b>al abrirse toma el foco, lo
/// retiene mientras está abierto, Escape lo cierra, y al cerrarse lo devuelve a quien lo abrió.</b>
/// Es el argumento de D-946 y D-951 —dos números que se mantienen a mano acaban siendo
/// distintos— aplicado al comportamiento en vez de a las medidas.
/// </para>
/// <para>
/// <b>Cómo se usa.</b> Se cuelga del elemento que ES la superposición —el <c>Border</c> del menú,
/// la tarjeta del cajón— y se le enlaza <see cref="IsOpenProperty"/> a lo que ya decide si está
/// abierto. Para cerrar acepta las dos formas que existen en la casa: un comando
/// (<see cref="CloseCommandProperty"/>, el cajón) o el <c>ToggleButton</c> que lo abrió
/// (<see cref="CloseToggleProperty"/>, el menú «···»).
/// </para>
/// </summary>
public static class Overlay
{
    /// <summary>Está abierta. Enlazar a lo que ya lo decide; no lo decide esto.</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen",
        typeof(bool),
        typeof(Overlay),
        new PropertyMetadata(false, OnIsOpenChanged));

    /// <summary>Lo que Escape ejecuta para cerrarla.</summary>
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.RegisterAttached(
        "CloseCommand", typeof(ICommand), typeof(Overlay), new PropertyMetadata(null));

    /// <summary>
    /// El interruptor que la abrió, cuando no hay comando: Escape lo desmarca. Es lo que necesita
    /// un desplegable de tarjeta, cuyo «abierto» es estado de la vista y no del view-model.
    /// </summary>
    public static readonly DependencyProperty CloseToggleProperty = DependencyProperty.RegisterAttached(
        "CloseToggle", typeof(ToggleButton), typeof(Overlay), new PropertyMetadata(null));

    /// <summary>A quién se le devuelve el foco al cerrar. Lo guarda esto, no lo pone quien usa.</summary>
    private static readonly DependencyProperty ReturnToProperty = DependencyProperty.RegisterAttached(
        "ReturnTo", typeof(IInputElement), typeof(Overlay), new PropertyMetadata(null));

    public static bool GetIsOpen(DependencyObject d) => (bool)d.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject d, bool value) => d.SetValue(IsOpenProperty, value);

    public static ICommand? GetCloseCommand(DependencyObject d) => (ICommand?)d.GetValue(CloseCommandProperty);

    public static void SetCloseCommand(DependencyObject d, ICommand? value) => d.SetValue(CloseCommandProperty, value);

    public static ToggleButton? GetCloseToggle(DependencyObject d) => (ToggleButton?)d.GetValue(CloseToggleProperty);

    public static void SetCloseToggle(DependencyObject d, ToggleButton? value) => d.SetValue(CloseToggleProperty, value);

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            Open(element);
        }
        else if (e.OldValue is true)
        {
            Close(element);
        }
    }

    private static void Open(FrameworkElement element)
    {
        element.SetValue(ReturnToProperty, Keyboard.FocusedElement);

        // EL FOCO SE QUEDA DENTRO. `Cycle` hace que el último Tab vuelva al primero en vez de
        // salir a la página que la superposición está tapando, que es literalmente lo que pasaba:
        // el recorrido seguía por controles que el usuario no puede ver.
        KeyboardNavigation.SetTabNavigation(element, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(element, KeyboardNavigationMode.Cycle);

        element.PreviewKeyDown -= OnKeyDown;
        element.PreviewKeyDown += OnKeyDown;

        // Al fondo de la cola: cuando `IsOpen` cambia, el `Popup` todavía no ha montado su árbol y
        // el cajón todavía no es visible, así que `MoveFocus` no encontraría a nadie. `Input` es la
        // prioridad en la que la superposición ya está pintada y todavía no ha llegado ninguna
        // tecla.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusFirst(element)));
    }

    private static void FocusFirst(FrameworkElement element)
    {
        if (!GetIsOpen(element))
        {
            return;
        }

        if (element.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            return;
        }

        // Sin un hijo enfocable, el foco va a la propia superposición: peor sería dejarlo detrás,
        // donde Tab seguiría recorriendo lo que está tapado.
        element.Focusable = true;
        element.Focus();
    }

    private static void Close(FrameworkElement element)
    {
        element.PreviewKeyDown -= OnKeyDown;

        if (element.GetValue(ReturnToProperty) is IInputElement previous)
        {
            element.SetValue(ReturnToProperty, null);
            Keyboard.Focus(previous);
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement element)
        {
            return;
        }

        // El foco se devuelve ANTES de cerrar: al cerrarse, el árbol de la superposición
        // desaparece y con él el elemento que tiene el foco, y WPF lo deja en la ventana.
        Close(element);

        if (GetCloseCommand(element) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
            return;
        }

        if (GetCloseToggle(element) is { } toggle)
        {
            toggle.IsChecked = false;
            e.Handled = true;
        }
    }
}
