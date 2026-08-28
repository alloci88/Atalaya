using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Atalaya.App.Services;

/// <summary>
/// La cultura con la que Atalaya escribe los números y las fechas que lee una persona (F8.1).
/// <para>
/// <b>Atalaya formatea SIEMPRE en es-ES, no en la cultura de la máquina.</b> No es una
/// preferencia estética: es la consecuencia de dos hechos del producto.
/// </para>
/// <para>
/// <b>Uno: la aplicación es monolingüe en español.</b> Cada etiqueta, cada tooltip, cada mensaje,
/// cada encabezado de informe y cada descripción de regla está en español. El formato numérico es
/// parte del idioma, no un ajuste de la máquina: un texto español que dice «coste 67.5» sobre un
/// Windows en inglés no es «respetar al usuario», es una frase a medio traducir. La combinación
/// coherente es la que ya usa todo lo demás de la ventana.
/// </para>
/// <para>
/// <b>Dos, y es el decisivo: los informes se comparten.</b> Se escriben en el hub y los lee todo
/// el equipo. Con la cultura de cada máquina, la misma sesión escrita desde un Windows en inglés y
/// desde uno en español producía dos textos distintos — y «1,234» significa 1,234 en uno y 1234 en
/// el otro. Un artefacto compartido cuyo significado depende de quién lo escribió no es
/// ambiguo de mostrar: es ambiguo de leer, que es mucho peor.
/// </para>
/// <para>
/// <b>Lo que esto NO toca.</b> Los datos para máquinas siguen siendo invariantes y tienen que
/// seguir siéndolo: el JSON del hub (System.Text.Json escribe los números invariantes por
/// construcción), los ULID, los hashes, los ids legibles (<c>BUG-0042</c>) y las rutas. Aplicar
/// una cultura ahí sería el error grave de esta historia — un <c>app.json</c> con «67,5» dentro no
/// lo puede volver a leer nadie. La frontera es: <b>texto para personas → es-ES; datos para
/// máquinas → invariante</b>.
/// </para>
/// </summary>
public static class AppCulture
{
    /// <summary>
    /// La cultura de todo lo que se le enseña o se le escribe a una persona. Se resuelve por
    /// nombre y con caída a invariante: una imagen recortada de .NET sin datos de globalización
    /// (<c>InvariantGlobalization</c>) no puede impedir que la aplicación arranque.
    /// </summary>
    public static CultureInfo Display { get; } = Resolve("es-ES");

    /// <summary>
    /// Fija <see cref="Display"/> como cultura de TODOS los hilos del proceso, incluidos los que
    /// se creen después. Se llama al arrancar, antes de abrir ninguna ventana.
    /// <para>
    /// Se usan las propiedades <c>DefaultThreadCurrent*</c> y no <c>Thread.CurrentThread</c>
    /// porque media aplicación formatea en hilos de fondo —la sesión en vivo, el arreglo asistido,
    /// las consultas de métricas— y un hilo del pool nace con la cultura del sistema. Fijar solo el
    /// hilo de UI habría dejado justo esos textos en la cultura de la máquina.
    /// </para>
    /// </summary>
    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = Display;
        CultureInfo.DefaultThreadCurrentUICulture = Display;
        CultureInfo.CurrentCulture = Display;
        CultureInfo.CurrentUICulture = Display;
        ApplyToWpfBindings();
    }

    /// <summary>
    /// Y la misma cultura para los <c>StringFormat</c> de los enlaces de XAML.
    /// <para>
    /// WPF no usa <c>CurrentCulture</c> en los enlaces: usa el <c>Language</c> del elemento, que
    /// vale <b>en-US</b> de fábrica y no lo cambia nadie. Hoy no hay ningún <c>StringFormat</c>
    /// numérico en las vistas, así que esto no arregla ningún síntoma: cierra la puerta a que el
    /// primero que alguien escriba salga en inglés en medio de una ventana en español, que es un
    /// fallo que no se ve venir y no lo detecta ningún test de los que hay.
    /// </para>
    /// </summary>
    private static void ApplyToWpfBindings()
    {
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Display.IetfLanguageTag)));
        }
        catch
        {
            // OverrideMetadata solo se puede llamar una vez por tipo: si ya se hizo (un segundo
            // arranque dentro del mismo proceso, un test que monta la carcasa), no hay nada que
            // hacer y desde luego no hay que impedir que la aplicación abra.
        }
    }

    private static CultureInfo Resolve(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
