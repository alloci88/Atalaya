using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R12 — <b>EL IDENTIFICADOR ES DATO, NO RÓTULO</b>.
/// <para>
/// <b>El defecto que lo trae.</b> La misma aplicación se llamaba de dos maneras en la misma
/// ventana: Portafolio e Inventario decían «XBLAST» —el nombre— y la sesión en vivo decía
/// «xblast» —el identificador del hub— en la miga, en la cabecera («Lotes · xblast») y en la barra
/// de estado («Auditando xblast»). No es una errata de una vista: es que el identificador, que
/// sirve para leer y escribir en el hub, se estaba usando para rotular.
/// </para>
/// <para>
/// <b>Por qué se rompe en silencio.</b> El identificador SIEMPRE está a mano —es lo que viaja en
/// las peticiones— y el nombre hay que ir a buscarlo al hub. Así que la forma cómoda de escribir
/// una etiqueta es también la incorrecta, no falla nunca, y solo se nota cuando alguien ve las dos
/// pantallas seguidas. Por eso hay test, y por eso mira las DOS puertas por las que puede volver a
/// entrar: la plantilla que enlaza el identificador, y el view-model que se lo da ya mascado.
/// </para>
/// </summary>
public sealed class AppNamingTests : IDisposable
{
    // ================================================================ la puerta de las plantillas

    /// <summary>
    /// <b>Ninguna plantilla enlaza el identificador de una aplicación donde se lee.</b> Un
    /// <c>{Binding Slug}</c> en un <c>Text</c> es el defecto en su forma más directa, y hoy no hay
    /// ninguno: esto cierra la puerta, no la abre.
    /// <para>
    /// Solo las posiciones donde el enlace se PINTA —texto, contenido, cabecera, tooltip—. Un
    /// identificador puede seguir viajando por un <c>CommandParameter</c> o por un
    /// <c>Tag</c>: ahí es lo que tiene que ser, la llave con la que se abre la aplicación.
    /// </para>
    /// </summary>
    [Fact]
    public void Ninguna_plantilla_enlaza_el_identificador_de_una_aplicacion_en_un_texto_visible()
    {
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Atalaya.App"), "*.xaml", SearchOption.AllDirectories))
        {
            string markup = Regex.Replace(
                File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (Match m in Regex.Matches(
                         markup, @"\b(Text|Content|Header|ToolTip)\s*=\s*""\{Binding ([^}""]+)\}"""))
            {
                // La RUTA del enlace, sin sus modificadores: `Path=`, `Mode=OneWay`, converters…
                string path = m.Groups[2].Value.Split(',')[0].Replace("Path=", string.Empty).Trim();

                if (Regex.IsMatch(path, @"(^|\.)([A-Za-z]*App)?Slug$"))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}=«{path}»");
                }
            }
        }

        offenders.Should().BeEmpty(
            "donde se enseña una aplicación se enseña su NOMBRE; el identificador es la llave del "
            + "hub y no un rótulo:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ================================================================ y la puerta del view-model

    /// <summary>
    /// <b>Y los tres sitios donde se coló dicen el nombre.</b> Aquí no vale mirar el XAML: la
    /// sesión no enlaza ningún identificador —se lo daban ya escrito—, así que este es el único
    /// lado desde el que el defecto se ve.
    /// <para>
    /// Los tres salen de la misma sesión y son los tres que el usuario leyó a la vez: el rótulo
    /// que la carcasa pone en la miga y en el grupo del raíl (<c>AppLabel</c>), el subtítulo de la
    /// cabecera (<c>HeaderText</c>) y la línea de la barra de estado (<c>ProgressLine</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_sesion_dice_el_NOMBRE_de_la_aplicacion_en_la_miga_la_cabecera_y_el_pie()
    {
        LiveSessionService live = NewLive();

        // La barra de estado solo habla mientras la sesión corre, así que se lee AHÍ: en cuanto
        // termina, `ProgressLine` es la cadena vacía y no probaría nada. Se engancha a `Changed`,
        // que es lo que el servicio dispara cuando la sesión ya tiene identidad — `IsRunning` se
        // levanta un par de líneas antes, dentro del cerrojo, y ahí todavía no hay qué nombrar.
        string? enMarcha = null;
        live.Changed += () =>
        {
            if (enMarcha is null && live.IsRunning && live.ProgressLine.Length > 0)
            {
                enMarcha = live.ProgressLine;
            }
        };

        await live.StartAsync(new SessionRequest(Slug, AuditMode.Lotes, new[] { "A.cs" }), new[] { "A.cs" });

        var vm = new SessionViewModel(live);

        vm.AppLabel.Should().Be(Name, "de aquí salen la miga y el rótulo del grupo del raíl");
        live.HeaderText.Should().Contain(Name).And.NotContain(Slug, "la cabecera dice «Lotes · XBLAST»");
        enMarcha.Should().NotBeNull("la sesión tiene que haber corrido para que el pie diga algo");
        enMarcha.Should().Contain(Name).And.NotContain(Slug, "y la barra de estado, «Auditando XBLAST»");
    }

    // ================================================================ andamiaje

    private const string Slug = "xblast";
    private const string Name = "XBLAST";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly SettingsService _settings;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public AppNamingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-naming", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));

        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath(Slug, _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = Name, RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory(Slug, new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
    }

    /// <summary>Con el HUB puesto, que es de donde sale el nombre: sin él no habría nada que leer.</summary>
    private LiveSessionService NewLive()
    {
        IAuditorProvider auditor = new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>());
        return new LiveSessionService(
            () => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, auditor, _settings),
            () => auditor,
            new OpenSessionStore(_paths),
            _hub);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Un temporal que no se deja borrar no invalida nada de lo que se ha medido.
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
