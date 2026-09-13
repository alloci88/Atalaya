using System.Text;
using System.Text.RegularExpressions;
using Atalaya.Agents;
using Atalaya.Agents.Tests;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.OpenAI;
using Atalaya.OpenAI.Tests;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>PROV-3 — la casa por API conduce un barrido entero, y la clave no sale del almacén.</b>
/// <para>
/// <b>Por qué estos dos viven aquí y no en <c>Atalaya.OpenAI.Tests</c>.</b> Los dos necesitan el
/// arnés de la aplicación, que es lo único que no cabe en el proyecto del proveedor: el inventario
/// de verdad, el coordinador de sesión, el toolbox que persiste, el hub y su remoto local
/// <c>--bare</c> (N-1) — y, para el segundo, el <c>settings.json</c> que escribe
/// <see cref="SettingsService"/> y el <c>secrets.dat</c> que escribe
/// <see cref="ProviderSecretStore"/>, que viven los dos en <c>Atalaya.App</c>. Es el mismo motivo
/// por el que <c>TerceraCasaBarridoTests</c> (PROV-2) está en este proyecto y no en el del
/// contrato. Lo que sí viaja desde el proyecto del proveedor es el doble del endpoint: se ENLAZA
/// (ver el .csproj), no se copia.
/// </para>
/// <para>
/// <b>Ninguno toca la red</b>: el endpoint es <c>EndpointFalso</c> y el hub es un <c>--bare</c> en
/// una carpeta temporal.
/// </para>
/// </summary>
public sealed class OpenAiBarridoTests : IDisposable
{
    private const string Slug = "app";
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string Modelo = "modelo-de-pruebas";
    private const string UrlBase = "https://api.ejemplo.invalid/v1";

    /// <summary>
    /// Con FORMA DE CLAVE DE VERDAD, y no es un detalle: una clave de mentira («clave», «xxx») se
    /// colaría en un log o en un JSON sin que ningún test lo notara, porque nadie la reconocería al
    /// leerla. Ésta se busca por su valor Y por su patrón.
    /// </summary>
    private const string Clave = "sk-proj-7Qh2Vb9LmR4tXeZ1aK8sJ3nP6dW0yC5uG7iT2oA4fB9lE1rN";

