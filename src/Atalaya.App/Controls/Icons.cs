using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// Los iconos de línea del sistema visual (F26 Parte A, D-950), como geometrías de 24×24.
/// <para>
/// <b>Por qué geometrías y no una fuente de iconos ni PNG.</b> Un trazo vectorial hereda el color
/// del texto al que acompaña —y por tanto cambia con el tema, que es el principio 5— y se dibuja
/// nítido a cualquier escala de Windows, que es el principio 1. Una fuente de iconos obliga a
/// desplegar un TTF y a recordar puntos de código; un PNG hay que tenerlo dos veces, uno por tema,
/// y a 150 % se ve borroso.
/// </para>
/// <para>
/// <b>Por qué en C# y no en un diccionario XAML.</b> El raíl construye sus entradas desde el
/// view-model, y un <c>DataTemplate</c> no puede resolver una clave de recurso variable sin
/// meter un converter por el medio. Aquí son propiedades estáticas: el view-model las nombra, y
/// los tests —que no levantan una <c>Application</c>— las leen igual.
/// </para>
/// <para>
/// Los trazados son los de la maqueta aprobada (<c>docs/design/atalaya-mockup-A.html</c>),
/// traducidos del <c>d</c> de cada SVG a la sintaxis de <see cref="Geometry"/>. Se dibujan con
/// <c>Fill=null</c> y trazo redondeado; el estilo <c>Icon</c> de <c>Styles.xaml</c> lo fija.
/// </para>
/// </summary>
public static class Icons
{
    private static Geometry P(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>Portafolio: una carpeta-tablero con su cabecera.</summary>
    public static Geometry Portfolio { get; } = P(
        "M5,4 L19,4 A2,2 0 0 1 21,6 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 L3,6 A2,2 0 0 1 5,4 Z M3,10 L21,10");

    /// <summary>Hallazgos: el triángulo de aviso. Es lo que un hallazgo es.</summary>
    public static Geometry Findings { get; } = P(
        "M12,3 L21,19 L3,19 Z M12,10 L12,14 M12,16.9 L12,17.1");

    /// <summary>Informes: una hoja escrita con la esquina doblada.</summary>
    public static Geometry Reports { get; } = P(
        "M14,3 L6,3 A2,2 0 0 0 4,5 L4,19 A2,2 0 0 0 6,21 L18,21 A2,2 0 0 0 20,19 L20,9 Z M14,3 L14,9 L20,9");

    /// <summary>Métricas: ejes y barras.</summary>
    public static Geometry Metrics { get; } = P(
        "M4,19 L4,5 M4,19 L20,19 M8,15 L8,11 M12,15 L12,8 M16,15 L16,13");

    /// <summary>Inventario: una lista de unidades.</summary>
    public static Geometry Inventory { get; } = P(
        "M4,6 L20,6 M4,12 L20,12 M4,18 L14,18");

    /// <summary>Sesión en vivo: un reloj, porque una sesión es algo que está corriendo.</summary>
    public static Geometry Session { get; } = P(
        "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z M12,7 L12,12 L15.5,14");

    /// <summary>Arreglo asistido: una llave inglesa.</summary>
    public static Geometry Fix { get; } = P(
        "M20.4,5.6 L17,9 L14.6,9 L14.6,6.6 L18,3.2 A6,6 0 0 0 10.6,10.6 L3.9,17.3 A2.2,2.2 0 0 0 7,20.4 L13.7,13.7 A6,6 0 0 0 20.4,5.6 Z");

    /// <summary>Cuenta: una persona.</summary>
    public static Geometry Account { get; } = P(
        "M8,8 A4,4 0 1 1 16,8 A4,4 0 1 1 8,8 Z M4,21 A8,8 0 0 1 20,21");

    /// <summary>Ajustes: el engranaje de siempre. Cambiarlo solo despistaría.</summary>
    public static Geometry Settings { get; } = P(
        "M9,12 A3,3 0 1 1 15,12 A3,3 0 1 1 9,12 Z " +
        "M19,12 A7,7 0 0 0 18.9,11 L20.9,9.5 L18.9,6 L16.5,7 A7,7 0 0 0 14.8,6 L14.5,3.4 L9.5,3.4 " +
        "L9.2,6 A7,7 0 0 0 7.5,7 L5.1,6 L3.1,9.5 L5.1,11 A7,7 0 0 0 5.1,13 L3.1,14.5 L5.1,18 L7.5,17 " +
        "A7,7 0 0 0 9.2,18 L9.5,20.6 L14.5,20.6 L14.8,18 A7,7 0 0 0 16.5,17 L18.9,18 L20.9,14.5 L18.9,13 " +
        "A7,7 0 0 0 19,12 Z");

    /// <summary>La flecha de volver, en la miga de pan.</summary>
    public static Geometry Back { get; } = P("M15,5 L8,12 L15,19");

    /// <summary>El separador «›» de la miga, dibujado y no escrito, para que case con la flecha.</summary>
    public static Geometry Chevron { get; } = P("M9,6 L15,12 L9,18");

    /// <summary>El triángulo de aviso suelto, para los avisos en línea y la razón de un botón.</summary>
    public static Geometry Warning { get; } = P("M12,3 L21,19 L3,19 Z M12,9 L12,13.5 M12,16.4 L12,16.6");

    /// <summary>El plegado/desplegado del raíl.</summary>
    public static Geometry RailToggle { get; } = P("M4,6 L20,6 M4,12 L20,12 M4,18 L20,18");
}
