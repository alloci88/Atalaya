using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.6 — «Verificar ahora» sobre un hallazgo ARREGLADO tiene que reconocer el arreglo.
/// <para>
/// El parte real: BUG-0003 (<c>HexStringToByteArray</c>) se arregló siguiendo la recomendación del
/// propio hallazgo, con commit y push hechos. Al pulsar «Verificar ahora» la aplicación contestó
/// «no se pudo verificar» y anotó «Reabierto — no localizado tras cambios del código» tres veces.
/// Las tres cosas estaban mal: el verify se rendía en la fase de ANCLAJE sin llegar a preguntar,
/// llamaba «reabrir» a lo que le pasa a un hallazgo activo, y avisaba sin causa.
/// </para>
/// <para>
/// El código malo desaparecido no es un rastro perdido: es el aspecto normal de un arreglo.
/// </para>
/// </summary>
public sealed class VerifyJudgmentTests : IDisposable
{
    private const string Malo = """
        class Hex
        {
            /// <summary>Convierte una cadena hexadecimal en bytes.</summary>
            public static byte[] HexStringToByteArray(string hex)
            {
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
                }

                return bytes;
            }
        }
        """;

    /// <summary>El arreglo tal y como lo recomendaba el hallazgo: null, longitud par y la API nueva.</summary>
    private const string Arreglado = """
        class Hex
        {
            /// <summary>Convierte una cadena hexadecimal en bytes.</summary>
            public static byte[] HexStringToByteArray(string hex)
            {
                ArgumentNullException.ThrowIfNull(hex);
                if (hex.Length % 2 != 0)
                {
                    throw new ArgumentException("La longitud tiene que ser par.", nameof(hex));
                }

                return Convert.FromHexString(hex);
            }
        }
        """;

    private const string SinRastro = """
        class Hex
        {
            // El fichero se rehizo entero y aquí ya no queda nada de aquello.
            public static int Sumar(int a, int b) => a + b;
        }
        """;

    private readonly string _root;
    private readonly string _clone;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public VerifyJudgmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-verify", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        var paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        _hub = TestFactory.Hub(paths, settings);
        _machines = new MachineConfigStore(paths.MachinesJson);
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    /// <summary>La línea con el defecto, que es a la que el auditor ancló el hallazgo.</summary>
    private const string LineaMala =
        "            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);";

