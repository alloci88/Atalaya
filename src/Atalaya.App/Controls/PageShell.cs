using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// UN PATRÓN DE PÁGINA, ESCRITO Y COMPROBABLE (P-02, UI-0035, UI-0056).
/// <para>
/// <b>De dónde viene.</b> No había un sitio donde estuviera escrito dónde empieza una página, y se
/// notaba en dos cosas a la vez. Medido sobre la fila del título, todas las vistas arrancaban en
/// <b>x = 264–266</b> —Portafolio, Hallazgos, Informes, Métricas, Inventario, la ficha, Ajustes—
/// menos dos: <b>Nueva aplicación en x = 633 y Cuenta en x = 800</b>, porque las dos centraban su
/// columna entera. Pasar de Portafolio a Cuenta movía el contenido 536 px a la derecha y dejaba la
/// miga —que sigue en x = 311, porque es de la carcasa— casi quinientos píxeles a la izquierda de
/// su propio título. Y en Informes el recuento («8 informes») vivía en el extremo derecho, a 1.600
/// px del título que cuenta, mientras en Hallazgos y en Portafolio el mismo dato va bajo el título:
/// tres listas hermanas, dos sitios para el mismo dato, y el que está solo es el que hay que ir a
/// buscar al otro extremo.
/// </para>
/// <para>
/// <b>La regla, y es una sola.</b> Todo —el título, su línea de subtítulo, el recuento, las
/// acciones de vista y el cuerpo— arranca <b>en el margen de la página</b>. Lo que una vista puede
/// declarar es el <b>techo</b> de su cuerpo (<see cref="BodyWidth"/>), que es otra cosa: un
/// formulario no debe estirarse hasta 1.900 px, pero tampoco tiene por qué irse al centro dejando
/// medio lienzo en blanco a su izquierda y su propio título quinientos píxeles más allá.
/// </para>
/// <para>
/// <b>Por qué un control y no una convención.</b> Porque una convención se copia mal: las dos
/// vistas que se salían de la cuadrícula lo hacían imitando a «Acerca de», que sí es una excepción
/// declarada —una tarjeta de 640 centrada en los dos ejes, D-999 §6—. Con el patrón escrito, la
/// próxima vista nace alineada y la excepción se declara en vez de imitarse mal.
/// </para>
/// </summary>
/// <remarks>
/// El cuerpo es el <c>Content</c>; las acciones de vista, si las hay, van en
/// <see cref="ActionsProperty"/>. El título y el recuento son propiedades y no hijos: son texto, y
/// tienen un solo sitio.
/// </remarks>
public sealed class PageShell : ContentControl, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>El nombre de la página. Es el mismo que dicen la miga y la entrada del raíl.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageShell),
        new FrameworkPropertyMetadata(string.Empty, OnLeadChanged));

    /// <summary>Una línea que dice de qué va la página. Bajo el título, no al lado.</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageShell),
        new FrameworkPropertyMetadata(string.Empty, OnLeadChanged));

    /// <summary>
    /// El recuento de la página: «8 informes», «111 hallazgos». VA BAJO EL TÍTULO porque es del
    /// título — cuenta lo que el título nombra (UI-0056).
    /// </summary>
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(string), typeof(PageShell),
        new FrameworkPropertyMetadata(string.Empty, OnLeadChanged));

    /// <summary>Las acciones de VISTA, a la derecha del título. Vacío es lo normal.</summary>
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageShell), new FrameworkPropertyMetadata(null));

    /// <summary>
    /// El TECHO del cuerpo. <c>PositiveInfinity</c> —lo normal— es «todo el ancho de la página»;
    /// un número lo para ahí. Nunca lo centra: el cuerpo empieza donde empieza el título, que es
    /// lo que hace que pasar de una vista a otra no mueva nada.
    /// </summary>
    public static readonly DependencyProperty BodyWidthProperty = DependencyProperty.Register(
        nameof(BodyWidth),
        typeof(double),
        typeof(PageShell),
        new FrameworkPropertyMetadata(
            double.PositiveInfinity,
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string Count
    {
        get => (string)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public double BodyWidth
    {
        get => (double)GetValue(BodyWidthProperty);
        set => SetValue(BodyWidthProperty, value);
    }

    /// <summary>
    /// La línea que se lee bajo el título: el recuento, el subtítulo, o los dos separados por su
    /// punto. Se compone aquí y no en cada vista para que el orden sea siempre el mismo.
    /// </summary>
    public string Lead => (Count, Subtitle) switch
    {
        ({ Length: > 0 }, { Length: > 0 }) => $"{Count} · {Subtitle}",
        ({ Length: > 0 }, _) => Count,
        _ => Subtitle,
    };

    /// <summary>Hay una línea bajo el título. Sin ella, el título no arrastra un hueco vacío.</summary>
    public bool HasLead => Lead.Length > 0;

    private static void OnLeadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PageShell)d;
        self.OnPropertyChangedName(nameof(Lead));
        self.OnPropertyChangedName(nameof(HasLead));
    }

    /// <summary>
    /// Avisa de que una propiedad DERIVADA ha cambiado. Un <c>ContentControl</c> no implementa
    /// <c>INotifyPropertyChanged</c>, así que lo que la plantilla enlaza son propiedades CLR y hay
    /// que empujarlas a mano cuando cambia lo que las compone.
    /// </summary>
    private void OnPropertyChangedName(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <inheritdoc cref="OnPropertyChangedName"/>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