    /// <summary>Cualquier cosa con forma de clave de OpenAI, aunque no sea exactamente la nuestra.</summary>
    private static readonly Regex ConFormaDeClave = new(@"sk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled);

    private readonly List<Casa> _casas = new();

    public void Dispose()
    {
        foreach (Casa casa in _casas)
        {
            casa.Dispose();
        }
    }

    // ============================================================ (1) el barrido entero

    /// <summary>
    /// <b>Qué regla protege.</b> Que el proveedor por API produzca, sobre el mismo guion, <b>los
    /// mismos hallazgos publicados en el hub</b> que cualquier otra casa: el mismo inventario, el
    /// mismo recuento, la misma regla, la misma severidad y la misma ubicación, comprobados desde
    /// un clon TESTIGO del remoto <c>--bare</c> (N-1). Añadir una casa cambia quién propone, no qué
    /// se persiste.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Que el proveedor «funcione» —sesión verde, informe
    /// escrito, nadie protesta— y produzca otra cosa: un hallazgo de menos porque el
    /// <c>submit_findings</c> en lote se perdió entre deltas, una severidad distinta porque el
    /// argumento se parseó a mano, una unidad sin auditar porque el bucle se cortó antes. Nada de
    /// eso da un error: da un hub con menos deuda de la que hay.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_casa_por_api_publica_los_mismos_hallazgos_que_el_mismo_guion_con_otra_casa()
    {
        // ---------------- el guion, uno solo, en las dos corridas ----------------
        SubmitFindingArgs esperado = Hallazgo("src/A.cs");

        // --- CORRIDA A: la casa de pruebas, que ya sabemos que conduce el pipeline (PROV-2 §5).
        Casa testigo = Montar();
        var otraCasa = new FakeProvider(
            auditScript: r => r.UnitPath == "src/A.cs" ? new[] { esperado } : Array.Empty<SubmitFindingArgs>(),
            modelName: Modelo);
        await testigo.BarrerAsync(otraCasa);

        // --- CORRIDA B: la casa por API, con el mismo guion dictado por el endpoint falso.
        Casa porApi = Montar();
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.SubmitFindings, ArgumentosEnLote(esperado)));
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/A.cs","summary":"revisada"}"""));
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/B.cs","summary":"revisada"}"""));

        await porApi.BarrerAsync(Arnes.Proveedor(falso, new OpenAiEndpoint(UrlBase, Modelo), Clave));

        // El guion del endpoint falso va por orden, así que el orden de las unidades importa: si
        // alguna vez cambiara, este test tiene que decirlo aquí y no confundirlo con otra cosa.
        falso.Recibidas.Should().NotBeEmpty();
        falso.Recibidas[0].Cuerpo.Should().Contain("src/A.cs",
            "la primera unidad del barrido es la primera que se pidió");

        // ---------------- lo publicado en los dos hubs, comparado ----------------
        IReadOnlyList<Finding> deLaOtra = testigo.Publicados();
        IReadOnlyList<Finding> deLaApi = porApi.Publicados();

        deLaApi.Select(Retrato).Should().BeEquivalentTo(
            deLaOtra.Select(Retrato),
            "el mismo guion con otra casa publica los MISMOS hallazgos: cambia quién los propone, "
            + "no lo que la aplicación persiste");

        deLaApi.Should().ContainSingle().Which.FirstDetected.Provider
            .Should().Be(OpenAiCompatibleProvider.Id,
                "y el hallazgo dice con qué casa se juzgó, que es lo único que sí cambia");

        porApi.Sesion().Provider.Should().Be(OpenAiCompatibleProvider.Id);
        porApi.Sesion().Model.Should().Be(Modelo);
        porApi.Inventario().Units.Should().OnlyContain(u => u.State == UnitState.Auditada,
            "las dos unidades quedan auditadas, también la que no tenía nada que decir");
    }

    // ============================================================ (3) la clave no sale del almacén

    /// <summary>
    /// <b>Qué regla protege.</b> La clave de API vive en el almacén cifrado de la máquina y en
    /// ningún otro sitio: no en <c>settings.json</c>, no en ningún fichero del clon del hub —que se
    /// publica y lo lee todo el equipo—, no en los logs. Sale de ahí para una cosa y solo una: la
    /// cabecera de la petición.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> La confianza, el primer día. Una clave escrita en
    /// <c>settings.json</c> viaja en cualquier copia de seguridad; una escrita en el hub queda en
    /// el historial de git de la organización para siempre y no se borra revirtiendo el commit; una
    /// escrita en un log se pega tal cual en un informe de incidencia. Ninguna de las tres da un
    /// error: se descubren cuando ya están publicadas.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_clave_no_aparece_ni_en_los_ajustes_ni_en_el_hub_ni_en_los_logs()
    {
        Casa casa = Montar();

        // ---------------- configurar, como lo configura Ajustes ----------------
        var secretos = new ProviderSecretStore(casa.Paths);
        secretos.Set(OpenAiCompatibleProvider.Id, Clave);
        casa.Settings.SetProviderOption(OpenAiCompatibleProvider.Id, OpenAiSettingKeys.BaseUrl, UrlBase);
        casa.Settings.SetProviderOption(
            OpenAiCompatibleProvider.Id, OpenAiSettingKeys.Auth, nameof(OpenAiAuth.Bearer));
        casa.Settings.SetModelFor(OpenAiCompatibleProvider.Id, Modelo);

        // ---------------- y correr ----------------
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.SubmitFindings, ArgumentosEnLote(Hallazgo("src/A.cs"))));
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/A.cs","summary":"revisada"}"""));
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/B.cs","summary":"revisada"}"""));

        var registro = new RegistroEnMemoria();
        OpenAiCompatibleProvider proveedor = Arnes.Proveedor(
            falso,
            new OpenAiEndpoint(
                casa.Settings.ProviderOption(OpenAiCompatibleProvider.Id, OpenAiSettingKeys.BaseUrl)!,
                casa.Settings.ModelFor(OpenAiCompatibleProvider.Id)!,
                OpenAiSettingKeys.AuthOf(
                    casa.Settings.ProviderOption(OpenAiCompatibleProvider.Id, OpenAiSettingKeys.Auth))),
            secretos.Get(OpenAiCompatibleProvider.Id),
            registro);

        await casa.BarrerAsync(proveedor);

        // ---------------- la clave SÍ llegó al endpoint, o esto no prueba nada ----------------
        // Sin esto, un proveedor que no mandara nunca la clave pasaría el test con sobresaliente.
        falso.Recibidas.Should().NotBeEmpty();
        falso.Recibidas.Should().OnlyContain(
            p => p.Cabeceras.Values.Any(v => v.Contains(Clave, StringComparison.Ordinal)),
            "la clave viaja en la cabecera de cada petición; ése es su único destino");

        // ---------------- y no está en ningún otro sitio ----------------
        string ajustes = File.ReadAllText(casa.Paths.SettingsJson);
        SinLaClave(ajustes, casa.Paths.SettingsJson);

        foreach (string fichero in Directory.EnumerateFiles(
                     casa.Paths.Hub, "*", SearchOption.AllDirectories))
        {
            SinLaClave(Legible(fichero), fichero);
        }

        SinLaClave(registro.Todo, "el log del proveedor");

        if (Directory.Exists(casa.Paths.Logs))
        {
            foreach (string fichero in Directory.EnumerateFiles(
                         casa.Paths.Logs, "*", SearchOption.AllDirectories))
            {
                SinLaClave(Legible(fichero), fichero);
            }
        }

        // Y el almacén, que es donde sí vive, no la guarda en claro.
        SinLaClave(Legible(secretos.Location), secretos.Location);
        secretos.Get(OpenAiCompatibleProvider.Id).Should().Be(Clave, "de ahí sí sale, y solo de ahí");
    }

    // ============================================================ lo de andar por casa

    private static void SinLaClave(string texto, string donde)
    {
        texto.Should().NotContain(Clave, "la clave no puede acabar en «{0}»", donde);
        ConFormaDeClave.IsMatch(texto).Should().BeFalse(
            "en «{0}» hay algo con forma de clave de API; si no es la nuestra, es peor", donde);
    }

    /// <summary>El contenido de un fichero como texto, sea texto o no: buscar no puede reventar.</summary>
    private static string Legible(string path)
    {
        try
        {
            return Encoding.Latin1.GetString(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Lo que de un hallazgo tiene que ser igual venga de la casa que venga.</summary>
    private static object Retrato(Finding f)
        => new
        {
            f.RuleId,
            f.Severity,
            f.Title,
            Ubicaciones = f.Locations.Select(l => l.Path + ":" + l.Line).ToArray(),
        };

    private static SubmitFindingArgs Hallazgo(string path)
        => new(
            "errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "class A { void M() { } }") }, "A.M");

    /// <summary>Los argumentos de <c>submit_findings</c>, tal y como los escribiría el modelo.</summary>
    private static string ArgumentosEnLote(SubmitFindingArgs f)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                new
                {
                    ruleId = f.RuleId,
                    pillar = f.Pillar,
                    severity = f.Severity,
                    title = f.Title,
                    description = f.Description,
                    impact = f.Impact,
                    recommendation = f.Recommendation,
                    locations = f.Locations.Select(l => new { path = l.Path, line = l.Line, snippet = l.Snippet }),
                    symbol = f.Symbol,
                },
            },
        });

    private Casa Montar()
    {
        var casa = new Casa(Slug, RepoUrl);
        _casas.Add(casa);
        return casa;
    }

    /// <summary>
    /// El arnés de PROV-2, tal cual: hub local, remoto <c>--bare</c>, clon de la aplicación con dos
    /// unidades que el escáner reconoce, y un barrido de una pasada. Se saca aquí porque este test
    /// lo monta DOS veces —una por casa— y compara lo publicado en los dos hubs.
    /// </summary>
    private sealed class Casa : IDisposable
    {
        private readonly string _root;
        private readonly string _bare;
        private readonly string _clone;
        private readonly string _slug;
        private readonly MachineConfigStore _machines;
        private readonly HubContext _hub;
        private readonly UlidFactory _ulids = new(SystemClock.Instance);

        public Casa(string slug, string repoUrl)
        {
            _slug = slug;
            _root = Path.Combine(Path.GetTempPath(), "atalaya-openai", Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Path.Combine(_root, "local"));
            _machines = new MachineConfigStore(Paths.MachinesJson);
            Settings = new SettingsService(Paths);
            Settings.Load();
            AppSettings s = Settings.Current;
            s.MaxPassesPerUnit = 1;
            Settings.Save(s);

            _hub = TestFactory.Hub(Paths, Settings);

            // El clon del hub ANTES de escribir nada: `git clone` exige un directorio vacío.
            _bare = Path.Combine(_root, "remote.git");
            TestGit.Init(_bare, isBare: true);
            _hub.EnsureSync();
            _hub.Sync!.EnsureCloned(_bare);

            _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
            _hub.Store.WriteApp(new AppConfig
            {
                Slug = slug, Name = "App", RepoUrl = repoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
            });

            _clone = Path.Combine(_root, "clone");
            TestFactory.MakeClone(_clone, repoUrl);
            File.WriteAllText(Path.Combine(_clone, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            Directory.CreateDirectory(Path.Combine(_clone, "src"));
            File.WriteAllText(Path.Combine(_clone, "src", "A.cs"), "class A { void M() { } }");
            File.WriteAllText(Path.Combine(_clone, "src", "B.cs"), "class B { void N() { } }");
            _machines.SetClonePath(slug, _clone);
        }

        public AppPaths Paths { get; }

        public SettingsService Settings { get; }

        public SessionResult? Resultado { get; private set; }

        public async Task BarrerAsync(IAuditorProvider casa)
        {
            new InventoryRescanService(_hub, new InventoryScanner()).Rescan(_slug, _clone);

            Resultado = await new SessionCoordinator(
                    _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                    _machines, _ulids, casa, Settings)
                .RunAsync(
                    new SessionRequest(_slug, AuditMode.Lotes, new[] { "src/A.cs", "src/B.cs" }),
                    CancellationToken.None);
        }

        /// <summary>Lo que llegó al remoto, leído desde un clon TESTIGO independiente (N-1).</summary>
        public IReadOnlyList<Finding> Publicados()
        {
            string witnessRoot = Path.Combine(_root, "testigo", Guid.NewGuid().ToString("N"));
            var witnessPaths = new HubPaths(witnessRoot);
            using var witness = new HubSyncService(witnessPaths, ("Testigo", "testigo@example.invalid"));
            witness.EnsureCloned(_bare);
            witness.Pull();
            return new HubStore(witnessPaths).ListFindings(_slug);
        }

        public AuditSession Sesion() => _hub.Store.ListSessions(_slug).Single();

        public InventoryCycle Inventario() => _hub.Store.TryReadInventory(_slug, 1)!;

        public void Dispose()
        {
            _hub.CloseSync();
            try
            {
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
    }

    /// <summary>Todo lo que el proveedor escribió, para poder buscar una clave dentro.</summary>
    private sealed class RegistroEnMemoria : ILogger
    {
        private readonly StringBuilder _todo = new();

        public string Todo
        {
            get
            {
                lock (_todo)
                {
                    return _todo.ToString();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_todo)
            {
                _todo.AppendLine(formatter(state, exception));
                _todo.AppendLine(state?.ToString());
                _todo.AppendLine(exception?.ToString());
            }
        }
    }
}
