using System.Windows;
using Atalaya.App.Services;
using FluentAssertions;
using Wpf.Ui.Markup;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R6 §1 — <b>dos reglas para que no haya tercera vez</b> (N-7).
/// <para>
/// La primera es la que R5 rompió: un estilo con clave sobre un control de la librería
/// <b>sustituye</b> al implícito de la librería, plantilla incluida, salvo que derive de él. Así se
/// quedaron los diez diálogos sin el <c>ControlTemplate</c> de <c>FluentWindow</c> —de ahí que
/// aparecieran en blanco— y así se cerraba la aplicación al abrirlos. Es una regla estática, se
/// comprueba sin abrir una ventana, y salta antes que cualquier autochequeo: verificada contra el
/// código de R5, donde falla nombrando <c>Dialog</c> y <c>Dialog.TitleBar</c>.
/// </para>
/// <para>
/// La segunda es la del encargo: la lista de diálogos que el autochequeo instancia y mide tiene que
/// ser la lista COMPLETA del ensamblado, para que un diálogo nuevo no se quede fuera de la red.
/// </para>
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DialogStyleTests
{
    /// <summary>
    /// <b>Todo estilo con clave sobre un control de WPF-UI deriva del implícito de la librería.</b>
    /// <para>
    /// Sin <c>BasedOn</c>, el estilo no añade: reemplaza. El control pierde la plantilla que la
    /// librería le da y, con ella, su chrome y sus partes — y lo que se ve es una ventana en blanco
    /// que se lleva la aplicación por delante al mostrarse.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_estilo_sobre_un_control_de_la_libreria_deriva_del_suyo_y_no_lo_sustituye()
    {
        var offenders = new List<string>();

        ViewLayout.OnUiThread(() =>
        {
            ResourceDictionary resources = AppResources();

            foreach ((string name, Style style) in OurStyles(resources))
            {
                // Solo los controles de la librería: los nuestros no traen un estilo implícito que
                // se pueda sustituir sin querer.
                if (style.TargetType?.Namespace?.StartsWith("Wpf.Ui", StringComparison.Ordinal) != true)
                {
                    continue;
                }

                // Y solo si la librería da uno para ese control: si no lo hay, no hay nada que
                // sustituir y el estilo puede partir de cero legítimamente.
                if (resources[style.TargetType] is not Style)
                {
                    continue;
                }

                if (!DerivesFrom(style, style.TargetType))
                {
                    offenders.Add($"{name} ({style.TargetType.Name})");
                }
            }
        });

        offenders.Should().BeEmpty(
            "un estilo con clave sin BasedOn sustituye al de la librería, plantilla incluida: el "
            + "control se queda sin chrome y la ventana revienta al mostrarse (R5)");
    }

    /// <summary>
    /// <b>El autochequeo instancia TODOS los diálogos.</b> La lista se descubre por reflexión y no
    /// se escribe a mano; esta prueba la recalcula por su cuenta y exige que coincida, que es lo que
    /// impide que un diálogo nuevo se quede fuera de la red sin que nadie lo note.
    /// </summary>
    [Fact]
    public void El_autochequeo_mide_todos_los_dialogos_del_ensamblado()
    {
        var esperados = typeof(Atalaya.App.App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(Window).IsAssignableFrom(t)
                        && t != typeof(Atalaya.App.MainWindow))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        StartupSelfCheck.DialogTypes().Select(t => t.Name).Should().Equal(esperados);

        esperados.Should().NotBeEmpty("si esto se vacía, la red ha dejado de existir");
        esperados.Should().Contain("ThresholdsDialog").And.Contain("ReconcileCostsDialog");
    }

    // ================================================================ andamiaje

    /// <summary>Los recursos de la aplicación, montados como los monta <c>App.xaml</c>.</summary>
    private static ResourceDictionary AppResources()
    {
        // Sin una `Application` viva, WPF no resuelve `pack://application:,,,/…`: la URI ni se
        // reconoce. Se crea una sola vez —el marco no admite dos en el mismo dominio— y vale para
        // todo lo que venga después.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        Application app = Application.Current ?? new Application();

        // SE MONTA EN LOS RECURSOS DE LA APLICACIÓN, como hace `App.xaml`. Un `StaticResource` se
        // resuelve al CARGAR el diccionario y, además de su propio ámbito, mira el de
        // `Application.Current`; un diccionario suelto no tiene ese respaldo, y el `BasedOn` de
        // `Dialog` —que apunta al estilo implícito de la librería— no encontraría nada. Cargarlo
        // como lo carga la aplicación es también lo único que hace que esta prueba mida lo que la
        // aplicación tiene, y no otra cosa parecida.
        var resources = new ResourceDictionary();
        app.Resources = resources;
        resources.MergedDictionaries.Add(
            new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        // SE ADJUNTA ANTES DE CARGAR, y en ese orden. Un `StaticResource` se resuelve al CARGAR el
        // diccionario y solo ve su propio ámbito y el de quien ya lo contiene; si se construye
        // suelto —`new ResourceDictionary { Source = … }`— y se añade después, el `BasedOn` de
        // `Dialog` no encuentra el estilo implícito de la librería y el fichero ni siquiera carga.
        // Es exactamente el orden que usa `App.xaml`, y por eso allí sí resuelve.
        foreach (string src in new[]
                 {
                     "Themes/Converters.xaml", "Themes/Tokens.xaml", "Themes/Palette.Dark.xaml",
                     "Themes/Styles.xaml", "Themes/Pages.xaml",
                 })
        {
            var dictionary = new ResourceDictionary();
            resources.MergedDictionaries.Add(dictionary);
            dictionary.Source = new Uri($"pack://application:,,,/Atalaya;component/{src}", UriKind.Absolute);
        }

        return resources;
    }

    /// <summary>
    /// Los estilos de NUESTROS diccionarios, con su clave. Los de la librería no se auditan: son
    /// los implícitos de los que hay que derivar, no candidatos a derivar de nadie. Se reconocen
    /// por la posición —todo lo que va detrás de los dos de WPF-UI— y no por su <c>Source</c>.
    /// </summary>
    private static IEnumerable<(string Key, Style Style)> OurStyles(ResourceDictionary resources)
    {
        foreach (ResourceDictionary dictionary in resources.MergedDictionaries.Skip(2))
        {
            foreach (object key in dictionary.Keys)
            {
                if (key is string name && dictionary[key] is Style style)
                {
                    yield return (name, style);
                }
            }
        }
    }

    /// <summary>¿Llega este estilo, por su cadena de <c>BasedOn</c>, al de su propio control?</summary>
    private static bool DerivesFrom(Style style, Type targetType)
    {
        for (Style? s = style.BasedOn; s is not null; s = s.BasedOn)
        {
            if (s.TargetType == targetType)
            {
                return true;
            }
        }

        return false;
    }
}
