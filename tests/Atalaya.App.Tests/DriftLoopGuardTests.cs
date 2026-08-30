using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9 §2 — el guardarraíl anti-bucle: los cambios que hizo la propia auditoría no pueden
/// realimentar la lista de unidades cambiadas, o el ciclo no converge nunca.
/// </summary>
public sealed class DriftLoopGuardTests
{
    [Fact]
    public void Solo_arreglos_propios_no_es_cambiada_sino_pendiente_de_verificar()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        // El agente arregla y deja el fichero escrito; la aplicación registra la huella. Luego el
        // usuario commitea, que es como funciona de verdad (D-556: Atalaya no commitea).
        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix: OPT-0001");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
        a.OwnFixes.Should().Be(1);
        a.Label.Should().Be("Arreglada — pendiente de verificar");
        a.IsChanged.Should().BeFalse("no entra en «Seleccionar cambiadas»: se verifica, no se re-audita");
    }

    [Fact]
    public void Un_solo_commit_ajeno_la_devuelve_a_cambiada()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix: OPT-0001");
        r.Commit("alguien más toca A", ("src/A.cs", "y ahora otra cosa"));

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.Modificada);
        a.Commits.Should().Be(1, "solo se cuenta el ajeno");
        a.OwnFixes.Should().Be(1, "el propio sigue contándose aparte, y el tooltip lo dice");
    }

    [Fact]
    public void Un_commit_de_arreglo_que_toca_varios_ficheros_es_propio_solo_para_el_suyo()
    {
        // El caso que el prompt nombra: el commit del arreglo roza otras unidades. Para la unidad
        // cuyo hallazgo se arreglaba es propio; para las demás es ajeno — y eso es lo correcto:
        // código tocado sin auditoría detrás es candidato.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"), ("src/B.cs", "intacto"));
        r.Audit(c1, "src/A.cs", "src/B.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix: OPT-0001 y de paso B", ("src/B.cs", "rozado por el arreglo"));

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
        r.Of(drift, "src/B.cs").State.Should().Be(DriftState.Modificada,
            "para B ese commit es ajeno: nadie ha auditado ese cambio");
    }

    [Fact]
    public void Tres_arreglos_acumulados_la_devuelven_a_cambiada_aunque_todos_sean_propios()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= DriftRules.MaxOwnFixesBeforeReaudit; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
        }

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.OwnFixes.Should().Be(3);
        a.State.Should().Be(DriftState.Modificada,
            "tanto retoque junto merece una mirada fresca, aunque cada uno viniera de aquí");
        a.Commits.Should().Be(3, "la etiqueta no puede decir «0 commits»");
    }

    [Fact]
    public void Dos_arreglos_todavia_no_llegan_al_umbral()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= 2; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
        }

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
    }

    [Fact]
    public void Re_auditar_resetea_el_contador_de_arreglos()
    {
        // El contador se mide SIEMPRE desde el commit de la última auditoría, así que re-auditar lo
        // pone a cero sin que haya ningún contador que mantener — que es lo que evita que se
        // desincronice.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "v0"));
        r.Audit(c1, "src/A.cs");

        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), $"arreglo {i}");
            r.RecordFix("src/A.cs");
            r.Commit($"fix {i}");
        }

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.Modificada);

        r.Audit(r.Head, "src/A.cs");

        UnitDrift after = r.Of(r.Drift(), "src/A.cs");
        after.State.Should().Be(DriftState.SinCambios);
        after.OwnFixes.Should().Be(0);
    }

    [Fact]
    public void Verificar_no_cuenta_como_cambio_porque_no_toca_el_codigo()
    {
        // Verificar no commitea nada, así que la unidad se queda como estaba: es el gesto que cierra
        // el bucle sin abrir otro.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix");

        AppDrift before = r.Drift();
        AppDrift after = r.Drift();

        after.FixedPendingVerify.Should().Be(before.FixedPendingVerify).And.Be(1);
    }

    [Fact]
    public void Un_arreglo_enmendado_deja_de_reconocerse_y_sale_como_cambiada()
    {
        // Degradación asumida y documentada: si el usuario enmienda o aplasta antes de publicar, el
        // contenido ya no casa con la huella. Es la dirección segura — re-auditar de más.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");

        // El usuario retoca a mano antes de commitear: ya no es lo que escribió el agente.
        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado, y además otra cosa mía");
        r.Commit("fix, con mi retoque");

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.Modificada);
    }

    [Fact]
    public void Los_finales_de_linea_no_rompen_el_reconocimiento_del_arreglo()
    {
        // La huella normaliza CRLF a LF: el fichero del árbol puede tener CRLF por autocrlf y el
        // blob guardado LF, y son el mismo contenido. Sin normalizar, cada máquina con una
        // configuración distinta dejaría de reconocer sus propios arreglos.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");

        string abs = Path.Combine(r.Clone, "src", "A.cs");
        File.WriteAllText(abs, "linea uno\r\nlinea dos\r\n");
        r.RecordFix("src/A.cs");
        File.WriteAllText(abs, "linea uno\nlinea dos\n");
        r.Commit("fix con LF");

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
    }

    [Fact]
    public void Los_conteos_van_separados_y_no_se_suman()
    {
        // «N cambiadas · M arregladas pendientes de verificar»: son acciones distintas —auditar y
        // verificar—, así que sumarlas propondría gastar una auditoría en algo que se verifica.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/B.cs", "1"));
        r.Audit(c1, "src/A.cs", "src/B.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix de A");
        r.Commit("alguien toca B", ("src/B.cs", "2"));

        AppDrift drift = r.Drift();

        drift.Changed.Should().Be(1);
        drift.FixedPendingVerify.Should().Be(1);
        drift.ChangedUnits.Should().ContainSingle().Which.Path.Should().Be("src/B.cs");
    }
}

