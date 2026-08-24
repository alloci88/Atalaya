using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F3.1 Bloque 1b — regresión que reproduce el ciclo duplicar→resolver observado en la sesión
/// real S3 (`01M0T3H386…`, 2026-08-24) sobre `xblast/CommonStatics.cs` a pesar de tener el
/// <see cref="SecondPassMatcher"/> desplegado. Ver DECISIONS.md D-068..D-075.
/// <para>
/// Escenario mínimo que ejercita las DOS causas raíz:
/// <list type="bullet">
///   <item><b>D-069</b>: <c>FindingIngestionService.Ingest</c> guarda la 2ª pasada tras
///     <c>existing.Count == 0</c>. Como el baseline tiene un gemelo resuelto cuyo hash coincide
///     con el que el LLM calcula en la nueva sesión, la 2ª pasada nunca corre y la ingestión va
///     por la vía de recurrencia — creando un nuevo activo aunque el reabierto legítimo esté ahí.</item>
///   <item><b>D-070</b>: <c>ApplyImplicitResolution</c> mira sólo <c>Finding.Fingerprint</c>, no
///     <c>PreviousFingerprints</c>. El reabierto conserva un hash "obsoleto" respecto al que la
///     sesión reporta, así que sale del set <c>reportedFingerprints</c> y muere por implícita.</item>
/// </list>
/// </para>
/// <para>
/// El test debe FALLAR hoy con la assertion final (new==0 &amp;&amp; resolved==0). Cuando D-073 esté
/// aplicado, debe pasar. Fixture destilado de los JSON reales del hub del piloto (título literal,
/// ruta, ruleId y símbolo del linaje `HexStringToByteArray no valida longitud par`).
/// </para>
/// </summary>
public sealed class PilotS3RegressionTests : IDisposable
{
    private const string Unit = "XBLASTCommon/Class/CommonStatics.cs";
    private const string RuleId = "errores.null.desreferencia";
    // Título literal-idéntico entre reabierto y payload: da Jaccard=1.0 con el tokenizer del
    // SecondPassMatcher, así el test aísla el bug real (orden de condiciones + PreviousFingerprints)
    // sin depender de la calibración del umbral. Es exactamente uno de los pares del D-064
    // ("coincidencia literal de título" — `ConvertToDetId/ConvertToSeq sin manejo de errores…`).
    private const string Title = "ConvertToDetId/ConvertToSeq sin manejo de errores de parseo";
    private const string Symbol = "ConvertToDetId";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public PilotS3RegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-pilot-s3", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(Path.Combine(_clone, "XBLASTCommon", "Class"));
        File.WriteAllText(
            Path.Combine(_clone, "XBLASTCommon", "Class", "CommonStatics.cs"),
            "public static class CommonStatics { public static byte[] HexStringToByteArray(string s) => null!; }");

        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        _hub = TestFactory.Hub(_paths, settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _machines.SetClonePath("xblast", _clone);
        _ingestion = new FindingIngestionService(_hub, _ulids);

        SeedBaseline();
    }

