using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// F17 §2 — la temática viaja en el hallazgo, en el ciclo y en la sesión, y lo que ya estaba
/// escrito en el hub sin ella se lee como General: era la única mirada que existía. La migración
/// es aditiva —ningún fichero se reescribe—; lo que este fichero fija es que un JSON anterior a
/// F17 sigue cargando y que uno nuevo escribe la clave <c>tematica</c> con su wire-value.
/// </summary>
public class ThemeSerializationTests
{
    [Fact]
    public void El_hallazgo_escribe_su_tematica_y_la_lee_de_vuelta()
    {
        Finding original = Samples.Finding();
        original.Theme = AuditTheme.Rendimiento;

        string json = AtalayaJson.Serialize(original);
        Finding back = AtalayaJson.Deserialize<Finding>(json);

        json.Should().Contain("\"tematica\": \"rendimiento\"");
        back.Theme.Should().Be(AuditTheme.Rendimiento);
    }

    /// <summary>Un hallazgo anterior a F17 —sin la clave— es General, que es lo que era.</summary>
    [Fact]
    public void Un_hallazgo_anterior_a_F17_se_lee_como_General()
    {
        string json = AtalayaJson.Serialize(Samples.Finding()).Replace("\"tematica\"", "\"ignorado\"");

        AtalayaJson.Deserialize<Finding>(json).Theme.Should().Be(AuditTheme.General);
    }

    [Fact]
    public void El_ciclo_escribe_su_configuracion_entera()
    {
        InventoryCycle inv = Samples.Inventory(2, ("src/A.cs", UnitState.Pendiente));
        inv.Theme = AuditTheme.Seguridad;
        inv.PreferredProvider = "claude-code";
        inv.PreferredModel = "opus";
        inv.OpenedUtc = Samples.T0;

        string json = AtalayaJson.Serialize(inv);
        InventoryCycle back = AtalayaJson.Deserialize<InventoryCycle>(json);

        json.Should().Contain("\"tematica\": \"seguridad\"")
            .And.Contain("\"preferredModel\": \"opus\"")
            .And.Contain("\"openedUtc\"");
        back.Config.Should().Be(new CycleConfig(AuditTheme.Seguridad, "claude-code", "opus"));
        back.OpenedUtc.Should().Be(Samples.T0);
    }

    /// <summary>F17.1 — el historial de temáticas viaja entero, con autor y fechas.</summary>
    [Fact]
    public void El_ciclo_escribe_su_historial_de_tematicas()
    {
        InventoryCycle inv = Samples.Inventory(1, ("src/A.cs", UnitState.Pendiente));
        inv.OpenThemeHistory(AuditTheme.Rendimiento, Samples.T0, "ana");
        inv.ChangeTheme(AuditTheme.Seguridad, Samples.T0.AddHours(3), "maría");

        string json = AtalayaJson.Serialize(inv);
        InventoryCycle back = AtalayaJson.Deserialize<InventoryCycle>(json);

        json.Should().Contain("\"historialTematica\"");
        back.Theme.Should().Be(AuditTheme.Seguridad, "la vigente es la última");
        back.ThemeHistory.Should().HaveCount(2);
        back.ThemeHistory[0].Should().Be(new ThemePeriod(AuditTheme.Rendimiento, Samples.T0, Samples.T0.AddHours(3), "ana"));
        back.ThemeHistory[1].Should().Be(new ThemePeriod(AuditTheme.Seguridad, Samples.T0.AddHours(3), null, "maría"));
    }

    /// <summary>Un ciclo de antes de F17.1 no trae historial: se deriva UNA entrada con la vigente desde la apertura, sin migrar nada.</summary>
    [Fact]
    public void Un_ciclo_sin_historial_deriva_una_entrada_desde_su_apertura()
    {
        InventoryCycle inv = Samples.Inventory(1, ("src/A.cs", UnitState.Pendiente));
        inv.Theme = AuditTheme.Fiabilidad;
        inv.OpenedUtc = Samples.T0;
        string json = AtalayaJson.Serialize(inv).Replace("\"historialTematica\"", "\"x\"");

        InventoryCycle back = AtalayaJson.Deserialize<InventoryCycle>(json);

        back.ThemeHistory.Should().BeEmpty("no se escribe nada que no estuviera");
        back.Periods.Should().ContainSingle().Which.Should().Be(new ThemePeriod(AuditTheme.Fiabilidad, Samples.T0, null, null));
    }

    /// <summary>Un <c>cycle{N}.json</c> anterior a F17: General, sin preferencia y sin fecha — nada se rellena.</summary>
    [Fact]
    public void Un_ciclo_anterior_a_F17_es_General_sin_preferencia_y_sin_fecha()
    {
        string legacy = """
            {
              "schemaVersion": 1,
              "cycleN": 3,
              "units": [ { "path": "src/A.cs", "module": "M", "loc": 10, "state": "auditada" } ]
            }
            """;

        InventoryCycle back = AtalayaJson.Deserialize<InventoryCycle>(legacy);

        back.Theme.Should().Be(AuditTheme.General);
        back.Config.Should().Be(CycleConfig.Default);
        back.OpenedUtc.Should().BeNull("una fecha que no se registró no se inventa");
    }

    [Fact]
    public void La_sesion_registra_la_tematica_del_ciclo_en_que_corrio()
    {
        AuditSession s = Samples.Session();
        s.Theme = AuditTheme.Concurrencia;

        string json = AtalayaJson.Serialize(s);

        json.Should().Contain("\"tematica\": \"concurrencia\"");
        AtalayaJson.Deserialize<AuditSession>(json).Theme.Should().Be(AuditTheme.Concurrencia);
        AtalayaJson.Deserialize<AuditSession>(json.Replace("\"tematica\"", "\"x\"")).Theme.Should().Be(AuditTheme.General);
    }

    /// <summary>Los seis wire-values, fijados: cambiar uno rompería la lectura del hub de todo el equipo.</summary>
    [Theory]
    [InlineData(AuditTheme.General, "general")]
    [InlineData(AuditTheme.Seguridad, "seguridad")]
    [InlineData(AuditTheme.Rendimiento, "rendimiento")]
    [InlineData(AuditTheme.Fiabilidad, "fiabilidad")]
    [InlineData(AuditTheme.Concurrencia, "concurrencia")]
    [InlineData(AuditTheme.Mantenibilidad, "mantenibilidad")]
    public void Cada_tematica_tiene_su_wire_value(AuditTheme theme, string wire)
    {
        Finding f = Samples.Finding();
        f.Theme = theme;

        AtalayaJson.Serialize(f).Should().Contain($"\"tematica\": \"{wire}\"");
    }
}
