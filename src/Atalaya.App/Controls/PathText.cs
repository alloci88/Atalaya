using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// UNA RUTA SE ACORTA POR EL MEDIO, y por el ancho que de verdad hay (P-01, UI-0009, UI-0011).
/// <para>
/// <b>De dónde viene.</b> A 1280 —que es también cualquier pantalla a 150 %— las cabeceras de
/// unidad de la sesión se cortaban por el final <b>sin puntos suspensivos</b> contra el galón de
/// desplegar: se leía <c>…/Servicios/FormateadorInfc⌄</c>, <c>…/Utilidades/Conversiones.c⌄</c>.
/// Nada decía que faltara texto: «FormateadorInfc» parece un nombre de fichero y «Conversiones.c»
/// parece un fichero de C. <b>Un texto recortado no falla: miente</b>, y es el fallo más caro de
/// esta interfaz precisamente porque no se nota.
/// </para>
/// <para>
/// <b>Por qué un control y no el recorte de WPF.</b> <c>TextTrimming</c> se come el final, que en
/// una ruta es lo único que identifica: el nombre del fichero. D-983 §5 ya lo arregló una vez en
/// la cabecera del bloque de código —acortando por el medio con un presupuesto de caracteres fijo—
/// y el arreglo no se generalizó: un presupuesto en caracteres no sabe a qué ancho está la ventana,
/// así que el mismo texto que cabía a 1920 volvía a mentir a 1280. Aquí el presupuesto se MIDE
/// contra el ancho que el contenedor da.
/// </para>
/// <para>
/// <b>Dónde se puede usar.</b> Donde el ancho lo impone el contenedor —una columna estrella, un
/// ancho fijo, un tope—. En una celda <c>Auto</c> el ancho depende del texto y el texto del ancho:
/// acortar daría más sitio, más sitio daría un texto más largo, y así. El guardarraíl de
/// <see cref="_lastWidth"/> corta esa pelota en el primer rebote, pero el sitio correcto es una
/// columna que mande.
/// </para>
/// </summary>
public sealed class PathText : TextBlock
{
    /// <summary>La ruta ENTERA. <c>Text</c> es lo que cabe de ella; esto es lo que dice.</summary>
    public static readonly DependencyProperty FullProperty = DependencyProperty.Register(
        nameof(Full),
        typeof(string),
        typeof(PathText),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnFullChanged));

    private double _lastWidth = -1;

    /// <summary>
    /// <c>TextBlock</c> tiene sellados <c>MeasureOverride</c>, <c>ArrangeOverride</c> y
    /// <c>OnPropertyChanged</c>, así que el aviso de que ha cambiado el tope se pide aquí: se
    /// reescriben los metadatos de <c>MaxWidth</c> para este tipo con su propio manejador. Es la
    /// vía que WPF deja para extender una propiedad heredada sin poder tocar sus métodos.
    /// </summary>
    static PathText() => MaxWidthProperty.OverrideMetadata(
        typeof(PathText),
        new FrameworkPropertyMetadata(
            double.PositiveInfinity,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((PathText)d).Reflow((double)e.NewValue)));

    public string Full
    {
        get => (string)GetValue(FullProperty);
        set => SetValue(FullProperty, value);
    }

    /// <summary>
    /// EL RECORTE SE DECIDE CONTRA UN ANCHO QUE NO DEPENDE DEL TEXTO, y ésa es toda la regla.
    /// <para>
    /// Si se midiera contra el tamaño que el control acaba ocupando, el texto acortado ocuparía
    /// menos, el siguiente pase daría un ancho menor, y el ciclo se iría comiendo la ruta hasta
    /// dejarla en nada. Hay dos anchos que no dependen del texto y aquí se usan los dos:
    /// </para>
    /// <list type="bullet">
    /// <item><b><c>MaxWidth</c>, si lo hay.</b> Es el caso de un contenedor que mide con ancho
    /// INFINITO —la cabecera de un <c>Expander</c>, un <c>StackPanel</c> horizontal—: ahí no hay
    /// nada contra lo que acortar y hace falta decirlo, normalmente como el ancho de la fila menos
    /// lo que la fila no puede usar.</item>
    /// <item><b>El ancho arreglado</b>, cuando el contenedor sí lo impone: una columna estrella o
    /// un ancho fijo dan el mismo número dure lo que dure el texto.</item>
    /// </list>
    /// <para>
    /// <c>TextBlock</c> tiene <c>MeasureOverride</c> y <c>ArrangeOverride</c> SELLADOS, así que el
    /// enganche son estos dos avisos y no la medida.
    /// </para>
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // Con `MaxWidth` puesto manda él: el ancho arreglado ya es el del texto acortado, y
        // volver a acortar contra él sería la pelota que no para de botar.
        if (double.IsInfinity(MaxWidth))
        {
            Reflow(info.NewSize.Width);
        }
    }

    private void Reflow(double available)
    {
        if (double.IsInfinity(available) || double.IsNaN(available)
            || Math.Abs(available - _lastWidth) <= 0.5)
        {
            return;
        }

        _lastWidth = available;
        string fitted = Fit(Full, available);
        if (!string.Equals(Text, fitted, StringComparison.Ordinal))
        {
            Text = fitted;
        }
    }

    private static void OnFullChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PathText)d;
        self._lastWidth = -1;
        self.Text = (string)e.NewValue ?? string.Empty;
        self.Reflow(self.MaxWidth);
    }

    /// <summary>
    /// La ruta que cabe en <paramref name="width"/>, conservando los dos extremos. El nombre del
    /// fichero manda: es lo que identifica, y no se toca mientras quepa.
    /// </summary>
    internal string Fit(string? full, double width)
    {
        string text = (full ?? string.Empty).Trim();
        if (text.Length == 0 || width <= 0 || double.IsInfinity(width) || Measure(text) <= width)
        {
            return text;
        }

        int slash = text.LastIndexOfAny(new[] { '/', '\\' });
        string tail = slash >= 0 ? text[slash..] : text;

        // Ni el nombre solo cabe: se acorta por delante, que es lo único que queda.
        if (Measure("…" + tail) > width)
        {
            string only = slash >= 0 ? text[(slash + 1)..] : text;
            for (int cut = 0; cut < only.Length; cut++)
            {
                string candidate = "…" + only[cut..];
                if (Measure(candidate) <= width)
                {
                    return candidate;
                }
            }

            return "…";
        }

        // Se come cabeza hasta que quepa. De uno en uno: una ruta larga son decenas de caracteres,
        // no miles, y así el resultado es el máximo que cabe y no «alguno que cabía».
        for (int head = slash; head >= 0; head--)
        {
            string candidate = text[..head] + "…" + tail;
            if (Measure(candidate) <= width)
            {
                return candidate;
            }
        }

        return "…" + tail;
    }

    private double Measure(string text) => new FormattedText(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
        FontSize,
        Brushes.Black,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
}

