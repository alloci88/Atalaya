using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Atalaya.App.Services;

/// <param name="Name">Qué se ha comprobado.</param>
/// <param name="Ok">Si pasó.</param>
/// <param name="Detail">La causa cuando no pasó; una nota útil cuando sí.</param>
public sealed record SelfCheckStep(string Name, bool Ok, string Detail = "")
{
    public override string ToString() => $"{(Ok ? "OK  " : "MAL ")} {Name}{(Detail.Length == 0 ? "" : $" — {Detail}")}";
}

/// <param name="Steps">Lo comprobado, en el orden en que se comprobó.</param>
public sealed record SelfCheckReport(IReadOnlyList<SelfCheckStep> Steps)
{
    public bool Ok => Steps.All(step => step.Ok);

    /// <summary>0 si arranca, 1 si no. Es lo que mira el workflow.</summary>
    public int ExitCode => Ok ? 0 : 1;

    /// <summary>El parte entero, para la consola y para el log del workflow.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine("Atalaya · autochequeo de arranque");
            foreach (SelfCheckStep step in Steps)
            {
                text.AppendLine("  " + step);
            }

            text.AppendLine(Ok
                ? "Arranca."
                : "NO ARRANCA. Este paquete no debe publicarse.");
            return text.ToString();
        }
    }
}

/// <summary>
/// El arranque entero, sin abrir ventana (BUGFIX-ARRANQUE).
/// <para>
/// <b>Por qué existe.</b> La 1.1.3 se publicó con 1.661 tests en verde y no arrancaba: ningún test
/// montaba el contenedor de verdad, así que el grafo de dependencias —lo único que no se puede
/// probar por partes— no lo miraba nadie. Un paquete que no arranca es el único fallo que no
/// admite matices, y ahora tiene su propia prueba: <c>Atalaya.exe --selfcheck</c> hace el arranque
/// completo —cultura, despliegue, contenedor, ajustes y migraciones, TODOS los servicios
/// registrados, los ficheros que tienen que viajar y la carcasa— y devuelve 0 o 1.
/// </para>
/// <para>
/// Lo ejecuta el workflow de release <b>sobre el zip recién comprimido</b>, antes de crear la
/// Release: no sobre la carpeta de compilación, sino sobre lo que se va a descargar. Un binario
/// que no arranca no puede volver a publicarse.
/// </para>
/// <para>
/// <b>Y no abre ventana</b>: construye la carcasa —que es donde revientan los errores de XAML,
/// que el compilador no ve—, <b>mide y coloca la primera vista</b> fuera de pantalla, y no toca la
/// red ni el hub. Es un chequeo, no una sesión.
/// </para>
/// <para>
/// <b>Por qué también se pinta</b> (F27, N-8). Construir no basta: un estilo que hereda de otro
/// declarado más abajo en el mismo diccionario compila, se registra y no falla hasta que alguien
/// lo <i>aplica</i>, y aplicarlo ocurre en la medida. Pasó en F27 con <c>Stat.Number.Sev</c>:
/// build verde, 1.920 tests verdes, autochequeo verde, y el <c>dist</c> se cerraba solo al pintar
/// el Portafolio —la primera pantalla—. Medir y colocar la primera vista con su view-model de
/// verdad cuesta milisegundos y cierra ese hueco.
/// </para>
/// </summary>
public static class StartupSelfCheck
{
    /// <summary>El interruptor. Se escribe una vez y se compara aquí.</summary>
    public const string Switch = "--selfcheck";

