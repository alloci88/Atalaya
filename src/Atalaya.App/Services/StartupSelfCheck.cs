using System.Reflection;
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
/// <b>Y no abre ventana</b>: comprueba que la carcasa se puede construir —que es donde revientan
/// los errores de XAML, que el compilador no ve— pero no la enseña ni navega a ninguna página, y
/// no toca la red ni el hub. Es un chequeo, no una sesión.
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
