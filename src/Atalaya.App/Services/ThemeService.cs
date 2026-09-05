using System.Windows;
using Wpf.Ui.Appearance;

namespace Atalaya.App.Services;

/// <summary>
/// Pone el tema: la paleta de Atalaya Y la de la librería, siempre las dos (F26 Parte A, D-945).
/// <para>
/// <b>Lo que hacía antes.</b> Una sola línea: <c>ApplicationThemeManager.Apply(Light|Dark)</c>. Eso
/// cambia los colores de fábrica de WPF-UI y nada más, así que el modo claro era el blanco puro de
/// la librería —«el fondo es blanco nuclear y quema»— y todo lo que Atalaya pintaba con brochas
/// propias (gravedades, estados, avisos) se quedaba con los colores del modo oscuro encima del
/// blanco. El tema cambiaba el fondo, no la aplicación.
/// </para>
/// <para>
/// <b>Lo que hace ahora.</b> Sustituye el diccionario de paleta que está montado en
/// <c>Application.Resources</c> por el del tema pedido, y además sigue avisando a WPF-UI para que
/// sus propios controles —los que no pasan por nuestras claves: barra de título, scroll, menús
/// contextuales— cambien con el resto. Las dos cosas, o la ventana queda a medio teñir.
/// </para>
/// <para>
/// La sustitución funciona porque todo lo que consume color lo hace con <c>DynamicResource</c>:
/// cambiar el diccionario reevalúa cada referencia sin reconstruir una sola vista. Por eso el
/// cambio de tema se ve al instante y no hace falta reiniciar.
/// </para>
/// </summary>
public static class ThemeService
{
    // Las URIs van CON el nombre del ensamblado. La forma corta —«pack://application:,,,/Themes/…»—
    // se resuelve contra el ensamblado de ENTRADA, que es Atalaya cuando arranca la aplicación pero
    // no cuando la carcasa la monta otro (el banco de capturas de las vistas densas, F26 §B). El
    // recurso es de Atalaya siempre, así que se dice así siempre.
    private const string DarkUri = "pack://application:,,,/Atalaya;component/Themes/Palette.Dark.xaml";
    private const string LightUri = "pack://application:,,,/Atalaya;component/Themes/Palette.Light.xaml";

    /// <summary>La clave que marca un diccionario como «paleta», para poder reconocer el puesto.</summary>
    private const string Marker = "Palette.Name";

    public static bool IsLight(string? theme)
        => string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        Swap(app.Resources, IsLight(theme) ? LightUri : DarkUri);

        ApplicationThemeManager.Apply(IsLight(theme) ? ApplicationTheme.Light : ApplicationTheme.Dark);

        // Y el panel de código (D-980). Su superficie sale de la paleta como todo lo demás, pero
        // el COLOREADO no es un recurso: es una definición de AvalonEdit que hay que reajustar
        // contra la superficie nueva. Se hace aquí porque éste es el único sitio que sabe cuándo
        // cambia el tema.
        if (app.TryFindResource("Color.Code.Surface") is System.Windows.Media.Color surface
            && app.TryFindResource("Color.Code.Ink") is System.Windows.Media.Color ink)
        {
            Controls.CodePalette.Apply(surface, ink);
        }
    }

    /// <summary>
    /// Cambia la paleta EN SU SITIO. El orden importa: la paleta tiene que seguir después de los
    /// diccionarios de WPF-UI, porque parte de su trabajo es reescribir las claves de la librería
    /// (fondos, tarjetas, acento). Quitarla y añadirla al final la movería detrás de todo, que
    /// funciona hoy pero deja de funcionar en cuanto alguien añada un diccionario más.
    /// </summary>
    private static void Swap(ResourceDictionary resources, string uri)
    {
        var next = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };

        for (int i = 0; i < resources.MergedDictionaries.Count; i++)
        {
            if (resources.MergedDictionaries[i].Contains(Marker))
            {
                resources.MergedDictionaries[i] = next;
                return;
            }
        }

        resources.MergedDictionaries.Add(next);
    }
}