    /// <summary>Adónde escribir el parte, además de a la consola.</summary>
    public const string ReportSwitch = "--report";

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>El valor de <c>--report</c>, si se dio.</summary>
    public static string? ReportPath(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], ReportSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// El arranque, paso a paso. Ningún paso lanza: cada uno se apunta con su causa y se sigue,
    /// porque saber que fallan tres cosas vale más que saber que falla la primera.
    /// </summary>
    /// <param name="paths">Dónde vive el estado local. Los tests le dan una carpeta temporal, que
    /// es la forma de reproducir un <b>primer arranque en limpio</b> — que es justamente el caso
    /// que se nos escapó.</param>
    /// <param name="shell">
    /// Si se construye también la carcasa (ventana y su view-model). Requiere hilo STA y una
    /// <see cref="System.Windows.Application"/> viva, así que la aplicación lo pide y un test de
    /// consola no.
    /// </param>
    public static async Task<SelfCheckReport> RunAsync(AppPaths paths, bool shell)
    {
        var steps = new List<SelfCheckStep>();
        IHost? host = null;

        Step(steps, "cultura", AppCulture.Apply);
        Step(steps, "configuración de despliegue", () =>
        {
            DeployConfig deploy = DeployConfig.Load();
            return deploy.AppRepoUrl.Length > 0 ? "appRepoUrl presente" : "sin appRepoUrl";
        });

        try
        {
            host = App.BuildHost(paths);
            await host.StartAsync();
            steps.Add(new SelfCheckStep("contenedor", true));
        }
        catch (Exception ex)
        {
            steps.Add(new SelfCheckStep("contenedor", false, Describe(ex)));
            return new SelfCheckReport(steps);
        }

        try
        {
            Step(steps, "ajustes y migraciones", () =>
            {
                var settings = host.Services.GetRequiredService<SettingsService>();
                settings.Load();
                settings.MigrateConnection(host.Services.GetRequiredService<DeployConfig>());
                settings.MigrateAssistedFixDefault();
                settings.MigrateSweepCapDefault();
                // PROV-2 §2 — las casas, igual que en el arranque de verdad: el chequeo pinta la
                // primera vista, y una vista que nombre un proveedor tiene que nombrarlo como lo
                // nombraría la aplicación. Dos formas de arrancar serían dos grafos que divergen.
                var providers = host.Services.GetRequiredService<AuditorProviderRegistry>();
                ProviderNames.Seed(providers.All);
                settings.AdoptLegacyProviderModels(providers.All);
                return $"tema «{settings.Current.Theme}»";
            });

            steps.Add(ResolveEverything(host.Services, paths, shell));

            steps.Add(PackagedFiles());

            if (shell)
            {
                Step(steps, "tema", () => ThemeService.Apply(
                    host.Services.GetRequiredService<SettingsService>().Current.Theme));
                Step(steps, "carcasa", () =>
                {
                    _ = host.Services.GetRequiredService<ViewModels.MainViewModel>();
                    _ = host.Services.GetRequiredService<MainWindow>();
                });
                Step(steps, "primera vista", () => PaintFirstView(host.Services));
                Step(steps, "diálogos", PaintDialogs);
            }
        }
        finally
        {
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
                if (host is IAsyncDisposable async)
                {
                    await async.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
            catch (Exception)
            {
                // Cerrar el chequeo no puede cambiar su veredicto.
            }
        }

        return new SelfCheckReport(steps);
    }

    /// <summary>
    /// Mide y coloca la PRIMERA VISTA, fuera de pantalla y con su view-model de verdad.
    /// <para>
    /// No se enseña ninguna ventana: la página se mete en un <c>ContentControl</c> suelto, que es
    /// lo que hace que la plantilla de <c>Themes/Pages.xaml</c> la resuelva, y se le pide una
    /// medida y una colocación a 1440×900. Eso aplica las plantillas y resuelve los
    /// <c>StaticResource</c> de los estilos, que es donde estaba el hueco.
    /// </para>
    /// <para>
    /// Devuelve qué se pintó, para que el parte lo diga. Si algo revienta, lo recoge
    /// <c>Step</c> con su causa, como todos los demás pasos.
    /// </para>
    /// </summary>
    private static string PaintFirstView(IServiceProvider services)
    {
        var shell = services.GetRequiredService<ViewModels.MainViewModel>();
        var page = services.GetRequiredService<ViewModels.PortfolioViewModel>();

        PaintPage(page);

        _ = shell;
        return $"Portafolio, medido y colocado a {PaintWidth:0}×{PaintHeight:0}";
    }

    /// <summary>Lo que mide una página al pintarse aquí: una ventana de trabajo corriente.</summary>
    public const double PaintWidth = 1440;

    /// <inheritdoc cref="PaintWidth"/>
    public const double PaintHeight = 900;

    /// <summary>
    /// <b>PINTA UNA PÁGINA</b>: la mete en un <c>ContentControl</c> suelto —que es lo que hace que
    /// la plantilla de <c>Themes/Pages.xaml</c> resuelva su vista— y le pide una medida y una
    /// colocación a <see cref="PaintWidth"/>×<see cref="PaintHeight"/>. No se enseña ninguna
    /// ventana.
    /// <para>
    /// <b>Por qué medir y no solo construir.</b> Instanciar un view-model no toca el XAML, y
    /// resolver la vista tampoco basta: las plantillas se aplican al MEDIR
    /// (<c>MeasureCore</c> → <c>ApplyTemplate</c>), y es ahí donde revientan un estilo que hereda
    /// de otro declarado más abajo (F27) o un <c>SharedSizeGroup</c> cuyo nombre no es un
    /// identificador. Nada de eso lo ve el compilador.
    /// </para>
    /// <para>
    /// Es <b>público</b> porque lo reusa la prueba que barre TODAS las vistas del raíl
    /// (<c>RailViewsPaintTests</c>): si el barrido pintara a su manera, dejaría de decir nada sobre
    /// lo que hace el autochequeo — y el hueco se abriría justo entre los dos.
    /// </para>
    /// </summary>
    public static void PaintPage(object page)
    {
        var host = new System.Windows.Controls.ContentControl
        {
            Content = page,
            Width = PaintWidth,
            Height = PaintHeight,
        };

        host.Measure(new System.Windows.Size(PaintWidth, PaintHeight));
        host.Arrange(new System.Windows.Rect(0, 0, PaintWidth, PaintHeight));
        host.UpdateLayout();
    }

    /// <summary>
    /// <b>Los DIÁLOGOS, uno a uno</b> (R6 §1). Se construyen y se miden; no se muestran.
    /// <para>
    /// <b>Por qué existe.</b> Es la segunda vez que un <c>dist</c> con el build y los tests en verde
    /// revienta al PINTAR: la primera fue un estilo que heredaba de otro declarado más abajo (F27),
    /// y la segunda, un estilo con clave sobre un control de la librería que sustituía al suyo y
    /// dejaba a la ventana sin plantilla — con lo que abrir Directivas cerraba la aplicación. Lo que
    /// las dos tienen en común es que un estilo no falla al compilar ni al registrarse: falla cuando
    /// alguien lo APLICA, y aplicarlo ocurre al medir. La primera vista ya se medía desde F27; los
    /// diálogos no los medía nadie.
    /// </para>
    /// <para>
    /// <b>La lista NO se escribe a mano</b>, se descubre: son las ventanas del ensamblado, y por eso
    /// un diálogo nuevo entra aquí solo. Un listado a mano es un listado que alguien olvidará.
    /// </para>
    /// <para>
    /// Los argumentos del constructor se crean SIN ejecutar el suyo
    /// (<see cref="RuntimeHelpers.GetUninitializedObject"/>): montar un view-model de verdad
    /// exigiría media aplicación por diálogo, y lo que se está probando no es el view-model — es que
    /// el XAML y sus estilos se resuelvan. Los enlaces que no encuentren datos fallan como enlaces,
    /// que es lo que WPF hace en silencio y no lo que se persigue aquí.
    /// </para>
    /// </summary>
    private static string PaintDialogs()
    {
        var painted = new List<string>();
        foreach (Type type in DialogTypes())
        {
            ConstructorInfo ctor = type.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .First();

            object?[] args = ctor.GetParameters()
                .Select(parameter => parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : RuntimeHelpers.GetUninitializedObject(parameter.ParameterType))
                .ToArray();

            var window = (System.Windows.Window)ctor.Invoke(args);

            // NO se crea el handle ni se muestra: probado, no añade nada. El cierre de R5 no salta
            // ni construyendo, ni midiendo, ni con `EnsureHandle` — hace falta una ventana MOSTRADA
            // y activa, y eso no cabe en un autochequeo que corre en una máquina de compilación.
            // Lo que este paso sí cubre es la clase de F27: un estilo que se resuelve mal al
            // APLICARSE, que es lo que ocurre al medir. Del defecto de R5 se encarga una regla
            // estática, que además lo pilla antes: ver `DialogStyleTests`.

            // Un diálogo con `SizeToContent` no declara alto, así que su `Height` es NaN y medir
            // con NaN lanza. Se mide con lo que declare y, donde no declare nada, con el tamaño de
            // un diálogo corriente: lo que se comprueba es que el árbol se construya y los estilos
            // se apliquen, no cuánto mide la ventana.
            var size = new System.Windows.Size(
                double.IsNaN(window.Width) ? 800 : window.Width,
                double.IsNaN(window.Height) ? 600 : window.Height);

            window.Measure(size);
            window.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), size));
            window.UpdateLayout();

