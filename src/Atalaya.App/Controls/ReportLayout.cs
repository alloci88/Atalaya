namespace Atalaya.App.Controls;

/// <summary>
/// <b>Las medidas de la página de un informe</b> (F36-1b), en un solo sitio.
/// <para>
/// Están aquí y no repartidas por el XAML porque tres de ellas se sostienen entre sí: el ancho del
/// carril decide cuánto le queda al cuerpo, y cuánto le queda al cuerpo decide a partir de qué
/// ancho de VENTANA las tarjetas de hallazgo caben en dos columnas. Escribirlas por separado
/// significa que el día que el carril cambie de ancho, el umbral de las dos columnas seguirá
/// diciendo lo que decía y ya no será verdad.
/// </para>
/// </summary>
public static class ReportLayout
{
    /// <summary>
    /// El carril. 380 y no 300: con 300 los títulos de los hallazgos se truncaban al segundo
    /// carácter útil, que es lo que se vio en el <c>dist</c>.
    /// </summary>
    public const double RailWidth = 380;

    /// <summary>El hueco entre el cuerpo y el carril, y entre columnas de tarjetas.</summary>
    public const double Gap = 16;

    /// <summary>
    /// <b>Lo que la página se queda antes de repartir</b>: su relleno y la barra de desplazamiento.
    /// MEDIDO, no supuesto (N-2): sobre la vista real, una ventana de 2.560 deja el panel de
    /// lectura en 2.538, una de 1.600 en 1.578 y una de 1.000 en 978 — 22 px, los mismos en los
    /// tres. Sin restarlo, el umbral de las dos columnas diría 1.600 y ocurriría a 1.622.
    /// </summary>
    public const double Chrome = 22;

    /// <summary>
    /// El ancho mínimo de un azulejo de la fila de cifras. 240 y no menos: por debajo, el rosco de
    /// gravedad y su leyenda dejan de caber uno al lado del otro. Con esto los seis van en una sola
    /// fila desde ~1.540 px de ventana; por debajo, la rejilla los reparte como reparte siempre — y
    /// NO se le pone un tope por número de azulejos, que es lo que D-970 ya probó y revirtió.
    /// </summary>
    public const double TileMinWidth = 240;

    /// <summary>
    /// La medida de lectura de la PROSA (F27), que no se toca. Aquí ya no recorta la columna: es el
    /// ancho MÍNIMO que el cuerpo tiene que poder tener para que el carril quepa a su lado.
    /// </summary>
    public const double ReadWidth = 764;

    /// <summary>
    /// <b>A partir de este ancho de ventana, las tarjetas de hallazgo van en dos columnas.</b> No
    /// es prosa: una tarjeta es un bloque con su cabecera, su pastilla y su texto, y a 2.500 px
    /// leerlas en una sola columna deja media pantalla en blanco.
    /// </summary>
    public const double TwoColumnsFrom = 1600;

    /// <summary>Lo que le queda al cuerpo con el carril al lado, descontada la página.</summary>
    public static double BodyWidth(double page) => page - Chrome - Gap - RailWidth;

    /// <summary>
    /// El ancho mínimo de una tarjeta de hallazgo. <b>Se deriva del umbral</b>, no se elige: es
    /// exactamente el que hace que a <see cref="TwoColumnsFrom"/> quepan dos y un píxel menos deje
    /// una. Así el umbral que dice el manual y el que aplica la rejilla son el mismo número.
    /// </summary>
    public static double CardMinWidth => (BodyWidth(TwoColumnsFrom) - Gap) / 2;

    /// <summary>Cuántas columnas de tarjetas caben en una ventana de este ancho. Nunca más de dos.</summary>
    public static int CardColumns(double page)
        => page >= TwoColumnsFrom ? 2 : 1;

    /// <summary>
    /// Dónde se coloca el carril fijo mientras se hace scroll: pegado arriba hasta que su pie
    /// llegaría al final del cuerpo, y ahí se para. Sin el tope, un carril corto seguiría bajando y
    /// acabaría fuera de su columna.
    /// </summary>
    public static double StickyOffset(double scroll, double panelTop, double panelHeight, double railHeight)
    {
        double wanted = scroll - panelTop;
        double room = panelHeight - railHeight;
        return Math.Clamp(wanted, 0, Math.Max(0, room));
    }
}
