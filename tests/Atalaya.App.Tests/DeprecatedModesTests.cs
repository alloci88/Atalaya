using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.6 §2 — «Integral» y «Superficial» salen de la interfaz, pero NO de los datos.
/// <para>
/// El barrido con reconciliación los dejó sin contenido propio: integral es «seleccionar todo +
/// lotes» y superficial es un tope de una pasada. Lo que se prueba aquí es la otra mitad de la
/// decisión: que retirarlos del UI no rompe nada de lo ya escrito. Las sesiones históricas del hub
/// los referencian y tienen que seguir cargando, contando en métricas y saliendo en informes — un
/// dato que deja de leerse es un dato perdido, y aquí nada se borra (§0, mejora 4).
/// </para>
/// </summary>
public sealed class DeprecatedModesTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public DeprecatedModesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-legacy-modes", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        _hub = TestFactory.Hub(_paths, settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    /// <summary>
    /// Un fichero de sesión tal cual lo escribió una versión anterior de Atalaya: modo retirado,
    /// sin <c>maxPassesPerUnit</c>, sin <c>usageBreakdown</c> y sin los contadores que llegaron
    /// después. Se escribe a mano, sin pasar por el serializador de hoy, porque el objetivo es
    /// justamente comprobar que lo VIEJO se sigue leyendo.
    /// </summary>
    private void WriteLegacySession(string wireMode, long inputTokens)
    {
        string ulid = _ulids.NewUlid().ToString();
        string dir = Path.Combine(_paths.Hub, "apps", "app", "sessions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{ulid}.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "{{ulid}}",
              "appSlug": "app",
              "mode": "{{wireMode}}",
              "by": "maria",
              "machine": "PORTATIL",
              "startedUtc": "2025-11-03T09:12:00Z",
              "endedUtc": "2025-11-03T10:44:00Z",
              "commit": "abc1234",
              "model": "gpt-5",
              "cycleN": 1,
              "units": [
                { "unit": "src/Common.cs", "module": "src", "verdict": "auditada", "summary": "Revisados: A, B." }
              ],
              "counters": { "new": 3, "confirmed": 1, "resolved": 0, "silencedRespected": 0 },
              "usage": { "inputTokens": {{inputTokens}}, "outputTokens": 4000, "cost": 12.5, "currency": "premium requests" },
              "notes": []
            }
            """);
    }

    // ---------------------------------------------------------------- los datos siguen vivos

    [Fact]
    public void Una_sesion_antigua_en_modo_retirado_se_sigue_cargando()
    {
        WriteLegacySession("integral", 100_000);
        WriteLegacySession("superficial", 20_000);

        var sessions = _hub.Store.ListSessions("app");

        sessions.Should().HaveCount(2);
        sessions.Select(s => s.Mode).Should().BeEquivalentTo(new[] { AuditMode.Integral, AuditMode.Superficial });
        sessions.Should().OnlyContain(s => s.By == "maria" && s.Units.Count == 1);
        sessions.Should().OnlyContain(s => s.MaxPassesPerUnit == 0, "no existía cuando se escribieron");
    }

    [Fact]
    public void Y_sigue_contando_en_las_metricas()
    {
        WriteLegacySession("integral", 100_000);
        WriteLegacySession("superficial", 20_000);

        MetricsSummary m = new MetricsQuery(_hub).Build();

        m.InputTokens.Should().Be(120_000, "el gasto de una sesión retirada se gastó igual");
        m.OutputTokens.Should().Be(8_000);
        m.Cost.Should().Be(25m);
    }

    [Fact]
    public void Y_su_informe_se_sigue_generando_entero()
    {
        WriteLegacySession("integral", 100_000);
        AuditSession session = _hub.Store.ListSessions("app").Single();

        string report = ReportBuilder.BuildSessionReport(
            _hub.Store.TryReadApp("app")!, session, Array.Empty<Finding>(), pendingUnits: 4, largeUnits: 1);

        report.Should().Contain("**Modo**: Integral")
            .And.Contain("premium requests")
            .And.Contain("src/Common.cs");
        report.Should().NotContain("Pasadas del barrido", "esa sesión no registraba tope");
    }

    [Fact]
    public void Y_la_ficha_del_hallazgo_los_sigue_nombrando_en_castellano()
    {
        AuditModeNames.Display(AuditMode.Integral).Should().Be("Auditoría integral");
        AuditModeNames.Display(AuditMode.Superficial).Should().Be("Auditoría superficial");
    }

    /// <summary>El ida y vuelta por el serializador de hoy no puede cambiar el valor guardado.</summary>
    [Theory]
    [InlineData(AuditMode.Integral, "integral")]
    [InlineData(AuditMode.Superficial, "superficial")]
    public void El_valor_de_alambre_no_se_toca(AuditMode mode, string wire)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app", Mode = mode, By = "a", Machine = "m",
            StartedUtc = DateTimeOffset.UtcNow, CycleN = 1,
        };

        string json = AtalayaJson.Serialize(session);

        json.Should().Contain($"\"mode\": \"{wire}\"");
        AtalayaJson.Deserialize<AuditSession>(json).Mode.Should().Be(mode);
    }

    // ---------------------------------------------------------------- y fuera de la interfaz

    [Fact]
    public void Los_dos_modos_estan_marcados_como_retirados_y_dicen_por_que()
    {
        AuditModes.IsDeprecated(AuditMode.Integral).Should().BeTrue();
        AuditModes.IsDeprecated(AuditMode.Superficial).Should().BeTrue();
        AuditModes.Deprecation(AuditMode.Superficial).Should().Contain("maxPassesPerUnit");

        AuditModes.IsDeprecated(AuditMode.Lotes).Should().BeFalse("es el que los sustituye");
        AuditModes.IsDeprecated(AuditMode.Verify).Should().BeFalse();
    }

    /// <summary>
    /// El comando desapareció, no se quedó escondido: mientras exista, un atajo o un enlace puede
    /// volver a lanzarlo. La retirada es de verdad o no es.
    /// </summary>
    [Fact]
    public void El_view_model_de_V2_ya_no_ofrece_lanzarlos()
    {
        var commands = typeof(InventoryViewModel).GetProperties().Select(p => p.Name).ToList();

        commands.Should().NotContain("AuditIntegralCommand").And.NotContain("AuditSuperficialCommand");
        commands.Should().Contain("AuditSelectionCommand", "el único lanzamiento que queda");
    }

    [Fact]
    public void Y_la_barra_de_acciones_de_V2_es_exactamente_la_que_dice_F5_6()
    {
        string xaml = InventoryXaml();

        xaml.Should().NotContain("AuditIntegralCommand").And.NotContain("AuditSuperficialCommand");
        foreach (string command in new[]
                 {
                     "AuditSelectionCommand", "SelectPendingCommand", "RescanCommand",
                     "ResetCycleCommand", "ShowFindingsCommand", "ClearSelectionCommand",
                     "ToggleAllGroupsCommand",
                 })
        {
            xaml.Should().Contain(command);
        }
    }

    private static string InventoryXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string path = Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "InventoryView.xaml");
        return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
