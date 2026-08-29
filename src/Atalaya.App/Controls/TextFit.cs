using System.Windows;
using System.Windows.Media;
using Atalaya.App.Services;

namespace Atalaya.App.Controls;

/// <summary>
/// Encajar un texto en un ancho <b>midiéndolo</b>, no estimándolo por número de caracteres
/// (F10.1 §2).
/// <para>
/// <b>Por qué medido.</b> «WWW» y «lll» tienen tres caracteres y ocupan el triple el uno que el
/// otro. Un presupuesto de caracteres acierta de media y falla en los nombres reales —
/// <c>FormAdvancedVibrationModel.cs</c> contra <c>Crc.cs</c>—, y falla siempre hacia el mismo
/// lado: dejando media palabra escrita.
/// </para>
/// <para>
/// <b>Y por qué elipsis EN MEDIO.</b> Los nombres de esta aplicación se distinguen por los dos
/// extremos: <c>ControllerConfiguration.cs</c> y <c>ControllerMain.cs</c> comparten los diez
/// primeros caracteres, así que un recorte por el final los deja idénticos y además se come la
/// extensión. Partiendo por el medio se conservan el prefijo y el sufijo, que es donde está la
/// diferencia.
/// </para>
/// </summary>
public static class TextFit
{
    /// <summary>El carácter de elipsis. Uno solo, no tres puntos: ocupa menos y no se parte.</summary>
    public const char Ellipsis = '…';

    /// <summary>
    /// Cuánto del nombre hay que conservar para que la etiqueta siga identificando algo. Por
    /// debajo de esto no se escribe nada: una celda con media palabra no informa, ensucia — y el
    /// tooltip sigue diciendo el nombre entero.
    /// </summary>
    public const double DefaultRetention = 0.7;

    /// <summary>Por debajo de estos caracteres, ningún recorte vale la pena.</summary>
    public const int MinimumChars = 6;

    /// <summary>
    /// El texto que cabe en <paramref name="available"/>, o <c>null</c> si no cabe nada aceptable.
    /// Devuelve el original cuando cabe entero.
    /// </summary>
    /// <param name="retention">
    /// Fracción del texto original que hay que conservar como mínimo. <c>0</c> significa «escribe
    /// lo que quepa aunque quede en un puñado de letras», que es lo que pide el nombre de un
    /// módulo: su cabecera tiene que ser legible siempre, y lo que cae antes es el detalle.
    /// </param>
    public static string? Fit(
        string? text,
        Typeface typeface,
        double size,
        double pixelsPerDip,
        double available,
        double retention = DefaultRetention)
    {
        string full = (text ?? string.Empty).Trim();
        if (full.Length == 0 || available <= 0 || size <= 0)
        {
            return null;
        }

        if (Width(full, typeface, size, pixelsPerDip) <= available)
        {
            return full;
        }

        // El suelo: por debajo de tantos caracteres conservados, mejor nada.
        int floor = Math.Max(
            retention <= 0 ? 3 : MinimumChars,
            (int)Math.Ceiling(full.Length * Math.Clamp(retention, 0, 1)));

        if (floor >= full.Length)
        {
            return null;
        }

        // Búsqueda binaria sobre cuántos caracteres se conservan. El ancho crece con el número de
        // caracteres, así que la propiedad es monótona y la búsqueda es correcta.
        int low = floor;
        int high = full.Length - 1;
        string? best = null;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            string candidate = Middle(full, mid);
            if (Width(candidate, typeface, size, pixelsPerDip) <= available)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// El texto recortado por el medio a <paramref name="keep"/> caracteres visibles, con la
    /// elipsis entre las dos mitades. La cabeza se queda con el carácter de más cuando el reparto
    /// es impar: la mitad izquierda es la que suele llevar la palabra que distingue.
    /// </summary>
    public static string Middle(string text, int keep)
    {
        if (keep >= text.Length)
        {
            return text;
        }

        if (keep <= 1)
        {
            return Ellipsis.ToString();
        }

        int head = (keep + 1) / 2;
        int tail = keep - head;
        return tail == 0
            ? text[..head] + Ellipsis
            : text[..head] + Ellipsis + text[^tail..];
    }

    /// <summary>Lo que mide ese texto con esa tipografía. Es la única medida que se usa.</summary>
    public static double Width(string text, Typeface typeface, double size, double pixelsPerDip)
        => Format(text, typeface, size, pixelsPerDip, Brushes.Black).WidthIncludingTrailingWhitespace;

    /// <summary>El <c>FormattedText</c> con la cultura de la aplicación, para medir o para pintar.</summary>
    public static FormattedText Format(
        string text, Typeface typeface, double size, double pixelsPerDip, Brush ink)
        => new(
            text,
            AppCulture.Display,
            FlowDirection.LeftToRight,
            typeface,
            size,
            ink,
            pixelsPerDip);
}
