using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9 §1 — la detección de deriva, contra repositorios git de verdad.
/// </summary>
public sealed class DriftDetectionTests
{
    [Fact]
    public void Sin_cambios_en_el_codigo_la_unidad_no_ha_derivado()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "class A {}"), ("src/B.cs", "class B {}"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        r.Commit("toca solo B", ("src/B.cs", "class B { int x; }"));

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.SinCambios);
        r.Of(drift, "src/B.cs").State.Should().Be(DriftState.Modificada);
        drift.Changed.Should().Be(1);
    }

    [Fact]
    public void Una_unidad_modificada_dice_cuantos_commits_y_cuando()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        r.Commit("uno", ("src/A.cs", "2"));
        r.Commit("dos", ("src/A.cs", "3"));
        r.Commit("tres", ("src/A.cs", "4"));

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.Modificada);
        a.Commits.Should().Be(3, "son los tres commits que la tocaron, y el de la auditoría no cuenta");
        a.LastChangeUtc.Should().NotBeNull();
        a.Label.Should().Be("Cambiada desde la auditoría (3 commits)");
    }

    [Fact]
    public void Cualquier_cambio_de_contenido_cuenta_aunque_sea_un_comentario()
    {
        // Anti-objetivo declarado: nada de juzgar si el diff «es relevante». El coste de re-auditar
        // una unidad con un cambio trivial es pequeño; el de ignorar un cambio real, no.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "class A {}"));
        r.Audit(c1, "src/A.cs");
        r.Commit("solo un comentario", ("src/A.cs", "// nota\nclass A {}"));

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.Modificada);
    }

    [Fact]
    public void Una_unidad_movida_cuenta_como_modificada_y_dice_de_donde_viene()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "class A { int uno; int dos; int tres; }"));
        r.Audit(c1, "src/A.cs");
        r.Move("src/A.cs", "src/nuevo/A.cs");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.Modificada, "movida es modificada a efectos de deriva");
        a.RenamedFrom.Should().Be("src/nuevo/A.cs", "la unidad ha ido a parar ahí");
    }

    [Fact]
    public void Una_unidad_borrada_se_informa_aparte_y_no_como_cambiada()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "class A {}"), ("src/B.cs", "class B {}"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        r.Commit("fuera A", ("src/A.cs", null));

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.Borrada);
        drift.Changed.Should().Be(0, "una unidad borrada no es candidata a re-auditar");
        drift.Deleted.Should().Be(1);
    }

    [Fact]
    public void Lo_nunca_auditado_no_se_mezcla_con_la_deriva()
    {
        // Es cobertura inicial, no deriva: son dos preguntas distintas y juntarlas haría que
        // «cambiadas» significara «todo lo que queda», que es exactamente lo que no ayuda.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/B.cs", "1"));
        r.Audit(c1, "src/A.cs");
        r.Pending("src/B.cs");
        r.Commit("toca las dos", ("src/A.cs", "2"), ("src/B.cs", "2"));

        AppDrift drift = r.Drift();

        drift.Units.Should().ContainSingle().Which.Path.Should().Be("src/A.cs");
        drift.Changed.Should().Be(1);
    }

    [Fact]
    public void Las_rutas_que_no_son_unidades_se_ignoran()
    {
        // El diff da rutas; el INVENTARIO dice cuál es unidad. Un csproj o un README que cambian
        // no derivan nada, porque no se auditan.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/App.csproj", "<Project/>"));
        r.Audit(c1, "src/A.cs");
        r.Commit("solo el csproj y un doc",
            ("src/App.csproj", "<Project><X/></Project>"), ("README.md", "hola"));

        r.Drift().Changed.Should().Be(0);
    }

    [Fact]
    public void Una_clase_parcial_deriva_si_cambia_cualquiera_de_sus_ficheros()
    {
        // Dos ficheros del inventario que son la misma clase: tocar uno marca ese, y el otro se
        // queda como estaba. Cada fichero del inventario es una unidad, y se cuentan por separado.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial",
            ("src/A.cs", "partial class A {}"), ("src/A.Extra.cs", "partial class A {}"));
        r.Audit(c1, "src/A.cs", "src/A.Extra.cs");
        r.Commit("toca la mitad de la parcial", ("src/A.Extra.cs", "partial class A { int x; }"));

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.Extra.cs").State.Should().Be(DriftState.Modificada);
        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.SinCambios);
    }

    [Fact]
    public void Las_unidades_se_agrupan_por_commit_de_auditoria()
    {
        // F9 §1.2: una pasada por commit de auditoría DISTINTO, no una por unidad. Se comprueba por
        // el resultado — dos grupos con anclas distintas dan respuestas distintas sobre el mismo
        // historial — que es lo que la agrupación tiene que preservar.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"), ("src/B.cs", "1"));
        r.Audit(c1, "src/A.cs");
        string c2 = r.Commit("toca A", ("src/A.cs", "2"));
        r.Audit(c2, "src/B.cs");
        r.Commit("toca B", ("src/B.cs", "2"));

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.Modificada,
            "A se auditó en c1 y cambió en c2");
        r.Of(drift, "src/B.cs").State.Should().Be(DriftState.Modificada,
            "B se auditó en c2 y cambió después");
        r.Of(drift, "src/B.cs").Commits.Should().Be(1, "solo el commit posterior a SU auditoría");
    }

    [Fact]
    public void La_deriva_se_mide_entre_commits_y_no_contra_el_arbol_de_trabajo()
    {
        // Un fichero sucio no es deriva: no hay commit que difear. Se avisa aparte.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");
        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "sucio sin commitear");

        AppDrift drift = r.Drift();

        r.Of(drift, "src/A.cs").State.Should().Be(DriftState.SinCambios);
        drift.UncommittedFiles.Should().Be(1);
        drift.Warnings.Should().Contain(w => w.Contains("sin commitear"));
    }

    [Fact]
    public void La_cache_se_reusa_y_se_invalida_sola_cuando_HEAD_avanza()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");

        var query = new DriftQuery(r.Hub);
        AppDrift first = query.For(r.Slug, r.Clone);
        query.For(r.Slug, r.Clone).Should().BeSameAs(first, "misma clave, mismo resultado");

        r.Commit("toca A", ("src/A.cs", "2"));

        AppDrift after = query.For(r.Slug, r.Clone);
        after.Should().NotBeSameAs(first, "cambió HEAD, cambió la clave");
        after.Changed.Should().Be(1);
    }

    [Fact]
    public void Invalidar_tira_la_cache_para_el_caso_del_pull()
    {
        // Un pull puede traer la sesión de OTRA máquina que re-auditó una unidad: cambia la deriva
        // sin que HEAD del clon se haya movido, y por eso la clave no se entera sola.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        string c2 = r.Commit("toca A", ("src/A.cs", "2"));
        r.Audit(c1, "src/A.cs");

        var query = new DriftQuery(r.Hub);
        query.For(r.Slug, r.Clone).Changed.Should().Be(1);

        r.Audit(c2, "src/A.cs");
        query.Invalidate();

        query.For(r.Slug, r.Clone).Changed.Should().Be(0, "ya se auditó en el commit de cabeza");
    }

    [Fact]
    public void Renombrar_no_pierde_los_commits_anteriores_al_movimiento()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "class A { int uno; int dos; int tres; }"));
        r.Audit(c1, "src/A.cs");
        r.Commit("cambio antes de mover", ("src/A.cs", "class A { int uno; int dos; int cuatro; }"));
        r.Move("src/A.cs", "src/otro/A.cs");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.Commits.Should().Be(2, "el cambio previo y el movimiento; sin seguir el rename saldría 1");
    }
}

