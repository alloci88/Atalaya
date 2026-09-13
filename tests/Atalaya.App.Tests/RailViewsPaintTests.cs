using System.Text;
using System.Windows.Input;
using Atalaya.App;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>TODAS LAS VISTAS DEL RAÍL SE PINTAN, SIN EXCEPCIÓN</b> (BUGFIX-CUENTA).
/// <para>
/// <b>Qué regla protege.</b> Que no exista ninguna vista alcanzable desde el raíl que reviente al
/// pintarse. Ni una.
/// </para>
/// <para>
/// <b>Qué se rompía en silencio sin este test.</b> Un XAML que el compilador acepta y que estalla
/// al APLICARSE: el estilo que hereda de otro declarado más abajo (F27), el estilo con clave que
/// dejaba a un diálogo sin plantilla (R5), y ahora un <c>SharedSizeGroup</c> cuyo nombre lleva un
/// punto —R-PROV2, <c>SettingsView.xaml</c>—, que WPF rechaza por no ser un identificador. Ninguno
/// de los tres rompe el build. Ninguno de los tres rompe un test de view-model. Los tres cierran
/// la aplicación del usuario: el último, con <b>303 diálogos de error seguidos</b> en el
/// <c>dist</c>. El autochequeo cubría <b>la primera vista</b> (Portafolio) y los diálogos; las
/// otras ocho páginas del raíl no las miraba nadie, y por ese hueco se coló esto y BUGFIX-F36-2.
/// </para>
/// <para>
/// <b>Cómo se prueba.</b> Con el contenedor completo —<c>App.BuildHost</c>, el único sitio donde
/// se monta (D-800)—, el raíl de verdad como lista de vistas (son datos, D-954) y el mismo pincel
/// que usa el autochequeo (<see cref="StartupSelfCheck.PaintPage"/>): medir y colocar a 1440×900,
/// que es donde las plantillas se aplican. Construir no basta; el fallo ocurre en
/// <c>MeasureCore</c> → <c>ApplyTemplate</c>.
/// </para>
/// <para>
/// <b>Y con datos.</b> Una plantilla de fila solo se aplica si hay filas: con el hub en blanco
/// cada vista pintaría su estado vacío —otro XAML— y el barrido pasaría sin haber aplicado una
/// sola plantilla de fila. El hub temporal se siembra con una aplicación, su inventario, un
/// hallazgo, una sesión terminada con sus tokens, un informe y la tabla de tarifas. Medido sobre
/// Ajustes → Tarifas: con las 31 tarifas sembradas el árbol pintado tiene 226 <c>TextBox</c>, y
/// con la tabla vacía, 9 — o sea, las 31 filas se realizan de verdad.
/// </para>
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RailViewsPaintTests : IDisposable
{
    private const string Slug = "atalayaprueba";
    private const string AppName = "Atalaya de prueba";

    /// <summary>Un modelo que la siembra de tarifas trae, para que la sesión tenga precio.</summary>
    private const string SeededModel = "claude-sonnet-4.5";

    private readonly WpfUiThread _ui;
    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public RailViewsPaintTests(WpfUiThread ui, ITestOutputHelper output)
    {
        _ui = ui;
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "atalaya-rail", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // El hub temporal no puede decidir el veredicto de un test.
        }
    }

    /// <summary>
    /// El barrido. Falla <b>con el nombre de la vista</b> y recoge TODAS las que caen, no solo la
    /// primera: «alguna vista revienta» no sirve de nada a las dos de la mañana, y saber que caen
    /// tres vale más que saber que cae una.
    /// </summary>
    [Fact]
    public void Todas_las_vistas_del_rail_se_pintan()
    {
        _ui.LoadAppResources();

        Barrido barrido = _ui.Run(BarrerAsync);

        foreach (string linea in barrido.Pintadas)
        {
            _output.WriteLine("OK  " + linea);
        }

        foreach (string linea in barrido.Rotas)
        {
            _output.WriteLine("MAL " + linea);
        }

        barrido.Rotas.Should().BeEmpty(
            "ninguna vista del raíl puede reventar al pintarse — y las que revientan son:"
            + Environment.NewLine + string.Join(Environment.NewLine, barrido.Rotas));

        // Y EL BARRIDO TIENE QUE HABER BARRIDO. Un raíl que se queda sin entradas —porque la
        // condición que las enseña cambió— dejaría este test en verde sin haber pintado nada, que
        // es la forma más silenciosa de perder una red.
        barrido.Vistas.Should().BeEquivalentTo(
            Esperadas,
            "el raíl es la lista de vistas de esta prueba, y son éstas");
    }

    /// <summary>
    /// Las páginas que el raíl ofrece, por su view-model. <b>No es la lista de la que sale el
    /// barrido</b> —esa la da el raíl, que son datos— sino su contraparte: lo que se espera
    /// encontrar allí. Si el raíl gana o pierde una entrada, este test lo dice y alguien decide;
    /// lo que no puede pasar es que una vista deje de barrerse sin que nadie se entere.
    /// </summary>
    private static readonly string[] Esperadas =
    {
        nameof(PortfolioViewModel),
        nameof(FindingsViewModel),
        nameof(ReportsViewModel),
        nameof(MetricsViewModel),
        nameof(InventoryViewModel),
        nameof(SessionViewModel),
        nameof(AssistedFixViewModel),
        nameof(AccountViewModel),
        nameof(SettingsViewModel),
        nameof(AboutViewModel),
    };

    private sealed record Barrido(
        IReadOnlyList<string> Pintadas,
        IReadOnlyList<string> Rotas,
        IReadOnlyList<string> Vistas);

    // ================================================================ el barrido

    private async Task<Barrido> BarrerAsync()
    {
        var paths = new AppPaths(_root);

        // D-062: antes de construir nada. Un test que escriba en el almacén real es un test que
        // le mete basura en el hub al equipo.
        TestFactory.AssertIsolated(paths);

        AppCulture.Apply();

        IHost host = App.BuildHost(paths);
        await host.StartAsync();

        try
        {
            // El arranque de verdad, en corto: ajustes, migraciones y tema. Es lo que hace
            // `StartupSelfCheck` antes de pintar la primera vista, y sin ello las páginas se
            // pintarían con una configuración que ningún usuario tiene.
            var settings = host.Services.GetRequiredService<SettingsService>();
            settings.Load();
            settings.MigrateConnection(host.Services.GetRequiredService<DeployConfig>());
            ThemeService.Apply(settings.Current.Theme);

            Sembrar(host.Services);

            var shell = host.Services.GetRequiredService<MainViewModel>();

            // EL RAÍL ENSEÑA TODO LO QUE PUEDE ENSEÑAR. Inventario, Sesión y Arreglo son
            // condicionales —solo salen con una aplicación activa y con algo que enseñar—, así que
            // si no se ponen esas condiciones el barrido se dejaría tres vistas fuera y no lo
            // diría. Se pone el estado, no se finge el raíl: lo construye `BuildRail` como siempre.
            shell.ActiveApplication.Set(Slug, AppName);
            shell.HasSession = true;
            shell.HasFix = true;
            shell.RefreshShell();

            var pintadas = new List<string>();
            var rotas = new List<string>();
            var vistas = new List<string>();

            // Se copia la lista: navegar rehace el raíl, y recorrer una colección que se está
            // reconstruyendo debajo es otra avería distinta de la que se busca.
            List<NavItem> entradas = shell.NavGroups.SelectMany(grupo => grupo.Items).ToList();

            foreach (NavItem entrada in entradas)
            {
                ViewModelBase? page;
                try
                {
                    await Navegar(entrada.Command);
                    page = shell.Navigation.Current;
                }
                catch (Exception ex)
                {
                    rotas.Add($"«{entrada.Label}» no se puede ni abrir: {Causa(ex)}");
                    continue;
                }

                if (page is null)
                {
                    rotas.Add($"«{entrada.Label}» no dejó ninguna página delante");
                    continue;
                }

                vistas.Add(page.GetType().Name);

                foreach ((string sufijo, Action antes) in Estados(page))
                {
                    string nombre = $"«{entrada.Label}»{sufijo} ({page.GetType().Name})";
                    try
                    {
                        antes();
                        StartupSelfCheck.PaintPage(page);
                        pintadas.Add(nombre);
                    }
                    catch (Exception ex)
                    {
                        rotas.Add($"{nombre}: {Causa(ex)}");
                    }
                }
            }

            return new Barrido(pintadas, rotas, vistas);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            if (host is IAsyncDisposable asincrono)
            {
                await asincrono.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
    }

    /// <summary>
    /// Cuántas veces se pinta una página, y en qué estado.
    /// <para>
    /// Casi todas, una. <b>Ajustes es la excepción</b>: es la única página que esconde la mitad de
    /// su XAML detrás de un selector de sección, y lo esconde con <c>Collapsed</c> — que no se
    /// mide, así que no aplica plantillas. Pintarla solo en su sección de entrada dejaría sin
    /// mirar cuatro de las cinco, y la tabla de tarifas es una de ellas. Las secciones también son
    /// datos, así que salen de la página y no de una lista escrita aquí.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Sufijo, Action Antes)> Estados(ViewModelBase page)
    {
        if (page is not SettingsViewModel ajustes)
        {
            yield return (string.Empty, () => { });
            yield break;
        }

        foreach (SettingsSectionItem seccion in ajustes.Sections.ToList())
        {
            string clave = seccion.Key;
            yield return ($" → {seccion.Label}", () => ajustes.Section = clave);
        }
    }

    /// <summary>
    /// Pulsa la entrada del raíl y espera a que termine. Los comandos de navegación son
    /// asíncronos: lanzarlos y seguir dejaría la página a medio cargar, y una página a medio
    /// cargar no tiene filas — que es justo lo que hace falta que tenga.
    /// </summary>
    private static async Task Navegar(ICommand command)
    {
        if (command is IAsyncRelayCommand asincrono)
        {
            await asincrono.ExecuteAsync(null);
            return;
        }

        command.Execute(null);
    }

    /// <summary>
    /// La causa, con la de dentro. Un <c>XamlParseException</c> envuelve lo que de verdad falló, y
    /// quedarse con «se produjo una excepción al establecer la propiedad» esconde la única frase
    /// que dice cuál es el valor que no vale.
    /// </summary>
    private static string Causa(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (text.Length > 0)
            {
                text.Append(" ← ");
            }

            text.Append(e.GetType().Name).Append(": ").Append(e.Message.ReplaceLineEndings(" "));
        }

        return text.ToString();
    }

    // ================================================================ los datos

    /// <summary>
    /// <b>EL HUB TEMPORAL, CON ALGO DENTRO.</b> Una vista vacía pinta el estado vacío, que es otro
    /// XAML: con el hub en blanco este barrido no aplicaría ni una plantilla de fila y pasaría
    /// diciendo que ha mirado nueve vistas.
    /// <para>
    /// Lo mínimo para que cada lista tenga al menos un elemento: una aplicación (Portafolio), su
    /// inventario (Inventario), un hallazgo (Hallazgos), una sesión terminada con sus tokens
    /// (Sesión, Métricas), su informe (Informes) y la tabla de tarifas (Ajustes → Tarifas).
    /// </para>
    /// </summary>
    private static void Sembrar(IServiceProvider services)
    {
        var hub = services.GetRequiredService<HubContext>();
        var ulids = services.GetRequiredService<IUlidFactory>();

        hub.Store.WriteHub(new HubInfo { OrganizationName = "Organización de prueba" });
        hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug,
            Name = AppName,
            RepoUrl = "https://example.invalid/org/atalayaprueba.git",
            Stack = TechStack.DotNet,
            CurrentCycle = 1,
        });

        hub.Store.WriteInventory(Slug, new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit
                {
                    Path = "src/Modulo/Unidad.cs",
                    Module = "Modulo",
                    State = UnitState.Pendiente,
                },
            },
        });

        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "ana");
        hub.Store.WriteFinding(Slug, new Finding
        {
            Id = ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Critica,
            Confidence = Confidence.Media,
            Title = "Una conexión que no se cierra",
            Description = "descripción",
            Impact = "impacto",
            Recommendation = "recomendación",
            Locations = { new Location("src/Modulo/Unidad.cs", 12, "sha256:snip") },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });

        Ulid sesion = ulids.NewUlid();
        var session = new AuditSession
        {
            Id = sesion,
            AppSlug = Slug,
            Mode = AuditMode.Lotes,
            By = "ana",
            Machine = "PC",
            Commit = "abc1234",
            Model = SeededModel,
            CycleN = 1,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedUtc = DateTimeOffset.UtcNow,
        };
        session.Usage.Add(12_000, 3_400, 8_000, 1_000, null, calls: 7);
        hub.Store.WriteSession(session);

        hub.Store.WriteReport(Slug, sesion.ToString(), "# Informe de prueba" + Environment.NewLine);

        // LA TABLA DE TARIFAS, por el mismo camino que la aplicación al abrir el hub (R2 §2). Es
        // lo que pone filas en Ajustes → Tarifas, y una fila es lo que aplica su plantilla.
        hub.SeedModelRates();
    }
}