            // Y QUE EL TEMA LLEGA DENTRO (R9). Medir prueba que el árbol se construye; esto prueba
            // que las claves de la casa se resuelven DESDE la ventana del diálogo, que es la
            // pregunta que las tres rondas anteriores dejaron sin contestar. Si algún día un
            // diálogo dejara de ver la paleta —por un `Resources` propio que la tape, por abrirse
            // fuera del árbol de la aplicación—, esto se pone rojo antes de que nadie lo abra.
            foreach (string clave in new[] { "Brush.Bg", "FontSize.Body" })
            {
                if (window.TryFindResource(clave) is null)
                {
                    throw new InvalidOperationException(
                        $"{type.Name} no resuelve «{clave}» desde su propio árbol: el diálogo no ve "
                        + "el tema de la aplicación y se pintará con los colores de fábrica.");
                }
            }

            painted.Add(type.Name);
        }

        return painted.Count == 0
            ? "ninguno encontrado"
            : $"{painted.Count} medidos: {string.Join(", ", painted)}";
    }

    /// <summary>
    /// <b>Los diálogos de la aplicación</b>: toda ventana del ensamblado que no sea la principal.
    /// <para>
    /// Público para que una prueba pueda exigir que esta lista sea la lista COMPLETA — si algún día
    /// se escribiera a mano, un diálogo nuevo se quedaría fuera del autochequeo sin que nadie lo
    /// notara, que es exactamente el hueco por el que se coló el cierre de R5.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Type> DialogTypes()
        => typeof(App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(System.Windows.Window).IsAssignableFrom(t)
                        && t != typeof(MainWindow))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <b>El paso que importa.</b> Resuelve TODOS los servicios que la aplicación registra, uno a
    /// uno. El contenedor solo falla cuando alguien pide algo, así que un registro roto —una
    /// dependencia que nadie registró, una fábrica que lanza— no se nota hasta que en el arranque
    /// de verdad alguien la pide. Aquí se piden todas, a propósito y de golpe.
    /// <para>
    /// Se enumeran los registros de la propia aplicación —volviendo a correr
    /// <c>App.ConfigureServices</c> sobre una colección de sonda— y no los del host genérico:
    /// lo que puede romperse es lo nuestro.
    /// </para>
    /// </summary>
    private static SelfCheckStep ResolveEverything(IServiceProvider services, AppPaths paths, bool shell)
    {
        List<Type> registered;
        try
        {
            var probe = new ServiceCollection();
            App.ConfigureServices(probe, paths);
            registered = probe
                .Select(descriptor => descriptor.ServiceType)
                .Where(type => !type.IsGenericTypeDefinition)
                .Distinct()
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            return new SelfCheckStep("inventario de servicios", false, Describe(ex));
        }

        var broken = new List<string>();
        int deferred = 0;
        foreach (Type type in registered)
        {
            // Lo que hereda de DispatcherObject —la ventana— exige hilo STA. Cuando el chequeo
            // corre DENTRO de la aplicación lo hay y se construye; cuando lo corre un test de
            // consola no, y hacerlo fallar ahí sería inventarse una avería. Se aplaza y se dice.
            if (!shell && typeof(System.Windows.Threading.DispatcherObject).IsAssignableFrom(type))
            {
                deferred++;
                continue;
            }

            try
            {
                if (services.GetService(type) is null)
                {
                    broken.Add($"{Name(type)}: el contenedor devolvió null");
                }
            }
            catch (Exception ex)
            {
                broken.Add($"{Name(type)}: {Describe(ex)}");
            }
        }

        string counted = $"{registered.Count - deferred} resueltos"
                         + (deferred == 0 ? string.Empty : $", {deferred} aplazados a la carcasa (hilo STA)");

        return broken.Count == 0
            ? new SelfCheckStep("servicios", true, counted)
            : new SelfCheckStep(
                "servicios",
                false,
                $"{broken.Count} de {registered.Count - deferred} no se pueden resolver · "
                + string.Join(" · ", broken));
    }

    /// <summary>
    /// Lo que TIENE que viajar en el paquete. Un fichero que se queda fuera no rompe la
    /// compilación —por eso se comprueba aquí y no allí— pero deja media aplicación sin funcionar
    /// en la máquina de alguien.
    /// </summary>
    private static SelfCheckStep PackagedFiles()
    {
        string dir = AppContext.BaseDirectory;
        var required = new[]
        {
            "appsettings.deploy.json",
            SelfUpdateService.RunnerExe,
            McpBridge.FileName,
        };

        List<string> missing = required
            .Where(name => !File.Exists(Path.Combine(dir, name)))
            .ToList();

        return missing.Count == 0
            ? new SelfCheckStep("ficheros del paquete", true, $"{required.Length} presentes")
            : new SelfCheckStep("ficheros del paquete", false, $"faltan: {string.Join(", ", missing)}");
    }

    private static void Step(List<SelfCheckStep> steps, string name, Action action)
        => Step(steps, name, () =>
        {
            action();
            return string.Empty;
        });

    private static void Step(List<SelfCheckStep> steps, string name, Func<string> action)
    {
        try
        {
            steps.Add(new SelfCheckStep(name, true, action()));
        }
        catch (Exception ex)
        {
            steps.Add(new SelfCheckStep(name, false, Describe(ex)));
        }
    }

    private static string Name(Type type)
        => type.IsGenericType
            ? $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(a => a.Name))}>"
            : type.Name;

    /// <summary>
    /// La causa, con la de dentro. Una fábrica del contenedor envuelve lo que lanzó, y quedarse con
    /// «no se pudo construir X» esconde justo la frase que dice por qué.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is TargetInvocationException)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(" ← ");
            }

            text.Append(e.GetType().Name).Append(": ").Append(e.Message.ReplaceLineEndings(" "));
        }

        return text.ToString();
    }
}
