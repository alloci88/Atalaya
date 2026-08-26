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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critica => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        Severity.Alta => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x2B)),
        Severity.Media => new SolidColorBrush(Color.FromRgb(0xD2, 0xB0, 0x36)),
        _ => new SolidColorBrush(Color.FromRgb(0x6C, 0x93, 0xC0)),
    };

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

/// <summary>Non-empty string / non-null → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? (!string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed)
            : value is not null ? Visibility.Visible : Visibility.Collapsed;

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
