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

    /// <summary>
    /// Inventario: una lista de unidades CON SU ESTADO — un portapapeles con dos marcas.
    /// <para>
    /// <b>Por qué cambia</b> (UI-0047). Eran tres rayas horizontales, y el botón de plegar el menú
    /// (<see cref="RailToggle"/>) también: la única diferencia era que la tercera raya de
    /// Inventario era más corta. Plegado el raíl —que es cuando el icono es lo ÚNICO que queda—
    /// los dos caían en la misma columna de 40 px separados por 40 de alto, y no había forma de
    /// distinguirlos. Un portapapeles marcado dice además lo que el inventario es: unidades con
    /// un estado, no una lista cualquiera.
    /// </para>
    /// </summary>
    public static Geometry Inventory { get; } = P(
        "M9,3.5 L15,3.5 L15,6 L9,6 Z " +
        "M9,4.75 L6.5,4.75 A1.5,1.5 0 0 0 5,6.25 L5,19 A1.5,1.5 0 0 0 6.5,20.5 L17.5,20.5 " +
        "A1.5,1.5 0 0 0 19,19 L19,6.25 A1.5,1.5 0 0 0 17.5,4.75 L15,4.75 " +
        "M8,11 L9.5,12.5 L12,10 M14,11.5 L16,11.5 " +
        "M8,16 L9.5,17.5 L12,15 M14,16.5 L16,16.5");

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

    /// <summary>
    /// La papelera de la tarjeta del portafolio: tapa, cuerpo y las dos rayas de dentro.
    /// <para>
    /// Vuelve en el cierre de F27. El borrado de una aplicación vivía tras un «…» desde F26 §B; el
    /// usuario mandó devolver el icono, que es lo que había antes y lo que se reconoce sin abrir
    /// nada. Se dibuja con la geometría de la casa y no con un glifo de Segoe MDL2 —que es como
    /// estaba— para que herede el trazo, el tamaño y el color del sistema (D-944).
    /// </para>
    /// </summary>
    public static Geometry Trash { get; } = P(
        "M4,7 L20,7 " +
        "M10,4 L14,4 " +
        "M6,7 L7,20 L17,20 L18,7 " +
        "M10,10.5 L10,16.5 " +
        "M14,10.5 L14,16.5");

    /// <summary>La flecha de volver, en la miga de pan.</summary>
    public static Geometry Back { get; } = P("M15,5 L8,12 L15,19");

    /// <summary>El separador «›» de la miga, dibujado y no escrito, para que case con la flecha.</summary>
    public static Geometry Chevron { get; } = P("M9,6 L15,12 L9,18");

    /// <summary>El triángulo de aviso suelto, para los avisos en línea y la razón de un botón.</summary>
    public static Geometry Warning { get; } = P("M12,3 L21,19 L3,19 Z M12,9 L12,13.5 M12,16.4 L12,16.6");

    /// <summary>El plegado/desplegado del raíl.</summary>
    public static Geometry RailToggle { get; } = P("M4,6 L20,6 M4,12 L20,12 M4,18 L20,18");

    /// <summary>Información: la «i» en su círculo. Es el icono de un aviso que no reclama nada.</summary>
    public static Geometry Info { get; } = P(
        "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z M12,11 L12,16.5 M12,7.6 L12,7.8");

    /// <summary>
    /// Disponible: la marca de verificación. Va DENTRO de su círculo en la lista de estados de
    /// Cuenta, porque las cinco filas tienen que ocupar lo mismo con estado o sin él.
    /// </summary>
    public static Geometry Check { get; } = P("M5,12.5 L10,17.5 L19,7");

    /// <summary>No disponible: la cruz. Nunca sola: siempre con la palabra y el motivo debajo.</summary>
    public static Geometry Cross { get; } = P("M6,6 L18,18 M18,6 L6,18");

    /// <summary>
    /// No comprobado: el círculo vacío. Es la forma de «todavía nada» que no se confunde con un
    /// fallo — el punto relleno de antes se leía como una viñeta y no como un estado.
    /// </summary>
    public static Geometry Pending { get; } = P("M5,12 A7,7 0 1 1 19,12 A7,7 0 1 1 5,12 Z");

    /// <summary>Un extra disponible que no está activado: el signo más, neutro por definición.</summary>
    public static Geometry Plus { get; } = P("M12,5 L12,19 M5,12 L19,12");

    /// <summary>
    /// El icono de un estado vacío: una bandeja. Dice «aquí va algo y todavía no está», que es
    /// distinto de un error y distinto de una advertencia.
    /// </summary>
    public static Geometry Empty { get; } = P(
        "M4,13 L8,13 L9.5,16 L14.5,16 L16,13 L20,13 M4,13 L6.5,5.5 L17.5,5.5 L20,13 L20,18.5 L4,18.5 Z");

    // ================================================================ El hilo de actividad (F30)

    // POR QUÉ VECTORES Y NO GLIFOS DE TEXTO. El hilo entró en F30 §1 con caracteres —⚒ 👁 ◆ →—
    // porque era lo rápido, y es frágil por dos motivos que se vieron en el `dist`: un carácter que
    // la fuente de interfaz no tiene lo resuelve Windows con la fuente que encuentre, así que sale
    // con otra métrica y con otro peso —el ⚒ se salía de su hueco y se solapaba con el texto—, y
    // además no hereda el trazo ni el color del sistema. Es exactamente el argumento de D-944 para
    // el resto de la casa, aplicado tarde. Estos cuatro son del mismo juego que los del raíl: 24×24,
    // trazo abierto, sin relleno.

    /// <summary>Una herramienta que el auditor ha invocado: los dos chevrones de una llamada.</summary>
    public static Geometry Tool { get; } = P("M9,7 L4.5,12 L9,17 M15,7 L19.5,12 L15,17");

    /// <summary>Ha leído un fichero. El ojo, que es lo que ya significa eso en el arreglo asistido.</summary>
    public static Geometry Eye { get; } = P(
        "M3,12 C6,7.5 9.5,5.5 12,5.5 C14.5,5.5 18,7.5 21,12 "
        + "C18,16.5 14.5,18.5 12,18.5 C9.5,18.5 6,16.5 3,12 Z "
        + "M12,9.5 A2.5,2.5 0 1 1 11.99,9.5 Z");

    /// <summary>Un hito de la propia Atalaya: el rombo de un mojón, ni bueno ni malo.</summary>
    public static Geometry Milestone { get; } = P("M12,4 L20,12 L12,20 L4,12 Z");

    /// <summary>El turno sale hacia el modelo. A partir de aquí, lo que pase es suyo.</summary>
    public static Geometry Handover { get; } = P("M4,12 L19,12 M13.5,6.5 L20,12 L13.5,17.5");
}
