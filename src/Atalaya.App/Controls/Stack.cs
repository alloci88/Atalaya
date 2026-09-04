using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// <c>Stack.Gap</c>: la separación entre los hijos de un panel, declarada UNA vez en el padre
/// (F26 Parte A, D-951).
/// <para>
/// <b>El problema que resuelve.</b> WPF no tiene <c>gap</c>: la única forma de separar los hijos de
/// un <see cref="StackPanel"/> es poner un <c>Margin</c> en cada uno. Eso son, en Atalaya, unos
/// setecientos márgenes escritos a mano —y en cuanto hay dos, hay dos números distintos: en el
/// raíl los botones iban a <c>0,2</c> y en las cabeceras a <c>0,0,0,6</c>, sin que nadie lo
/// decidiera—. Con esto, el padre dice «mis hijos van separados por
/// <c>{StaticResource Space.S}</c>» y ya no hay dónde equivocarse.
/// </para>
/// <para>
/// <b>Cómo reparte.</b> El margen va en el hijo, del lado que mira al siguiente —abajo si el panel
/// es vertical, a la derecha si es horizontal—, y el ÚLTIMO no lleva ninguno: un hueco colgando al
/// final desalinea el panel contra lo que tenga debajo, que es de las cosas que se ven y no se
/// saben explicar. Se respeta el margen que un hijo ya traiga en los otros tres lados.
/// </para>
/// <para>
/// <b>Cuándo se aplica.</b> Al cargarse el panel, que es cuando sus hijos ya existen. Para listas
/// generadas (<c>ItemsControl</c>) la separación va en el <c>ItemContainerStyle</c>, porque ahí los
/// hijos aparecen y desaparecen y el «último» cambia con los datos.
/// </para>
/// </summary>
public static class Stack
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.RegisterAttached(
            "Gap",
            typeof(double),
            typeof(Stack),
            new PropertyMetadata(0d, OnGapChanged));

    public static void SetGap(DependencyObject element, double value) => element.SetValue(GapProperty, value);

    public static double GetGap(DependencyObject element) => (double)element.GetValue(GapProperty);

    private static void OnGapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel)
        {
            return;
        }

        panel.Loaded -= OnPanelLoaded;
        panel.Loaded += OnPanelLoaded;

        // Un panel que YA está cargado (le cambian el gap en caliente, o lo trae un estilo que se
        // aplica después) no vuelve a disparar Loaded. Se reparte ahora mismo.
        if (panel.IsLoaded)
        {
            Apply(panel);
        }
    }

    private static void OnPanelLoaded(object sender, RoutedEventArgs e) => Apply((Panel)sender);

    /// <summary>Reparte la separación entre los hijos visibles del panel.</summary>
    public static void Apply(Panel panel)
    {
        double gap = GetGap(panel);
        bool horizontal = panel is StackPanel { Orientation: Orientation.Horizontal }
            or VirtualizingStackPanel { Orientation: Orientation.Horizontal }
            or WrapPanel { Orientation: Orientation.Horizontal };

        // El último QUE SE VE, no el último de la colección: si el de abajo está plegado, el hueco
        // se quedaría flotando en el borde del panel.
        int last = -1;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is FrameworkElement { Visibility: not Visibility.Collapsed })
            {
                last = i;
            }
        }

        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement child)
            {
                continue;
            }

            double trailing = i == last ? 0 : gap;
            var m = child.Margin;
            child.Margin = horizontal
                ? new Thickness(m.Left, m.Top, trailing, m.Bottom)
                : new Thickness(m.Left, m.Top, m.Right, trailing);
        }
    }
}
