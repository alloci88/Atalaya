using Atalaya.App.Services;
using Atalaya.Domain.Ids;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9.1 §1 — verificar CIERRA el ciclo del arreglo.
/// <para>
/// El flujo completo es arreglar → commitear → «arreglada, pendiente de verificar» → verificar. Si
/// tras una verificación en verde la unidad siguiera marcada, la aplicación estaría cobrando dos
/// veces por la misma evidencia: la verificación es el instrumento que valida un arreglo, y pedir
/// además una re-auditoría para limpiar el indicador convierte el guardarraíl en burocracia.
/// </para>
/// </summary>
public sealed class DriftVerificationTests
{
    [Fact]
    public void Un_arreglo_verificado_devuelve_la_unidad_a_sin_cambios_sin_re_auditar()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix: el arreglo del agente");

        // Antes de verificar: pendiente.
        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);

        r.VerifyGreen(finding);

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.SinCambios, "el ciclo del arreglo está cerrado");
        a.OwnFixes.Should().Be(0, "ya no queda ninguno pendiente");
        a.CoveredFixes.Should().Be(1, "pero se sabe que hubo uno, y el detalle lo dice");
        a.Tooltip.Should().Contain("ya verificados");
        a.IsChanged.Should().BeFalse();
    }

    [Fact]
    public void La_unidad_cubierta_deja_de_contar_en_el_panel()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix");

        r.Drift().FixedPendingVerify.Should().Be(1);

        r.VerifyGreen(finding);

        AppDrift after = r.Drift();
        after.FixedPendingVerify.Should().Be(0);
        after.Changed.Should().Be(0, "y no aparece por la otra puerta");
    }

    [Fact]
    public void Una_verificacion_fallida_no_cubre_nada()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "supuestamente arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix");

        r.VerifyRed(finding);

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.ArregladaPendienteDeVerificar,
            "el hallazgo sigue ahí: no hay evidencia que cubra nada");
        a.OwnFixes.Should().Be(1);
        a.CoveredFixes.Should().Be(0);
    }

    [Fact]
    public void Tres_arreglos_verificados_uno_a_uno_no_disparan_el_umbral()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= DriftRules.MaxOwnFixesBeforeReaudit; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            Ulid finding = r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
            r.VerifyGreen(finding);
        }

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.SinCambios,
            "verificar resetea de facto: el umbral cuenta solo los que están sin comprobar");
        a.CoveredFixes.Should().Be(3);
    }

    [Fact]
    public void Tres_arreglos_sin_verificar_si_lo_disparan()
    {
        // La otra mitad de la regla, para que el test anterior no pueda pasar por accidente.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= DriftRules.MaxOwnFixesBeforeReaudit; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
        }

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.Modificada);
    }

    [Fact]
    public void Dos_verificados_y_uno_pendiente_se_quedan_en_pendiente()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= 2; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            Ulid verified = r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
            r.VerifyGreen(verified);
        }

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglo 3");
        r.RecordFix("src/A.cs");
        r.Commit("fix 3");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
        a.OwnFixes.Should().Be(1, "solo cuenta el que falta por comprobar");
        a.CoveredFixes.Should().Be(2);
    }

    [Fact]
    public void Un_commit_ajeno_posterior_manda_sobre_el_arreglo_cubierto()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix");
        r.VerifyGreen(finding);

        r.Commit("alguien más toca A", ("src/A.cs", "y ahora otra cosa"));

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.Modificada,
            "código tocado sin auditoría detrás es candidato, haya lo que haya alrededor");
        a.Commits.Should().Be(1, "el ajeno; el arreglo cubierto ya no cuenta como deriva");
        a.CoveredFixes.Should().Be(1);
    }

    [Fact]
    public void Un_arreglo_resuelto_a_mano_no_cubre_nada()
    {
        // Resolver manualmente es un juicio de una persona sin que nadie haya vuelto a mirar el
        // código. La cobertura exige la evidencia del instrumento que detectó el hallazgo.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix");

        var governance = new GovernanceService(
            r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance));
        governance.ResolveManually(r.Slug, finding, "me fío", r.Head);

        r.Of(r.Drift(), "src/A.cs").State
            .Should().Be(DriftState.ArregladaPendienteDeVerificar);
    }

    [Fact]
    public void Los_arreglos_anteriores_a_F9_no_tienen_huella_y_cuentan_como_pendientes()
    {
        // Migración tolerante: una huella sin hallazgo referenciado no rompe nada y cuenta como no
        // cubierta. Es la dirección segura — pedir una verificación de más, nunca darla por hecha.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordLegacyFix("src/A.cs");
        r.Commit("fix de antes de F9.1");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");
        a.State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
        a.CoveredFixes.Should().Be(0);
    }

    [Fact]
    public void Verificar_cubre_solo_la_unidad_de_su_arreglo()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"), ("src/B.cs", "roto"));
        r.Audit(c1, "src/A.cs", "src/B.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid fa = r.RecordFix("src/A.cs");
        r.Commit("fix de A");

        File.WriteAllText(Path.Combine(r.Clone, "src", "B.cs"), "arreglado");
        r.RecordFix("src/B.cs");
        r.Commit("fix de B");

        r.VerifyGreen(fa);

        AppDrift drift = r.Drift();
        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.SinCambios);
        r.Of(drift, "src/B.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
    }

    [Fact]
    public void Un_arreglo_cubierto_que_rozo_otra_unidad_la_deja_igualmente_como_cambiada()
    {
        // La cobertura no se contagia: para la unidad rozada ese commit sigue siendo ajeno, porque
        // nadie ha auditado ni verificado ESE cambio.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"), ("src/B.cs", "intacto"));
        r.Audit(c1, "src/A.cs", "src/B.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid fa = r.RecordFix("src/A.cs");
        r.Commit("fix de A y de paso B", ("src/B.cs", "rozado"));
        r.VerifyGreen(fa);

        AppDrift drift = r.Drift();
        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.SinCambios);
        r.Of(drift, "src/B.cs").State.Should().Be(DriftState.Modificada);
    }

    [Fact]
    public void Un_hallazgo_medido_por_la_aplicacion_tambien_cierra_el_ciclo()
    {
        // F5.16: los hallazgos que MIDE la aplicación se verifican midiendo, no preguntando. Es el
        // mismo principio —el instrumento que lo detectó dice que ya no está—, así que cubre igual.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "una clase enorme"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "troceada");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix: trocea la clase");

        Domain.Model.Finding f = r.Hub.Store.TryReadFinding(r.Slug, finding.ToString())!;
        f.Resolve(new Domain.Model.ResolutionStamp(
            DateTimeOffset.UtcNow, Domain.Model.ResolutionVia.Medida, Domain.AuditMode.Verify,
            r.Head, "atalaya", "120 LOC < umbral 1500"));
        r.Hub.Store.WriteFinding(r.Slug, f);

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.SinCambios);
    }
}