/// <summary>
/// UNA PASTILLA NO SE RECORTA: si no cabe entera, no se pinta (P-01, UI-0011).
/// <para>
/// <b>De dónde viene.</b> A 1280, la pastilla «Agente falso · claude-opus-4.7» de la cabecera del
/// arreglo se recortaba <b>a mitad de palabra y sin elipsis</b> hasta dejar solo <c>Age</c>; en la
/// pantalla de cierre, <c>Agente falso · clau</c>. El modelo con el que se está arreglando —el dato
/// que decide cuánto cuesta y quién juzga— desaparecía, y lo que quedaba no se podía interpretar.
/// </para>
/// <para>
/// <b>Por qué no se recorta y ya.</b> Una pastilla es una etiqueta: o dice lo que dice o no dice
/// nada. «Age» no es una versión corta de «Agente falso · claude-opus-4.7»; es una palabra
/// distinta. Retirarla deja el hueco vacío, que es honesto, y el dato sigue en el tooltip y en la
/// pantalla de cierre a ancho completo.
/// </para>
/// <para>
/// <b>Cómo.</b> Mide a su hijo con ancho infinito —así sabe lo que necesita de verdad, aunque el
/// contenedor le dé menos— y, si no cabe, lo arregla fuera de sus límites con
/// <c>ClipToBounds</c> puesto. No toca la <c>Visibility</c>: cambiarla dentro del arreglo pide otra
/// medida, la medida de un elemento oculto es cero, cero cabe, y el conjunto entra en bucle.
/// </para>
/// </summary>
public sealed class ChipHost : Decorator
{
    private Size _needed;

    public ChipHost() => ClipToBounds = true;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        _needed = Child.DesiredSize;

        // Pide lo suyo, pero acepta menos: pedir lo imposible en una fila apretada empuja a los
        // vecinos fuera de la pantalla, que es el defecto de al lado.
        return new Size(Math.Min(_needed.Width, constraint.Width), _needed.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        Child?.Arrange(size.Width + 0.5 >= _needed.Width
            ? new Rect(0, 0, _needed.Width, size.Height)
            : new Rect(-_needed.Width - 1, 0, _needed.Width, size.Height));

        return size;
    }
}
