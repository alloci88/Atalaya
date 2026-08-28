using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;

namespace Atalaya.App;

/// <summary>Sync indicator colour (§3): green/amber/red.</summary>
public sealed class SyncHealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SyncHealth.Green => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        SyncHealth.Amber => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        _ => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El piloto de vinculación local (F5.8 §1): verde, ámbar, rojo. Mismos tres colores y misma
/// forma —un <c>Ellipse</c>— que el indicador de sync de la barra inferior, porque significan lo
/// mismo: si esto está en verde, se puede trabajar.
/// <para>
/// Va con colores explícitos y no con los del tema por la razón de <c>FindingStatusToBrush</c>:
/// el estado es semántico y significa lo mismo en claro que en oscuro. Y el color NUNCA va solo —
/// la etiqueta con el nombre del estado se pinta al lado, para quien no lo distinga.
/// </para>
/// </summary>
public sealed class CloneLinkStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CloneLinkState.Vinculada => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        CloneLinkState.Problema => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        _ => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Severity colour for finding badges (rúbrica §0).</summary>
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

/// <summary>Check mark / cross / dash for each row of the chained connection check (D2.3).</summary>
public sealed class CheckStateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckState.Ok => "✓",
        CheckState.Failed => "✕",
        CheckState.Running => "…",
        _ => "•",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colour for the connection-check glyph: green ok, red failed, muted otherwise.</summary>
public sealed class CheckStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckState.Ok => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        CheckState.Failed => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
        CheckState.Running => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a 0..1 progress as a whole percentage string.</summary>
public sealed class ProgressToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Icono del tipo de entrada de actividad: evento con glifo, texto sin él.</summary>
public sealed class ActivityKindToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ActivityKind.Evento ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Color del estado de una unidad en la cola de V5.</summary>
public sealed class UnitRunStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        UnitRunState.Auditando => new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xE0)),
        UnitRunState.Completa => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        UnitRunState.Incompleta => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        UnitRunState.CoberturaIncompleta => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        UnitRunState.CortadaPorPresupuesto => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        UnitRunState.Detenida => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        UnitRunState.NoLocalizada => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };

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

/// <summary>Resalta en ámbar las líneas del resumen que piden atención.</summary>
public sealed class WarningToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30))
            : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El color del chip de estado de la ficha (F5.5 §2): azul lo activo, verde lo resuelto, gris lo
/// silenciado. Va con colores explícitos y no con los del tema porque el estado es semántico —
/// significa lo mismo en claro que en oscuro— y porque son los mismos tres colores que ya usan el
/// indicador de sync y la cola de V5.
/// </summary>
public sealed class FindingStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingStatus.Activo => new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xE0)),
        FindingStatus.Resuelto => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        _ => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <inheritdoc cref="FindingStatusToBrushConverter"/>
/// <remarks>El mismo color al 12 %: el relleno del chip, que nunca compite con el texto.</remarks>
public sealed class FindingStatusToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingStatus.Activo => new SolidColorBrush(Color.FromArgb(0x20, 0x4A, 0x9E, 0xE0)),
        FindingStatus.Resuelto => new SolidColorBrush(Color.FromArgb(0x20, 0x3F, 0xB9, 0x50)),
        _ => new SolidColorBrush(Color.FromArgb(0x20, 0x9A, 0x9A, 0xA2)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El color del glifo de un evento del historial (F5.5 §5): la línea de tiempo se recorre con la
/// vista, y el color hace que «Resuelto» y «Reabierto» se distingan sin leerlos.
/// </summary>
public sealed class FindingEventToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingEvent.Resolved => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        FindingEvent.Reopened or FindingEvent.Recurrence => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x2B)),
        FindingEvent.Disputed or FindingEvent.DisputeCleared => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        FindingEvent.Detected => new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xE0)),
        FindingEvent.SeverityChanged => new SolidColorBrush(Color.FromRgb(0xD2, 0xB0, 0x36)),
        _ => new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x96)),
    };

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

// ====================================================================== F6.9 · Arreglo asistido

/// <summary>
/// El color del nombre de quien habla en la conversación de arreglo (F6.9 §4). Tres voces, tres
/// colores: el agente en azul, el usuario en verde, la aplicación en gris. Explícitos y no del
/// tema por la razón de siempre — significan lo mismo en claro que en oscuro— y nunca van solos:
/// el nombre («Agente», «Tú», «Atalaya») se escribe al lado.
/// </summary>
public sealed class FixVoiceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FixVoice.Agente => new SolidColorBrush(Color.FromRgb(0x6C, 0x93, 0xC0)),
        FixVoice.Usuario => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        _ => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>El fondo de la burbuja: apenas un tinte, para separar sin gritar.</summary>
public sealed class FixVoiceToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FixVoice.Usuario => new SolidColorBrush(Color.FromArgb(0x22, 0x3F, 0xB9, 0x50)),
        FixVoice.Sistema => new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
        _ => new SolidColorBrush(Color.FromArgb(0x1A, 0x6C, 0x93, 0xC0)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// El color del texto de una línea de diff. Verde lo añadido, rojo lo quitado, el color normal el
/// contexto. El marcador (+ − ⋯) va SIEMPRE delante: el color solo refuerza lo que ya se lee.
/// </summary>
public sealed class DiffKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiffKind.Anadida => new SolidColorBrush(Color.FromRgb(0x6C, 0xD0, 0x80)),
        DiffKind.Quitada => new SolidColorBrush(Color.FromRgb(0xE0, 0x80, 0x80)),
        DiffKind.Salto => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        _ => new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
    };

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

/// <summary>Niega un booleano. Lo pide el <c>MultiBinding</c> de «puede pero no debería verse».</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}
