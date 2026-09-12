using Atalaya.Agents.Tests;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>PROV-2 §5 — una casa que no es ninguna de las dos corre un barrido entero.</b>
/// <para>
/// <b>Qué regla protege.</b> Que añadir un proveedor sea declarar el contrato y nada más: el
/// pipeline —inventario, pasadas, hallazgos, sesión, informe y publicación en el hub— tiene que
/// funcionar con una casa que la aplicación no conoce, que no está nombrada en ningún <c>if</c> y
/// que declara sólo el mínimo. El doble vive en <c>Atalaya.Agents.Tests</c>, junto al contrato, y
/// lleva un identificador inventado: nada de Copilot ni de Claude Code participa en esta corrida.
/// </para>
/// <para>
/// <b>Qué se rompería en silencio sin él.</b> Que la entrega siguiente descubra el primer <c>if</c>
/// que faltaba a mitad de la implementación, con medio proveedor escrito. Hasta hoy el único
/// doble que ejercitaba el pipeline completo viajaba dentro del paquete de un proveedor concreto
/// (<c>FakeCopilotAgent</c>, en <c>Atalaya.Copilot</c>), así que «una casa nueva puede auditar»
/// era una creencia y no una medida.
/// </para>
/// <para>
/// <b>La publicación va contra un remoto local <c>--bare</c>, sin red</b> (N-1), y se comprueba
/// desde un clon TESTIGO independiente: que el fichero exista en el clon de trabajo sólo diría
/// que se escribió, no que se publicó.
/// </para>
/// </summary>
public sealed class TerceraCasaBarridoTests : IDisposable
{
    private const string Slug = "app";
    private const string RepoUrl = "https://example.invalid/org/app.git";

    private readonly string _root;
    private readonly string _bare;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public TerceraCasaBarridoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-tercera", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);

