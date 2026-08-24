using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F3.1 Bloque 1b (D-074) · tests de consolidación multi-generación de
/// <see cref="SessionRepairTool"/>. Cubre: plan puro (no toca disco), no fusiona singletons,
/// idempotencia (Apply → Apply → no error), purga de residuos fixture, y verificación en
/// disco (canónico contiene los fps absorbidos).
/// </summary>
public sealed class SessionRepairToolConsolidationTests : IDisposable
{
    private const string Slug = "xblast";
    private const string Unit = "XBLASTCommon/Class/CommonStatics.cs";
    private const string RuleId = "errores.null.desreferencia";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly SessionRepairTool _tool;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly DateTimeOffset _now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    public SessionRepairToolConsolidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-consolidate", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        _hub = TestFactory.Hub(_paths, settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "XBlast", RepoUrl = "u",
            Stack = TechStack.DotNet, CurrentCycle = 5,
        });
        _tool = new SessionRepairTool(_hub);
    }

    private Finding MakeFinding(
        string title, string symbol, FindingStatus status, DateTimeOffset firstDetected,
        string? fingerprintOverride = null)
    {
        var stamp = new DetectionStamp(firstDetected, AuditMode.Lotes, "commit", "op");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = fingerprintOverride ?? Fingerprint.Compute(RuleId, Unit, symbol, title),
            RuleId = RuleId,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = status,
            Title = title,
            Locations = { new Location(Unit, 100) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        if (status == FindingStatus.Resuelto)
        {
            f.Resolved = new ResolutionStamp(
                firstDetected.AddDays(1), ResolutionVia.Implicita, AuditMode.Lotes,
                "commit-r", "op", "cerrado en sesión previa");
        }

        return f;
    }

    [Fact]
    public void PlanConsolidation_is_pure_and_does_not_touch_disk()
    {
        Finding a = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Activo, _now.AddDays(-2));
        Finding b = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Resuelto, _now.AddDays(-3),
            fingerprintOverride: "sha256:" + new string('b', 64));
        _hub.Store.WriteFinding(Slug, a);
        _hub.Store.WriteFinding(Slug, b);

        ConsolidationPlan plan = _tool.PlanConsolidation(Slug, _now);

        plan.Clusters.Should().HaveCount(1);
        _hub.Store.TryReadFinding(Slug, a.Id.ToString()).Should().NotBeNull();
        _hub.Store.TryReadFinding(Slug, b.Id.ToString()).Should().NotBeNull();
    }

    [Fact]
    public void PlanConsolidation_does_not_cluster_singletons()
    {
        Finding lonely = MakeFinding("Bug único sin gemelos", "OnlySymbol",
            FindingStatus.Activo, _now.AddDays(-1));
        _hub.Store.WriteFinding(Slug, lonely);

        ConsolidationPlan plan = _tool.PlanConsolidation(Slug, _now);

        plan.Clusters.Should().BeEmpty("un finding solo no consolida con nadie");
    }

    [Fact]
    public void Apply_absorbs_members_into_canonical_and_persists_fingerprints()
    {
        Finding canonicalSeed = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Activo, _now.AddDays(-2));
        Finding twinResolved = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Resuelto, _now.AddDays(-5),
            fingerprintOverride: "sha256:" + new string('c', 64));
        _hub.Store.WriteFinding(Slug, canonicalSeed);
        _hub.Store.WriteFinding(Slug, twinResolved);

        ConsolidationPlan plan = _tool.PlanConsolidation(Slug, _now);
        ConsolidationResult result = _tool.Apply(plan, _now, "tester");

        result.ClustersConsolidated.Should().Be(1);
        result.FindingsAbsorbed.Should().Be(1);

        // El activo gana como canónico; el resuelto absorbido desaparece del disco.
        Finding? survivor = _hub.Store.TryReadFinding(Slug, canonicalSeed.Id.ToString());
        survivor.Should().NotBeNull();
        survivor!.PreviousFingerprints.Should().Contain(twinResolved.Fingerprint);
        _hub.Store.TryReadFinding(Slug, twinResolved.Id.ToString()).Should().BeNull();
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        Finding a = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Activo, _now.AddDays(-2));
        Finding b = MakeFinding("ConvertToDetId sin manejo de errores de parseo", "ConvertToDetId",
            FindingStatus.Resuelto, _now.AddDays(-5),
            fingerprintOverride: "sha256:" + new string('d', 64));
        _hub.Store.WriteFinding(Slug, a);
        _hub.Store.WriteFinding(Slug, b);

        ConsolidationPlan plan1 = _tool.PlanConsolidation(Slug, _now);
        _tool.Apply(plan1, _now, "tester");

        // 2ª pasada: el plan (recalculado sobre el estado ya consolidado) no encuentra
        // clusters; re-aplicar el plan viejo tampoco debe fallar (idempotencia).
        ConsolidationPlan plan2 = _tool.PlanConsolidation(Slug, _now);
        plan2.Clusters.Should().BeEmpty();

        Action replayOld = () => _tool.Apply(plan1, _now, "tester");
        replayOld.Should().NotThrow("Apply debe ser idempotente cuando el miembro ya fue absorbido");
    }

    [Fact]
    public void PlanConsolidation_lists_fixture_residues_as_purges()
    {
        Finding residue = MakeFinding("test", "S", FindingStatus.Activo, _now.AddDays(-1));
        _hub.Store.WriteFinding(Slug, residue);

        ConsolidationPlan plan = _tool.PlanConsolidation(Slug, _now);

        plan.Purges.Should().ContainSingle(p => p.Ulid == residue.Id.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
