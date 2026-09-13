using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Markup;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>EL HILO DE INTERFAZ de la suite</b>: uno, STA, con su despachador corriendo y con LA
/// <see cref="Application"/> encima.
/// <para>
/// <b>Por qué hace falta.</b> xUnit corre los tests en hilos MTA del pool, y WPF pide STA para
/// construir controles. Hasta ahora bastaba con levantar un hilo suelto por test
/// (<c>ViewLayout.OnUiThread</c>), porque lo que se montaba eran controles sueltos sin aplicación
/// detrás. Una vista de verdad no: necesita <see cref="Application.Current"/> —de ahí salen las
/// plantillas de <c>Themes/Pages.xaml</c> y las claves del tema— y WPF admite UNA por dominio, con
/// afinidad de hilo. Si cada test creara la suya en su hilo, el primero que la creara dejaría a
/// los demás marshalando hacia un hilo que no bombea: eso no da un rojo, da un <b>bloqueo</b> —
/// que es justamente el aviso que lleva escrito <see cref="AppCollection"/>.
/// </para>
/// <para>
/// Por eso vive como <b>fixture de la colección</b>: xUnit la construye antes que cualquier test
/// de «aplicación», así que la <see cref="Application"/> nace SIEMPRE en este hilo y siempre en el
/// mismo, la vea quien la vea. El despachador se queda bombeando (<see cref="Dispatcher.Run"/>),
/// que es lo que permite <c>await</c> dentro del trabajo de interfaz sin inventarse un contexto de
/// sincronización.
/// </para>
/// <para>
/// <b>Y los diccionarios salen de <c>App.xaml</c>, leído del repositorio</b>, no de una lista
/// copiada aquí: se probó con una copia y se quedó atrás a la primera —faltaban
/// <c>Themes/Conversation.xaml</c> y <c>Themes/StepList.xaml</c>, y la vista del arreglo asistido
/// salía «rota» por un defecto del andamio y no del producto—. Cargar <c>App.xaml</c> con su
/// <c>InitializeComponent</c> sería mejor todavía y no se puede: sus URIs no nombran ensamblado,
/// así que se resuelven contra <c>Application.ResourceAssembly</c>, y en un proceso de pruebas eso
/// ya vale <c>testhost</c> antes de que nadie llegue a cambiarlo.
/// </para>
/// </summary>
public sealed class WpfUiThread : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher _dispatcher = null!;

    public WpfUiThread()
    {
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;

        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;

                // `Current ??` y no `new` a secas: dos instancias en el mismo dominio lanzan, y lo
                // que se quiere aquí es SER el dueño, no competir por serlo.
                Application app = Application.Current
                    ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                LoadAppResourcesInto(app);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "atalaya-ui",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// Devuelve a <c>Application.Current</c> los recursos de la aplicación. Hay tests en esta
    /// misma colección que los sustituyen por los suyos para auditar estilos
    /// (<c>DialogStyleTests</c>), y quien vaya a pintar una vista los necesita enteros.
    /// </summary>
    public void LoadAppResources() => Invoke(() => LoadAppResourcesInto(Application.Current));

    /// <summary>Corre algo en el hilo de interfaz y devuelve su excepción al hilo del test.</summary>
    public void Invoke(Action action)
    {
        Exception? failure = null;
        _dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// Igual, para trabajo ASÍNCRONO. El hilo del test espera en un evento —no en el despachador—
    /// para que el despachador pueda seguir bombeando las continuaciones de los <c>await</c>.
    /// </summary>
    public T Run<T>(Func<Task<T>> body)
    {
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();

        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                result = await body().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        return result;
    }

    public void Dispose() => _dispatcher.InvokeShutdown();

    /// <summary>
    /// Los recursos de la aplicación, montados COMO LOS MONTA <c>App.xaml</c> y en su orden.
    /// <para>
    /// Los dos primeros son código porque en <c>App.xaml</c> tampoco son rutas —son los
    /// diccionarios de la librería— y van delante por lo mismo que allí: la paleta de la casa
    /// reescribe después las claves de WPF-UI (D-945). El resto sale del fichero.
    /// </para>
    /// <para>
    /// El diccionario se cuelga de la aplicación VACÍO y se llena después, y cada hoja se adjunta
    /// ANTES de darle su <c>Source</c>: un <c>StaticResource</c> se resuelve al CARGAR y solo ve
    /// su propio ámbito, el de quien ya lo contiene y el de <c>Application.Current</c>. Llenarlo
    /// suelto y colgarlo al final deja sin resolver todo lo que mira hacia arriba — y eso no
    /// revienta al cargar la hoja, revienta al pintar, con un «UnsetValue no es un valor válido»
    /// en cualquier vista.
    /// </para>
    /// </summary>
    private static void LoadAppResourcesInto(Application app)
    {
        // Sin nombrar esto, WPF ni reconoce el esquema `pack://` en un proceso que no es una
        // aplicación de ventana.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        var resources = new ResourceDictionary();
        app.Resources = resources;
        resources.MergedDictionaries.Add(
            new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        resources.MergedDictionaries.Add(new ControlsDictionary());

        foreach (string source in AppXamlDictionaries())
        {
            var dictionary = new ResourceDictionary();
            resources.MergedDictionaries.Add(dictionary);
            dictionary.Source = new Uri(
                $"pack://application:,,,/Atalaya;component/{source}", UriKind.Absolute);
        }
    }

    /// <summary>Las rutas que <c>App.xaml</c> declara, en su orden.</summary>
    private static IReadOnlyList<string> AppXamlDictionaries()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string xaml = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Atalaya.App", "App.xaml"));

        List<string> sources = Regex
            .Matches(xaml, "Source=\"pack://application:,,,/([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "App.xaml no declara ningún diccionario: o cambió de forma, o esta lectura dejó de "
                + "valer — y un hilo de interfaz sin los estilos de la casa pinta otra aplicación.");
        }

        return sources;
    }
}
