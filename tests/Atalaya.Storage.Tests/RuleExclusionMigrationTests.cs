using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// F5.12: lo que quedara escrito como exclusión de regla (F5.10) se convierte en patrón silenciado.
/// La exclusión por regla se retiró entera —convertía el silenciado en mantenimiento de taxonomía—
/// pero nada de lo que alguien decidiera se tira: se traduce, con la descripción de la regla como
/// ejemplar, y el usuario lo afina desde la gestión si no le sirve.
/// </summary>
public sealed class RuleExclusionMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly HubPaths _paths;
    private readonly HubStore _store;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public RuleExclusionMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-excl-mig", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new HubPaths(_root);
        _store = new HubStore(_paths);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Una exclusión legada, escrita a mano tal y como la dejaba F5.10.</summary>
    private void SeedLegacy(string slug, string ruleId, string json)
    {
        string dir = _paths.LegacyRuleExclusionsDir(slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{ruleId}.json"), json);
    }

    private RuleExclusionMigration.Result Migrate(string slug, RuleExclusionMigration.DescribeRule? describe = null)
        => RuleExclusionMigration.MigrateApp(_paths, slug, _ulids, DateTimeOffset.UtcNow, describe);

    [Fact]
    public void Una_exclusion_se_convierte_en_patron_con_la_descripcion_de_la_regla()
    {
        SeedLegacy("app", "mejoras.estilo.nomenclatura", """
            {
              "schemaVersion": 1,
              "ruleId": "mejoras.estilo.nomenclatura",
              "reason": "deuda-aceptada",
              "notes": "no aplica aquí",
              "by": "alvaro",
              "utc": "2026-08-01T10:00:00+00:00"
            }
            """);

        RuleExclusionMigration.Result result = Migrate(
            "app", _ => "Nomenclatura/estilo: nombres poco claros, código muerto");

        result.Migrated.Should().ContainSingle().Which.Should().Contain("P-1");
        result.Skipped.Should().BeEmpty();

        PatternSilence pattern = _store.ListPatternSilences("app").Single();
        pattern.ShortId.Should().Be("P-1");
        pattern.Exemplar.Should().Be("Nomenclatura/estilo: nombres poco claros, código muerto");
        pattern.Reason.Should().Be(SilenceReason.DeudaAceptada);
        pattern.By.Should().Be("alvaro");
        pattern.Utc.Should().Be(DateTimeOffset.Parse("2026-08-01T10:00:00+00:00"));
        pattern.SourceFindingUlid.Should().BeNull("no nació de ningún hallazgo concreto");
        pattern.Notes.Should().Contain("no aplica aquí")
            .And.Contain("mejoras.estilo.nomenclatura", "la procedencia queda escrita");
    }

    /// <summary>La caducidad se conserva: era una decisión con fecha y sigue siéndolo.</summary>
    [Fact]
    public void La_caducidad_sobrevive_a_la_migracion()
    {
        SeedLegacy("app", "optimizacion.alloc.excesiva", """
            {
              "ruleId": "optimizacion.alloc.excesiva",
              "by": "alvaro",
              "utc": "2026-08-01T10:00:00+00:00",
              "expiresUtc": "2026-12-31T00:00:00+00:00"
            }
            """);

        Migrate("app", _ => "Asignación excesiva");

        _store.ListPatternSilences("app").Single().ExpiresUtc
            .Should().Be(DateTimeOffset.Parse("2026-12-31T00:00:00+00:00"));
    }

    /// <summary>Sin descripción disponible el ejemplar es el ruleId: menos legible, nunca vacío.</summary>
    [Fact]
    public void Una_regla_que_ya_no_esta_en_el_catalogo_deja_su_id_como_ejemplar()
    {
        SeedLegacy("app", "criterio.observabilidad", """{"ruleId":"criterio.observabilidad","by":"alvaro"}""");

        Migrate("app", _ => null);

        _store.ListPatternSilences("app").Single().Exemplar.Should().Be("criterio.observabilidad");
    }

    /// <summary>El directorio legado desaparece: la exclusión por regla ya no es una superficie.</summary>
    [Fact]
    public void El_directorio_de_exclusiones_se_retira()
    {
        SeedLegacy("app", "mejoras.estilo.nomenclatura", """{"ruleId":"mejoras.estilo.nomenclatura","by":"a"}""");

        Migrate("app", _ => "Nomenclatura");

        Directory.Exists(_paths.LegacyRuleExclusionsDir("app")).Should().BeFalse();
    }

    /// <summary>Idempotente: correr en cada apertura del hub no puede duplicar nada.</summary>
    [Fact]
    public void Migrar_dos_veces_no_duplica_patrones()
    {
        SeedLegacy("app", "mejoras.estilo.nomenclatura", """{"ruleId":"mejoras.estilo.nomenclatura","by":"a"}""");

        Migrate("app", _ => "Nomenclatura").Migrated.Should().ContainSingle();
        Migrate("app", _ => "Nomenclatura").Migrated.Should().BeEmpty();

        _store.ListPatternSilences("app").Should().ContainSingle();
    }

    /// <summary>Los ids cortos no chocan con los patrones que ya existieran en la app.</summary>
    [Fact]
    public void El_id_corto_respeta_los_patrones_que_ya_hubiera()
    {
        _store.WritePatternSilence("app", new PatternSilence
        {
            Id = _ulids.NewUlid(),
            ShortId = "P-1",
            Exemplar = "algo que ya se callaba",
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow,
        });
        SeedLegacy("app", "mejoras.estilo.nomenclatura", """{"ruleId":"mejoras.estilo.nomenclatura","by":"a"}""");

        Migrate("app", _ => "Nomenclatura");

        _store.ListPatternSilences("app").Select(p => p.ShortId)
            .Should().BeEquivalentTo(new[] { "P-1", "P-2" });
    }

    /// <summary>Un fichero ilegible se deja donde está y se reporta: nunca se borra en silencio.</summary>
    [Fact]
    public void Un_fichero_ilegible_no_se_borra_y_se_reporta()
    {
        SeedLegacy("app", "roto", "{ esto no es json");

        RuleExclusionMigration.Result result = Migrate("app", _ => "x");

        result.Migrated.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Contain("roto");
        File.Exists(Path.Combine(_paths.LegacyRuleExclusionsDir("app"), "roto.json")).Should().BeTrue();
    }

    [Fact]
    public void Una_app_sin_exclusiones_no_produce_nada()
    {
        Migrate("sin-exclusiones").Migrated.Should().BeEmpty();
        _store.ListPatternSilences("sin-exclusiones").Should().BeEmpty();
    }

    /// <summary>Migrar una app no toca a la de al lado: la exclusión nunca fue global, y el patrón tampoco.</summary>
    [Fact]
    public void Migrar_una_app_no_toca_a_otra()
    {
        SeedLegacy("alpha", "mejoras.estilo.nomenclatura", """{"ruleId":"mejoras.estilo.nomenclatura","by":"a"}""");
        SeedLegacy("beta", "errores.null.desreferencia", """{"ruleId":"errores.null.desreferencia","by":"b"}""");

        Migrate("alpha", _ => "Nomenclatura");

        _store.ListPatternSilences("alpha").Should().ContainSingle();
        _store.ListPatternSilences("beta").Should().BeEmpty();
        Directory.Exists(_paths.LegacyRuleExclusionsDir("beta")).Should().BeTrue();
    }
}