    /// <summary>
    /// Siembra el hallazgo real: anclado a la línea mala, con su símbolo y con el contenido de la
    /// unidad sellado en <c>lastConfirmed</c> — que es la segunda capa de la guarda de evidencia.
    /// </summary>
    private Finding Seed(string contenido, string? symbol = "Hex.HexStringToByteArray", string? title = null)
    {
        string file = Path.Combine(_clone, "Hex.cs");
        File.WriteAllText(file, contenido);

        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-1), AuditMode.Lotes, "abc1234", "alvaro",
            HashUtil.Sha256Hex(File.ReadAllBytes(file)));

        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0003",
            RuleId = "errores.calculo.negocio",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = title ?? "HexStringToByteArray no valida la entrada",
            Description = "No comprueba null ni longitud par antes de convertir.",
            Recommendation = "Usa ArgumentNullException.ThrowIfNull, valida la longitud par y Convert.FromHexString.",
            Locations = { new Location("Hex.cs", 8, CodeAnchor.ComputeSnippetHash(LineaMala)) },
            Symbol = symbol,
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        f.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, "alvaro", "detected via Lotes"));
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    private void Rewrite(string contenido) => File.WriteAllText(Path.Combine(_clone, "Hex.cs"), contenido);

    private Finding Read(Finding f) => _hub.Store.TryReadFinding("app", f.Id.ToString())!;

    private VerifyCoordinator Coordinator(IAuditorProvider agent)
        => new(_hub, _machines, _ulids, agent);

    // ------------------------------------------------------------------ (1) el caso real

    [Fact]
    public async Task Ancla_perdida_con_simbolo_presente_y_unidad_cambiada_resuelve_con_evidencia()
    {
        Finding f = Seed(Malo);
        Rewrite(Arreglado); // el usuario aplicó el fix: el código malo ya no existe

        VerifyTarget? visto = null;
        var agent = new FakeCopilotAgent(verdictScript: t =>
        {
            visto = t;
            return "resuelto";
        });

        VerifyOutcome outcome = await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        // Se llegó a PREGUNTAR, y se preguntó por el código de ahora.
        visto.Should().NotBeNull();
        visto!.Basis.Should().Be(VerifyBasis.Simbolo);
        visto.Member.Should().Be("Hex.HexStringToByteArray");
        visto.Snippet.Should().Contain("Convert.FromHexString").And.NotContain("byte.Parse");
        visto.Recommendation.Should().Contain("Convert.FromHexString");

        Finding after = Read(f);
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Verify);
        after.Resolved.Justification.Should().NotBeNullOrWhiteSpace();
        after.NeedsReview.Should().BeFalse();
        outcome.Applied.Should().Be(1);

        // UN evento nuevo, y es el veredicto — ni «reabierto», ni un re-anclaje coreando lo mismo.
        after.History.Should().HaveCount(2);
        after.History[^1].Event.Should().Be(FindingEvent.Resolved);
        after.History.Should().NotContain(h => h.Event == FindingEvent.Reopened);

        outcome.Toast.Should().Contain("resuelto").And.NotContain("no se pudo");
    }

    [Fact]
    public async Task El_prompt_le_dice_al_auditor_que_juzgue_el_codigo_de_ahora()
    {
        Finding f = Seed(Malo);
        Rewrite(Arreglado);

        string prompt = string.Empty;
        var agent = new ScriptedAgent(req =>
        {
            prompt = req.Prompt;
            return "resuelto";
        });

        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        prompt.Should().Contain("el código anclado YA NO ESTÁ");
        prompt.Should().Contain("Hex.HexStringToByteArray");
        prompt.Should().Contain("NO es motivo de «no-verificable»");
        prompt.Should().Contain("Convert.FromHexString");
    }

    // ------------------------------------------------------------------ (2) la guarda, intacta

    [Fact]
    public async Task Simbolo_presente_sin_cambios_degrada_el_arreglado_a_presente()
    {
        Finding f = Seed(Malo); // no se toca el fichero: la unidad es la misma que se vio

        var agent = new FakeCopilotAgent(verdictScript: _ => "resuelto");
        VerifyOutcome outcome = await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = Read(f);
        after.Status.Should().Be(FindingStatus.Activo, "sin cambio en la unidad nada puede haberse arreglado");
        after.Resolved.Should().BeNull();
        after.History.Should().Contain(h => (h.Detail ?? string.Empty).Contains("degradado a presente"));
        outcome.Toast.Should().Contain("degradado a presente");
    }

    // ------------------------------------------------------------------ (3) sin símbolo

    [Fact]
    public async Task Simbolo_desaparecido_con_unidad_cambiada_se_juzga_sobre_la_unidad()
    {
        Finding f = Seed(Malo);
        Rewrite(SinRastro); // ni el código anclado ni el método: pero el fichero SÍ cambió

        VerifyTarget? visto = null;
        var agent = new FakeCopilotAgent(verdictScript: t =>
        {
            visto = t;
            return "confirmado";
        });

        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        visto.Should().NotBeNull();
        visto!.Basis.Should().Be(VerifyBasis.Unidad);
        visto.Snippet.Should().Contain("public static int Sumar");

        Finding after = Read(f);
        after.NeedsReview.Should().BeFalse("se pudo juzgar: no hubo nada que revisar a mano");
        after.History.Should().NotContain(h => h.Event == FindingEvent.NotLocated);
    }

    [Fact]
    public async Task Simbolo_desaparecido_y_unidad_sin_cambios_es_no_localizado_con_su_etiqueta()
    {
        // El hallazgo se vio por última vez contra ESTE contenido, y su ancla ya no casaba
        // entonces: no hay ancla, no hay símbolo y no hay nada nuevo que juzgar.
        Finding f = Seed(SinRastro, symbol: null, title: "conversion floja");

        var agent = new FakeCopilotAgent(verdictScript: _ => "resuelto");
        VerifyOutcome outcome = await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = Read(f);
        after.NeedsReview.Should().BeTrue();
        after.Status.Should().Be(FindingStatus.Activo, "«no localizado» nunca es «resuelto»");
        after.Resolved.Should().BeNull();
        outcome.Applied.Should().Be(0);

        HistoryEntry last = after.History[^1];
        last.Event.Should().Be(FindingEvent.NotLocated);
        after.History.Should().NotContain(h => h.Event == FindingEvent.Reopened);
        FindingEventNames.Display(last.Event).Should().Be("No localizado");

        outcome.Toast.Should().Contain("no localizado").And.NotContain("no se pudo verificar");
    }

    // ------------------------------------------------------------------ (4) el historial sin eco

    [Fact]
    public async Task Verificaciones_identicas_consecutivas_se_anotan_una_sola_vez()
    {
        Finding f = Seed(SinRastro, symbol: null, title: "conversion floja");
        VerifyCoordinator verify = Coordinator(new FakeCopilotAgent());

        for (int i = 0; i < 3; i++)
        {
            await verify.RunAsync("app", new[] { f.Id }, CancellationToken.None);
        }

        Finding after = Read(f);
        after.History.Where(h => h.Event == FindingEvent.NotLocated).Should().ContainSingle()
            .Which.Detail.Should().EndWith("(×3)");
        after.History.Should().HaveCount(2, "la detección y una sola línea de «no localizado»");
    }

    [Fact]
    public void El_eco_solo_se_colapsa_contra_el_evento_inmediatamente_anterior()
    {
        var utc = DateTimeOffset.UtcNow;
        var f = new Finding
        {
            RuleId = "r",
            Title = "t",
            FirstDetected = new DetectionStamp(utc, AuditMode.Lotes, "abc", "alvaro"),
            LastConfirmed = new DetectionStamp(utc, AuditMode.Lotes, "abc", "alvaro"),
        };

        f.Record(new HistoryEntry(utc, FindingEvent.NotLocated, "alvaro", "no localizado"));
        f.Record(new HistoryEntry(utc, FindingEvent.NotLocated, "alvaro", "no localizado"));
        f.Record(new HistoryEntry(utc, FindingEvent.Commented, "alvaro", "mirando esto"));
        f.Record(new HistoryEntry(utc, FindingEvent.NotLocated, "alvaro", "no localizado"));

        f.History.Should().HaveCount(3);
        f.History[0].Detail.Should().Be("no localizado (×2)");
        f.History[2].Detail.Should().Be("no localizado", "en cuanto pasa otra cosa vuelve a ser noticia");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Un agente que además deja mirar el prompt que se le mandó.</summary>
    /// <summary>
    /// Verificar CUESTA, y esa factura tiene que quedar escrita en la sesión. No se anotaba: la
    /// sesión de verify se guardaba con <c>usage</c> a cero, así que en el panel de métricas cada
    /// verificación parecía gratis y el coste del periodo salía corto por todo lo que verificar
    /// gasta. Es el complemento del cuadre de Métricas: allí se suma TODA sesión con coste, y aquí
    /// se garantiza que la verificación traiga el suyo.
    /// </summary>
    [Fact]
    public async Task Una_verificacion_registra_lo_que_gasta_en_su_sesion()
    {
        Finding f = Seed(Malo);
        Rewrite(Arreglado);

        var agent = new ScriptedAgent(
            _ => "resuelto",
            new UsageSample(1200, 340, 4.5m, "fake-model", CacheReadTokens: 90, CostUnit: "unidades SDK"));

        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        AuditSession session = _hub.Store.ListSessions("app").Single(x => x.Mode == AuditMode.Verify);
        session.Usage.Cost.Should().Be(4.5m);
        session.Usage.InputTokens.Should().Be(1200);
        session.Usage.OutputTokens.Should().Be(340);
        session.Usage.CacheReadTokens.Should().Be(90);
        session.Usage.Currency.Should().Be("unidades SDK");
    }

    private sealed class ScriptedAgent : IAuditorProvider
    {
        private readonly Func<VerifyRequest, string> _script;
        private readonly UsageSample _usage;

        public ScriptedAgent(Func<VerifyRequest, string> script, UsageSample? usage = null)
        {
            _script = script;
            _usage = usage ?? new UsageSample(0, 0, null, "fake-model");
        }

        public string? ModelName => "fake-model";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        {
            string verdict = _script(request);
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(_usage);
            foreach (VerifyTarget t in request.Targets)
            {
                toolbox.SubmitVerdict(t.FindingUlid, verdict, "el método ahora valida y usa Convert.FromHexString");
            }

            return Task.CompletedTask;
        }
    }
}