/// <summary>
/// F9 §1.1 — cuando el historial no coopera. Cada caso con su estado honesto: ni un cero falso ni
/// una excepción sin capturar (N-2).
/// </summary>
public sealed class DriftHistoryTests
{
    [Fact]
    public void Commit_de_auditoria_ausente_no_es_sin_cambios_sino_historial_no_disponible()
    {
        using var r = new DriftRepo();
        r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit("deadbee", "src/A.cs");

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.HistorialNoDisponible);
        a.Note.Should().Contain("no está en este clon");
        a.Label.Should().Be("Historial no disponible");
    }

    [Fact]
    public void Auditoria_sin_commit_registrado_tambien_es_historial_no_disponible()
    {
        using var r = new DriftRepo();
        r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit("unknown", "src/A.cs");

        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.HistorialNoDisponible);
    }

    [Fact]
    public void Historial_reescrito_se_detecta_con_merge_base_antes_de_difear()
    {
        // El commit EXISTE pero no es antecesor de HEAD: un diff entre ramas divergentes daría
        // cambios fantasma, así que no se hace.
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));

        string huerfano;
        using (var repo = new Repository(r.Clone))
        {
            // Una rama sin relación con main: es lo que deja un rebase o un force-push agresivo.
            repo.Refs.UpdateTarget("HEAD", "refs/heads/otra");
            repo.Index.Clear();
            repo.Index.Write();
            File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "otra historia");
            Commands.Stage(repo, "*");
            var who = new Signature("T", "t@e.com", DateTimeOffset.UtcNow.AddMinutes(5));
            huerfano = repo.Commit("raíz paralela", who, who, new CommitOptions { AllowEmptyCommit = true })
                .Sha[..7];
            repo.Refs.UpdateTarget("HEAD", "refs/heads/master");
        }

        r.Audit(huerfano, "src/A.cs");
        _ = c1;

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.HistorialNoDisponible);
        a.Note.Should().Match(n => n!.Contains("reescrito") || n.Contains("no comparten historia"));
    }

    [Fact]
    public void Clon_por_detras_de_la_auditoria_pide_pull_y_no_inventa_deriva()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        string c2 = r.Commit("avance", ("src/A.cs", "2"));

        // La otra máquina auditó en c2; este clon vuelve atrás, a c1.
        r.Audit(c2, "src/A.cs");
        using (var repo = new Repository(r.Clone))
        {
            repo.Reset(ResetMode.Hard, repo.Lookup<Commit>(c1));
        }

        UnitDrift a = r.Of(r.Drift(), "src/A.cs");

        a.State.Should().Be(DriftState.HistorialNoDisponible);
        a.Note.Should().Contain("haz pull");
    }

    [Fact]
    public void El_panel_dice_contra_que_rama_se_ha_medido()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");

        AppDrift drift = r.Drift();

        drift.Branch.Should().Be("master");
        drift.Head.Should().NotBeEmpty();
    }

    [Fact]
    public void Una_rama_que_no_es_la_por_defecto_lleva_su_matiz()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "1"));
        r.Audit(c1, "src/A.cs");

        using (var repo = new Repository(r.Clone))
        {
            Branch feature = repo.CreateBranch("feature/x");
            Commands.Checkout(repo, feature);
        }

        AppDrift drift = r.Drift();

        drift.Branch.Should().Be("feature/x");
        drift.BranchIsDefault.Should().BeFalse();
        drift.Warnings.Should().Contain(w => w.Contains("feature/x") && w.Contains("no es la rama por defecto"));
    }

    [Fact]
    public void Sin_clon_no_hay_deriva_y_se_dice_por_que()
    {
        using var r = new DriftRepo();
        AppDrift drift = new DriftQuery(r.Hub).Compute(r.Slug, clonePath: null);

        drift.Problem.Should().Contain("Vincula tu clon");
        drift.Units.Should().BeEmpty();
        drift.Changed.Should().Be(0);
    }

    [Fact]
    public void Una_carpeta_que_no_es_repo_se_declara_en_vez_de_reventar()
    {
        using var r = new DriftRepo();
        string folder = Path.Combine(Path.GetTempPath(), "atalaya-norepo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            AppDrift drift = new DriftQuery(r.Hub).Compute(r.Slug, folder);
            drift.Problem.Should().Contain("no es un repositorio git");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
