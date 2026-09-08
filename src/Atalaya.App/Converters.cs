using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;

namespace Atalaya.App;

/// <summary>
/// El tooltip del botón de plegar el raíl: dice lo que va a HACER, no cómo está (F26 §A retoque).
/// «Plegar o desplegar» describe un interruptor, y quien lo mira quiere saber qué pasa si lo pulsa.
/// </summary>
public sealed class RailToggleTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Desplegar el menú" : "Plegar el menú a solo iconos";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// EL COLOR DE UN ESTADO SALE DE LA PALETA, y por eso aquí ya no hay nueve converters de color
// (UI-AUDIT-1 raíz 1). Se fueron los del piloto de sync, el del vínculo local, el del estado de una
// unidad, el del aviso, los dos del estado de un hallazgo, el de un evento del historial, el de
// quién habla en el arreglo y el de la lupa del ciclo. Todos devolvían un `SolidColorBrush` con el
// hexadecimal escrito dentro —el mismo en los dos temas «porque el estado es semántico»— y todos
// fallaban por lo mismo: ese verde #3FB950 es el del tema OSCURO y sobre el crema del claro da
// 2,34:1. Su sitio es un `DataTrigger` con `DynamicResource` en `Styles.xaml`, que además se
// reevalúa al cambiar de tema (D-971).
//
// Los que quedan devuelven o un TINTE CON ALFA —que se compone sobre la superficie que haya y por
// eso no depende del tema— o algo que no es un color.

/// <summary>
/// El color de una gravedad EN UNA GRÁFICA (rúbrica §0). Es el único sitio donde una severidad no
/// se pinta con la escala de la paleta, y es a propósito: allí el color identifica una serie y
/// tiene que ser el mismo en los dos temas para poder comparar dos capturas. Por eso ya no se
/// declara en <c>Themes/Converters.xaml</c> — una vista que lo pidiera volvería a tener dos juegos
/// de color de gravedad a la vez, que es UI-0010.
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(SeverityPalette.Hex(value as Severity? ?? Severity.Baja)));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El nombre de una severidad tal y como se ESCRIBE en la interfaz. El identificador de la
/// enumeración va sin tilde porque C# no las lleva; volcarlo con <c>ToString()</c> en una etiqueta
/// escribía «Critica» en una interfaz en castellano. La enumeración es del modelo, no del usuario.
/// <para>
/// <b>UN rótulo por nivel, y la cifra siempre en el mismo lado</b> (UI-0027). Los cuatro niveles se
/// rotulaban de cinco maneras —«Críticas · Altas» en Portafolio, «15 Alta» en Hallazgos, «Crít 0»
/// en Métricas, «3 Altas» en el informe y «Critica» sin tilde en la sesión, que era el enum en
/// crudo—. Aquí están las tres formas que existen y no hay una cuarta: el nombre solo
/// (<see cref="Display"/>), el nombre en plural (<see cref="Plural"/>) y el recuento con su
/// nombre (<see cref="Counted"/>), que pone la cifra delante y concuerda el número.
/// </para>
/// </summary>
public static class SeverityNames
{
    public static string Display(Severity severity) => severity switch
    {
        Severity.Critica => "Crítica",
        Severity.Alta => "Alta",
        Severity.Media => "Media",
        _ => "Baja",
    };

    /// <summary>El nombre en plural: el que acompaña a un recuento distinto de uno.</summary>
    public static string Plural(Severity severity) => severity switch
    {
        Severity.Critica => "Críticas",
        Severity.Alta => "Altas",
        Severity.Media => "Medias",
        _ => "Bajas",
    };

    /// <summary>
    /// «3 Altas», «1 Crítica», «0 Bajas». La cifra DELANTE, siempre, y el nombre concordado: es la
    /// única forma en la que un recuento de gravedad se escribe en toda la aplicación.
    /// </summary>
    public static string Counted(Severity severity, int count)
        => $"{count} {(count == 1 ? Display(severity) : Plural(severity))}";
}