    /// <summary>
    /// Construye el estado post-S2 tal como lo cuenta el reflog del hub del piloto:
    /// <list type="bullet">
    ///   <item>Un <b>reabierto</b> (R) activo por la 2ª pasada, con <c>Fingerprint = A</c> (hash del
    ///     duplicado que S2 le migró) y <c>PreviousFingerprints = [B]</c> (el hash original v4).</item>
    ///   <item>Un <b>gemelo resuelto</b> (G) con <c>Fingerprint = C</c>. El hash C es exactamente
    ///     el que <see cref="Fingerprint.Compute"/> devuelve para el payload que el LLM emitirá en
    ///     la próxima sesión — determinismo puro: mismo ruleId + misma ruta + mismo símbolo.</item>
    /// </list>
    /// </summary>
    private void SeedBaseline()
    {
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast", Name = "XBlast", RepoUrl = "u",
            Stack = TechStack.DotNet, CurrentCycle = 5,
        });
        _hub.Store.WriteInventory("xblast", new InventoryCycle
        {
            CycleN = 5,
            Units = { new InventoryUnit { Path = Unit, Module = "XBLASTCommon", State = UnitState.Pendiente } },
        });

        var stampOld = new DetectionStamp(
            new DateTimeOffset(2026, 8, 21, 12, 44, 48, TimeSpan.Zero),
            AuditMode.Lotes, "commit-v4", "alopezciller");

        // Hash canónico C: el que la sesión nueva calculará (mismo ruleId+path+symbol).
        string canonical = Fingerprint.Compute(RuleId, Unit, Symbol, Title);

        // R = reabierto por S2 con fingerprint migrado a "A" (hash volátil del duplicado S2, distinto
        // del canónico y del original v4). PrevFps guarda B (el original v4).
        var reopened = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = "sha256:" + new string('a', 64),
            RuleId = RuleId,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = Title,
            Locations = { new Location(Unit, 154) },
            Origin = AuditMode.Lotes,
            FirstDetected = stampOld,
            LastConfirmed = stampOld,
            PreviousFingerprints = { "sha256:" + new string('b', 64) },
        };
        _hub.Store.WriteFinding("xblast", reopened);

        // G = gemelo resuelto con el hash canónico C. Este es el que capturará la ingestión de
        // la nueva sesión por la vía de recurrencia si no arreglamos D-069.
        var twin = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = canonical,
            RuleId = RuleId,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Resuelto,
            Title = Title,
            Locations = { new Location(Unit, 153) },
            Origin = AuditMode.Lotes,
            FirstDetected = stampOld,
            LastConfirmed = stampOld,
            Resolved = new ResolutionStamp(
                stampOld.Utc.AddDays(3), ResolutionVia.Implicita, AuditMode.Lotes,
                "commit-s2", "alopezciller", "cubierta por la sesión y no re-reportada"),
        };
        _hub.Store.WriteFinding("xblast", twin);
    }

    /// <summary>
    /// El LLM entrega EXACTAMENTE el mismo problema (mismo ruleId + símbolo + ruta), sólo cambia
    /// la línea y una palabra del título. Con la ingestión actual, la 2ª pasada no se llama
    /// (existe un resuelto con fp coincidente) y crea un nuevo activo por recurrencia; además
    /// resuelve implícitamente al reabierto porque su fp obsoleto no está en <c>reportedFingerprints</c>.
    /// </summary>
    [Fact]
    public async Task S3_new_session_over_unit_with_reopened_and_twin_must_NOT_recreate_the_cycle()
    {
        var agent = new FakeCopilotAgent(auditScript: _ => new[]
        {
            new SubmitFindingArgs(
                RuleId, "errores", "alta",
                Title, // literal-idéntico → Jaccard=1.0
                "desc", "impact", "reco",
                new[] { new SubmitLocation(Unit, 170, "int.Parse(...)") },
                Symbol),
        });

        var coordinator = new SessionCoordinator(_hub, _ingestion, _machines, _ulids, agent);
        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("xblast", AuditMode.Lotes, new[] { Unit }),
            CancellationToken.None);

        // Contrato F3.1 Bloque 1b — el mismo defecto NO puede aparecer como "nuevo" ni el reabierto
        // puede caer por implícita: el linaje entero debe converger en UN sólo activo confirmado.
        IReadOnlyList<Finding> all = _hub.Store.ListFindings("xblast");

        result.Counters.New.Should().Be(0,
            "el payload es el mismo defecto que el reabierto activo; la 2ª pasada debe atraparlo aunque haya un gemelo resuelto con fp coincidente (D-069)");
        result.Counters.Resolved.Should().Be(0,
            "el reabierto lleva PreviousFingerprints; la resolución implícita debe reconocerlo como re-reportado (D-070)");
        result.Counters.Confirmed.Should().BeGreaterThan(0,
            "el reabierto debe reconfirmarse en esta sesión");

        all.Count(f => f.Status == FindingStatus.Activo).Should().Be(1,
            "un único linaje = un único activo tras la sesión");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
