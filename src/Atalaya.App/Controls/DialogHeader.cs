using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Atalaya.App.Controls;

/// <summary>
/// <b>La cabecera de un diálogo de la casa</b> (R9): el título del diálogo y el botón de cerrar, en
/// la tipografía y los colores del sistema.
/// <para>
/// Sustituye a <c>ui:TitleBar</c>, que pintaba la barra de la librería —título pequeño, gris
/// propio— encima del título que el cuerpo ya decía: dos títulos, uno de ellos de otro programa.
/// Con esto hay uno, y es el de Atalaya.
/// </para>
/// <para>
/// Y es lo que se agarra para mover la ventana: sin la barra de Windows no queda otra cosa de la
/// que tirar.
/// </para>
/// </summary>
public sealed class DialogHeader : Control
{
    static DialogHeader()
        => DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DialogHeader), new FrameworkPropertyMetadata(typeof(DialogHeader)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DialogHeader), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Close") is Button close)
        {
            close.Click += (_, _) => Window.GetWindow(this)?.Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Doble clic no maximiza: un diálogo no se maximiza (por eso su ventana base apaga los dos
        // botones). Arrastrar, sí.
        if (e.ClickCount == 1 && Window.GetWindow(this) is Views.AtalayaDialog dialog)
        {
            dialog.DragFromHeader(e);
        }
    }
}