/// <inheritdoc cref="SeverityNames"/>
public sealed class SeverityToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Severity s ? SeverityNames.Display(s) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible (for empty-state overlays).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty (string, number, collection, bool) → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// «Vacío» según lo que sea el valor: una cadena en blanco, un número a cero, una colección sin
    /// elementos, un <c>false</c> o un nulo.
    /// <para>
    /// <b>Por qué la rama de no-cadena es explícita.</b> Antes decía
    /// <c>value is not null ? Visible : Collapsed</c>, y con eso <b>cualquier</b> valor no nulo
    /// encendía el control: un <c>Count</c> de 0 incluido. En V5, la insignia ⚖ de disputa colgaba
    /// de <c>Disputes.Count</c> y por tanto se pintaba en TODOS los hallazgos entrantes, mientras
    /// el motor y el resumen final decían —con razón— cero disputados (F5.14). Un conversor
    /// llamado «no vacío» que considera lleno el cero no es un descuido de un sitio: es una mina
    /// para el siguiente que lo use bien.
    /// </para>
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => IsNotEmpty(value) ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsNotEmpty(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        bool b => b,
        // Los contadores son el caso que abrió el parte: 0 es vacío, no «hay algo».
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0m,
        System.Collections.ICollection c => c.Count > 0,
        System.Collections.IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => true,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Una medida MENOS un número, sin bajar de cero (F26 §C, tercera revisión).
/// <para>
/// Existe para una sola cosa y conviene decir cuál: el tope de alto del contenido de Ajustes. La
/// fila de botones va justo debajo de la última fila de la sección cuando cabe, y pegada al pie de
/// la columna cuando no — que son dos comportamientos distintos y WPF no tiene ninguno de fábrica
/// (no hay <c>position: sticky</c>). Se consigue dejando que el scroll mida lo que mida su
/// contenido, con un tope: el alto de la columna menos lo que ocupan los botones.
/// </para>
/// </summary>
public sealed class MinusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double left = value is double d ? d : 0;
        double right = parameter is null
            ? 0
            : double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p)
                ? p
                : 0;
        return Math.Max(0, left - right);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a 0..1 progress as a whole percentage string.</summary>
public sealed class ProgressToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? PercentText.Of(d) : PercentText.Of(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Una severidad nula (evento que no narra un hallazgo) no pinta chip.</summary>
public sealed class SeverityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Severity ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El patron de trazos de una serie agrupada (F5.9 §2). «Otras» va SIEMPRE a trazos, tambien en
/// la leyenda: el color solo no basta para decir que ahi dentro hay varias aplicaciones.
/// </summary>
public sealed class DashedToArrayConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new DoubleCollection(new double[] { 2, 1.5 }) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>El fondo de la línea de diff, muy tenue: el código tiene que seguir leyéndose.</summary>
public sealed class DiffKindToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiffKind.Anadida => new SolidColorBrush(Color.FromArgb(0x22, 0x3F, 0xB9, 0x50)),
        DiffKind.Quitada => new SolidColorBrush(Color.FromArgb(0x22, 0xD1, 0x3A, 0x3A)),
        DiffKind.Salto => new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)),
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>La pestaña del fichero que se está mirando, resaltada.</summary>
public sealed class SelectedTabToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromArgb(0x33, 0x6C, 0x93, 0xC0))
            : new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Empareja la pregunta con la opción pulsada para que el comando reciba las dos. Un botón dentro
/// de un <c>ItemsControl</c> anidado solo conoce su opción; el comando necesita saber además a qué
/// tarjeta contesta, y pasar el par es más honrado que buscarlo por el árbol visual.
/// </summary>
public sealed class QuestionChoicePairConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.ToArray();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible solo si TODAS las condiciones se cumplen.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Acorta una ruta por el MEDIO: <c>src/…/CommonStatics.cs</c>. La elipsis del final de WPF
/// (<c>TextTrimming</c>) se come justo lo que identifica un fichero —el nombre— y deja lo que
/// comparten todas las rutas del repo. El parámetro es el largo máximo en caracteres (por
/// defecto 44); la ruta entera va siempre en el tooltip, que es donde se lee sin prisa.
/// </summary>
public sealed class MiddleEllipsisConverter : IValueConverter
{
    public const int DefaultMax = 44;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Shorten(value as string, Max(parameter));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static string Shorten(string? path, int max)
    {
        string text = (path ?? string.Empty).Trim();
        if (max < 8 || text.Length <= max)
        {
            return text;
        }

        // El nombre del fichero manda: es lo único que no se recorta mientras quepa.
        int slash = text.LastIndexOfAny(new[] { '/', '\\' });
        string name = slash >= 0 ? text[(slash + 1)..] : text;
        if (name.Length + 2 >= max)
        {
            return "\u2026" + text[^(max - 1)..];
        }

        int head = max - name.Length - 2;
        return text[..head] + "\u2026" + (slash >= 0 ? text[slash..] : string.Empty);
    }