        // EL CLON DEL HUB, ANTES DE ESCRIBIR NADA: `git clone` exige un directorio vacío y el hub
        // se escribe dentro de él. Primero el clon del `--bare` local, y con él ya montado se
        // puebla (el mismo orden que HubPublishTimeoutTests).
        _bare = Path.Combine(_root, "remote.git");
        TestGit.Init(_bare, isBare: true);
        _hub.EnsureSync();
        _hub.Sync!.EnsureCloned(_bare);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        // La aplicación de fixture: un clon de verdad con dos unidades que el escáner reconoce.
        _clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(_clone, RepoUrl);
        File.WriteAllText(Path.Combine(_clone, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, "src", "A.cs"), "class A { void M() { } }");
        File.WriteAllText(Path.Combine(_clone, "src", "B.cs"), "class B { void N() { } }");
        _machines.SetClonePath(Slug, _clone);
    }

    public void Dispose()
    {
        _hub.CloseSync();
        try
        {
            // Los `pack` de git quedan en solo lectura: hay que quitarles el atributo antes de
            // borrar, como hace `TempRepo` en los tests de sync.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Un temporal que no se deja borrar no puede tumbar un test que ya ha dicho lo suyo.
        }
    }

    /// <summary>
    /// El barrido de punta a punta con la casa inventada. Se afirma el RESULTADO, no que no
    /// reviente: que el inventario salió del escáner, que las dos unidades quedaron auditadas, que
    /// el hallazgo está en el hub, que hay informe, que la sesión lleva escrito el identificador
    /// inventado —y no el de una casa real ni el vacío del histórico— y que todo eso llegó al
    /// remoto <c>--bare</c>.
    /// </summary>
    [Fact]
    public async Task Una_casa_que_no_es_ninguna_de_las_dos_audita_publica_y_queda_escrita()
    {
        // (1) INVENTARIO — del escáner de verdad, no una lista escrita a mano.
        new InventoryRescanService(_hub, new InventoryScanner()).Rescan(Slug, _clone);
        _hub.Store.TryReadInventory(Slug, 1)!.Units.Select(u => u.Path)
            .Should().BeEquivalentTo("src/A.cs", "src/B.cs");

        // (2) LAS PASADAS — conducidas por un proveedor que la aplicación no conoce de nada.
        var casa = new FakeProvider(
            auditScript: r => r.UnitPath == "src/A.cs"
                ? new[] { Finding("src/A.cs") }
                : Array.Empty<SubmitFindingArgs>(),
            modelName: "modelo-de-pruebas");

        SessionResult result = await new SessionCoordinator(
                _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                _machines, _ulids, casa, _settings)
            .RunAsync(
                new SessionRequest(Slug, AuditMode.Lotes, new[] { "src/A.cs", "src/B.cs" }),
                CancellationToken.None);

        // (3) HALLAZGOS — ingeridos y persistidos como los de cualquiera.
        result.Counters.New.Should().Be(1);
        IReadOnlyList<Finding> findings = _hub.Store.ListFindings(Slug);
        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(Severity.Critica);
        findings[0].FirstDetected.Provider.Should().Be(FakeProvider.Id,
            "el hallazgo dice con qué casa se juzgó, y esa casa no es ninguna de las dos reales");

        // (4) EL INVENTARIO SE CIERRA — las dos unidades auditadas, la reclamación liberada.
        InventoryCycle inventory = _hub.Store.TryReadInventory(Slug, 1)!;
        inventory.Units.Should().OnlyContain(u => u.State == UnitState.Auditada);
        _hub.Store.ListClaims(Slug).Should().BeEmpty();

        // (5) LA SESIÓN Y EL INFORME — con el proveedor inventado escrito en los dos.
        AuditSession session = _hub.Store.ListSessions(Slug).Should().ContainSingle().Subject;
        session.Provider.Should().Be(FakeProvider.Id);
        session.Model.Should().Be("modelo-de-pruebas");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, result.SessionId.ToString()));
        report.Should().Contain(FakeProvider.Id,
            "sin sembrar los nombres, una casa desconocida se escribe por su identificador y no en blanco");

        // (6) PUBLICACIÓN — contra el `--bare` local, comprobada desde un clon TESTIGO (N-1).
        string witnessRoot = Path.Combine(_root, "testigo");
        var witnessPaths = new HubPaths(witnessRoot);
        using var witness = new HubSyncService(witnessPaths, ("Testigo", "testigo@example.invalid"));
        witness.EnsureCloned(_bare);
        witness.Pull();
        var witnessStore = new HubStore(witnessPaths);

        witnessStore.ListSessions(Slug).Should().ContainSingle()
            .Which.Provider.Should().Be(FakeProvider.Id, "lo que llegó al hub lleva la casa escrita");
        witnessStore.ListFindings(Slug).Should().ContainSingle();
        File.Exists(witnessPaths.ReportFile(Slug, result.SessionId.ToString())).Should().BeTrue(
            "el informe también se publica: es lo que el equipo lee");
    }

    /// <summary>
    /// Y no hace falta tocar nada de Copilot para que corra: la casa inventada declara el MÍNIMO
    /// —identificador, nombre y modelo— y todo lo demás se queda en el valor por defecto del
    /// contrato. Si algún camino del barrido necesitara algo más, el test de arriba se cae.
    /// </summary>
    [Fact]
    public void La_casa_inventada_declara_solo_el_minimo()
    {
        IAuditorProvider casa = new FakeProvider();

        casa.ProviderId.Should().Be(FakeProvider.Id).And.NotBe("copilot").And.NotBe("claude-code");
        casa.IsOptional.Should().BeFalse("por defecto");
        casa.IsPresent.Should().BeTrue("por defecto");
        casa.IsFactoryDefault.Should().BeFalse("el de fábrica lo declara quien lo sea, y no es ésta");
        casa.ClaimsUnattributedSessions.Should().BeFalse("el histórico sin atribuir no es suyo");
        casa.Accounting.Should().Be(TokenAccounting.InputIncludesCache, "por defecto");
        casa.Billing.Should().BeSameAs(ProviderBilling.Default, "factura en dólares y no declara nada");
        casa.LegacyModelSettingKey.Should().BeNull("no tiene histórico de ajustes que adoptar");
    }

    private static SubmitFindingArgs Finding(string path)
        => new(
            "errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "class A { void M() { } }") }, "A.M");
}