/// <summary>
/// F9 §4 — los hallazgos que se quedaron sin código. La aplicación los encuentra y aporta la
/// evidencia; resolverlos es siempre un acto humano.
/// </summary>
public sealed class DeletedUnitFindingsTests
{
    [Fact]
    public void Un_hallazgo_activo_cuya_unidad_ya_no_existe_sale_aparte()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/B.cs", "1"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        Ulid fA = Seed(r, "src/A.cs", "hallazgo de A");
        Seed(r, "src/B.cs", "hallazgo de B");
        r.Commit("fuera A", ("src/A.cs", null));

        AppDrift drift = r.Drift();

        drift.Orphans.Should().ContainSingle();
        drift.Orphans[0].FindingId.Should().Be(fA.ToString());
        drift.Orphans[0].UnitPath.Should().Be("src/A.cs");
    }

    [Fact]
    public void Un_hallazgo_ya_resuelto_no_vuelve_como_huerfano()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        Ulid id = Seed(r, "src/A.cs", "hallazgo");
        Finding f = r.Hub.Store.TryReadFinding(r.Slug, id.ToString())!;
        f.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow, ResolutionVia.Manual, AuditMode.Verify, c1, "yo", "ya"));
        r.Hub.Store.WriteFinding(r.Slug, f);
        r.Commit("fuera A", ("src/A.cs", null));

        r.Drift().Orphans.Should().BeEmpty();
    }

    [Fact]
    public void Se_localiza_el_commit_que_borro_el_fichero()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        r.Commit("ruido", ("src/otro.cs", "x"));
        string borrado = r.Commit("fuera A", ("src/A.cs", null));
        r.Commit("más ruido", ("src/otro.cs", "y"));

        (string? sha, DateTimeOffset? utc) = new DriftQuery(r.Hub).FindDeletion(r.Clone, "src/A.cs");

        sha.Should().Be(borrado);
        utc.Should().NotBeNull();
    }

    [Fact]
    public void Resolver_por_codigo_eliminado_deja_atribucion_y_evidencia()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        Ulid id = Seed(r, "src/A.cs", "hallazgo");
        string borrado = r.Commit("fuera A", ("src/A.cs", null));

        var governance = new GovernanceService(r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance));
        governance.ResolveAsDeletedCode(r.Slug, id, "src/A.cs", borrado);

        Finding f = r.Hub.Store.TryReadFinding(r.Slug, id.ToString())!;
        f.Status.Should().Be(FindingStatus.Resuelto);
        f.Resolved!.Via.Should().Be(ResolutionVia.CodigoEliminado);
        f.Resolved.Commit.Should().Be(borrado, "la evidencia es el commit del borrado");
        f.Resolved.By.Should().NotBeNullOrWhiteSpace("la resolución lleva el nombre de quien decidió");
        f.Resolved.Justification.Should().Contain("src/A.cs").And.Contain(borrado);
    }

    [Fact]
    public void Sin_commit_localizado_la_resolucion_lo_dice_en_vez_de_inventarlo()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        Ulid id = Seed(r, "src/A.cs", "hallazgo");

        var governance = new GovernanceService(r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance));
        governance.ResolveAsDeletedCode(r.Slug, id, "src/A.cs", deletedCommit: null);

        Finding f = r.Hub.Store.TryReadFinding(r.Slug, id.ToString())!;
        f.Resolved!.Justification.Should().Contain("no se ha podido localizar el commit");
    }

    [Fact]
    public void La_lista_no_resuelve_nada_por_su_cuenta_al_abrirse()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        Ulid id = Seed(r, "src/A.cs", "hallazgo");
        r.Commit("fuera A", ("src/A.cs", null));

        var vm = new DeletedUnitsViewModel(
            new GovernanceService(r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance)),
            new DriftQuery(r.Hub),
            new ToastCenter());
        vm.Load(r.Slug, "APP", r.Clone, r.Drift().Orphans);

        vm.Rows.Should().ContainSingle();
        vm.Rows[0].DeletedCommit.Should().NotBeNullOrEmpty("la evidencia se busca al abrir");
        r.Hub.Store.TryReadFinding(r.Slug, id.ToString())!.Status
            .Should().Be(FindingStatus.Activo, "abrir la lista no resuelve nada");
    }

    [Fact]
    public void Solo_se_resuelve_lo_marcado()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/B.cs", "1"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        Ulid a = Seed(r, "src/A.cs", "de A");
        Ulid b = Seed(r, "src/B.cs", "de B");
        r.Commit("fuera las dos", ("src/A.cs", null), ("src/B.cs", null));

        var vm = new DeletedUnitsViewModel(
            new GovernanceService(r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance)),
            new DriftQuery(r.Hub),
            new ToastCenter());
        vm.Load(r.Slug, "APP", r.Clone, r.Drift().Orphans);

        vm.Rows.Single(x => x.UnitPath == "src/A.cs").IsSelected = true;
        vm.ResolveSelectedCommand.Execute(null);

        r.Hub.Store.TryReadFinding(r.Slug, a.ToString())!.Status.Should().Be(FindingStatus.Resuelto);
        r.Hub.Store.TryReadFinding(r.Slug, b.ToString())!.Status.Should().Be(FindingStatus.Activo);
    }

    private static Ulid Seed(DriftRepo r, string path, string title)
    {
        var ulids = new UlidFactory(Domain.Abstractions.SystemClock.Instance);
        Ulid id = ulids.NewUlid();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "tester");
        r.Hub.Store.WriteFinding(r.Slug, new Finding
        {
            Id = id,
            RuleId = "R-1",
            Title = title,
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Locations = { new Location(path, 1) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });

        return id;
    }
}