    private static int Max(object? parameter)
        => parameter is not null && int.TryParse(parameter.ToString(), out int n) && n > 0
            ? n
            : DefaultMax;
}

/// <summary>
/// <b>El icono de una entrada de la conversación</b> (F30 §1c, ampliado en §3).
/// <para>
/// <b>Por qué un converter y no un campo en el modelo.</b> La marca (<c>ConversationEntry.Glyph</c>)
/// es el dato: dice de qué clase es la línea, y de ella cuelgan los tests que cuadran la narración
/// con los contadores de la sesión. El dibujo es presentación, y cambiar de carácter a vector no
/// puede obligar a tocar el modelo ni a reescribir esos tests.
/// </para>
/// <para>
/// <b>La clase manda sobre la marca en un caso, y a propósito</b>: <c>unit_done</c> se apunta con la
/// marca de una herramienta cualquiera —lo es en el registro— pero en el hilo es el cierre de un
/// tramo y lleva su icono. Lo demás se lee de la marca, que es donde vive la diferencia entre leer
/// un fichero y reportar.
/// </para>
/// <para>
/// Devuelve <c>null</c> para las marcas que todavía son caracteres —las de hallazgo y cierre de
/// pasada, anteriores a F30—, y la vista pinta entonces el carácter. Convivir es a propósito: esta
/// familia son las seis clases que llevan icono, no las siete marcas que llevan bien desde F12.
/// </para>
/// </summary>
public sealed class ConversationIconConverter : IValueConverter
{
    /// <summary>El trazo de una entrada, o <c>null</c> si su marca se pinta como carácter.</summary>
    public static Geometry? IconOf(ConversationEntry entry) => entry.Kind switch
    {
        ConversationKind.Unidad => Controls.Icons.UnitDone,
        _ => entry.Glyph switch
        {
            "⚒" => Controls.Icons.Tool,
            "👁" => Controls.Icons.Eye,
            "◆" => Controls.Icons.Milestone,
            "→" => Controls.Icons.Handover,
            "⚖" => Controls.Icons.Judge,
            _ => null,
        },
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConversationEntry entry ? IconOf(entry) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <inheritdoc cref="ConversationIconConverter"/>
/// <summary>Lo contrario: visible solo cuando la entrada NO tiene icono y hay que pintar el carácter.</summary>
public sealed class ConversationGlyphIsTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConversationEntry entry
           && entry.Glyph.Length > 0
           && ConversationIconConverter.IconOf(entry) is null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Niega un booleano. Lo pide el <c>MultiBinding</c> de «puede pero no debería verse».</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>El nombre de una temática tal y como se escribe (F17).</summary>
public sealed class ThemeToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AuditTheme t ? Copilot.ThemeCatalog.Display(t) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <b>Un trozo de markdown, ya pintado</b> (F36 §1.5). Lo usa la tarjeta de un hallazgo: su
/// descripción y su recomendación son markdown del informe y se dibujan con el mismo renderizador
/// que el resto del documento, no con un <c>TextBlock</c> que perdería las negritas y las listas.
/// <para>
/// Va sin manejador de enlaces a propósito: un enlace dentro de la descripción de un hallazgo se
/// queda en texto, que es lo que ya hacía el renderizador sin destino utilizable. Abrir el
/// navegador es del documento, y el documento lo pinta el view-model con su manejador.
/// </para>
/// </summary>
public sealed class MarkdownToDocumentConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string text && !string.IsNullOrWhiteSpace(text)
            ? Services.MarkdownFlowDocument.Build(text)
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
