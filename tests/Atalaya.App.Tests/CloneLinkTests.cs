using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.8 — Estado de vinculación local y flujo «Vincular clon».
/// <para>
/// El hueco que cierra: las apps del hub salen en el portafolio de TODO el mundo, pero auditarlas
/// necesita el clon local en ESTA máquina, y eso no lo decía nada. El usuario lo descubría al
/// intentarlo. Lo que se prueba aquí es lo que hace que no vuelva a pasar: que los tres estados se
/// detecten (no dos — la carpeta movida es un caso real y distinto), que vincular una carpeta
/// equivocada se rechace con las dos URLs delante, y que «Nueva aplicación» no pueda crear un
/// duplicado de una app que ya existe.
/// </para>
/// </summary>
public sealed class CloneLinkTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/xblast.git";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly CloneLinkService _links;

    public CloneLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f58", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast", Name = "XBlast", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _machines = new MachineConfigStore(_paths.MachinesJson);
        _links = new CloneLinkService(_hub, _machines);
    }

    /// <summary>Una carpeta que es un clon creíble del repo indicado.</summary>
    private string Clone(string name, string origin = RepoUrl)
    {
        string path = Path.Combine(_root, name);
        TestFactory.MakeClone(path, origin);
        return path;
    }

    // ================================================================= §1 · los tres estados

    [Fact]
    public void Sin_ruta_registrada_la_app_esta_en_rojo()
    {
        CloneLink link = _links.For("xblast");

        link.State.Should().Be(CloneLinkState.SinVincular);
        link.CanAudit.Should().BeFalse();
        link.NeedsAction.Should().BeTrue();
        link.ActionLabel.Should().Be("Vincular clon local…");
        link.Tooltip.Should().Contain("hallazgos, métricas e informes",
            "el tooltip tiene que decir qué SÍ se puede hacer, no solo qué no");
    }

    [Fact]
    public void Con_clon_valido_la_app_esta_en_verde()
    {
        string clone = Clone("xblast");
        _machines.SetClonePath("xblast", clone);

        CloneLink link = _links.For("xblast");

        link.State.Should().Be(CloneLinkState.Vinculada);
        link.CanAudit.Should().BeTrue();
        link.NeedsAction.Should().BeFalse();
        link.Tooltip.Should().Contain(clone).And.Contain("Puedes auditar");
    }

    /// <summary>El caso del prompt: renombrar la carpeta del clon. Ámbar, no rojo.</summary>
    [Fact]
    public void Si_la_carpeta_registrada_ya_no_esta_el_piloto_es_ambar()
    {
        string clone = Clone("xblast");
        _machines.SetClonePath("xblast", clone);
        Directory.Move(clone, Path.Combine(_root, "xblast-renombrada"));

        CloneLink link = _links.For("xblast");

        link.State.Should().Be(CloneLinkState.Problema);
        link.CanAudit.Should().BeFalse();
        link.ActionLabel.Should().Be("Reparar vínculo…");
        link.Problem.Should().Contain("ya no existe").And.Contain(clone);
    }

    [Fact]
    public void Una_carpeta_que_ya_no_es_un_repo_git_es_ambar()
    {
        string plain = Path.Combine(_root, "solo-una-carpeta");
        Directory.CreateDirectory(plain);
        _machines.SetClonePath("xblast", plain);

        CloneLink link = _links.For("xblast");

        link.State.Should().Be(CloneLinkState.Problema);
        link.Problem.Should().Contain("no es un repositorio git");
    }

    /// <summary>
    /// El caso que justifica que la comprobación llegue hasta el remoto: la carpeta existe, es un
    /// repo, y es OTRO proyecto. Sin esto se auditaría el de al lado publicando los hallazgos bajo
    /// el nombre de éste.
    /// </summary>
    [Fact]
    public void Un_repo_con_otro_remoto_es_ambar_y_dice_las_dos_URLs()
    {
        string otro = Clone("otro-proyecto", "https://example.invalid/org/otro.git");
        _machines.SetClonePath("xblast", otro);

        CloneLink link = _links.For("xblast");

        link.State.Should().Be(CloneLinkState.Problema);
        link.Problem.Should()
            .Contain("https://example.invalid/org/otro.git", "la del clon")
            .And.Contain(RepoUrl, "la de la app");
    }

    [Fact]
    public void Un_repo_sin_remoto_origin_es_ambar()
    {
        string huerfano = Path.Combine(_root, "sin-origin");
        Directory.CreateDirectory(huerfano);
        Repository.Init(huerfano);
        _machines.SetClonePath("xblast", huerfano);

        _links.For("xblast").State.Should().Be(CloneLinkState.Problema);
    }

    /// <summary>
    /// El clon clonado por SSH del MISMO repo vale. Rechazarlo dejaría sin auditar a quien clona
    /// así, que es la mitad del equipo.
    /// </summary>
    [Fact]
    public void Un_clon_por_ssh_del_mismo_repo_es_verde()
    {
        _machines.SetClonePath("xblast", Clone("xblast-ssh", "git@example.invalid:org/xblast.git"));

        _links.For("xblast").State.Should().Be(CloneLinkState.Vinculada);
    }

    // ================================================================= §2 · validación

    [Fact]
    public void Validar_una_carpeta_que_no_es_repo_falla_y_no_escribe_nada()
    {
        string plain = Path.Combine(_root, "no-repo");
        Directory.CreateDirectory(plain);

        CloneValidation result = _links.Link("xblast", plain);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain(".git");
        _machines.Load().ClonePathFor("xblast").Should().BeNull(
            "una validación fallida NO puede dejar rastro en machines.json");
    }

    [Fact]
    public void Vincular_una_carpeta_con_el_remoto_equivocado_se_rechaza_con_las_dos_URLs()
    {
        string otro = Clone("vecino", "https://example.invalid/org/vecino.git");

        CloneValidation result = _links.Link("xblast", otro);

        result.Ok.Should().BeFalse();
        result.Error.Should()
            .Contain("https://example.invalid/org/vecino.git")
            .And.Contain(RepoUrl);
        _machines.Load().ClonePathFor("xblast").Should().BeNull();
    }

    [Fact]
    public void Vincular_la_carpeta_correcta_la_registra_y_pone_el_piloto_en_verde()
    {
        string clone = Clone("xblast");

        _links.Link("xblast", clone).Ok.Should().BeTrue();

        _machines.Load().ClonePathFor("xblast").Should().Be(Path.GetFullPath(clone));
        _links.For("xblast").CanAudit.Should().BeTrue();
    }

    // ================================================================= §2 · el diálogo

    private LinkCloneViewModel Dialog(IFolderPicker? picker = null)
        => new(
            _hub.Store.TryReadApp("xblast")!,
            _links.For("xblast"),
            _links,
            new InventoryRescanService(_hub, new InventoryScanner(), _settings),
            picker ?? new TestFactory.NoFolderPicker());

    /// <summary>Elige lo que le digan, en el orden en que se lo digan.</summary>
    private sealed class ScriptedPicker : IFolderPicker
    {
        private readonly Queue<string?> _answers;

        public ScriptedPicker(params string?[] answers) => _answers = new Queue<string?>(answers);

        public string? Pick(string title, string? initialDirectory = null)
            => _answers.Count > 0 ? _answers.Dequeue() : null;
    }

    [Fact]
    public void El_dialogo_rechaza_la_carpeta_equivocada_y_deja_seguir_con_la_buena()
    {
        string mala = Clone("vecino", "https://example.invalid/org/vecino.git");
        string buena = Clone("xblast");

        LinkCloneViewModel vm = Dialog(new ScriptedPicker(mala, buena));

        vm.ChooseExistingCommand.Execute(null);
        vm.LinkExistingCommand.Execute(null);

        vm.Linked.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.Error.Should().Contain("vecino").And.Contain("xblast");

        // Y con la buena, a la primera: el error de antes no deja estado pegado.
        vm.ChooseExistingCommand.Execute(null);
        vm.LinkExistingCommand.Execute(null);

        vm.Linked.Should().BeTrue();
        vm.HasError.Should().BeFalse();
        vm.LinkedPath.Should().Be(Path.GetFullPath(buena));
        _links.For("xblast").State.Should().Be(CloneLinkState.Vinculada);
    }

    /// <summary>Reparar y vincular no se dicen igual, ni parten del mismo sitio.</summary>
    [Fact]
    public void Reparar_arranca_en_la_ruta_rota_y_lo_dice()
    {
        string clone = Clone("xblast");
        _machines.SetClonePath("xblast", clone);
        Directory.Move(clone, Path.Combine(_root, "movida"));

        LinkCloneViewModel vm = Dialog();

        vm.IsRepair.Should().BeTrue();
        vm.Title.Should().Be("Reparar vínculo");
        vm.ExistingPath.Should().Be(clone, "se parte de donde estaba, no de una caja vacía");
        vm.Intro.Should().Contain("ya no existe");
    }

    /// <summary>
    /// «Clonarlo ahora», contra un remoto local (N-1: sin red). Termina en 🟢 y con la ruta
    /// registrada, que es lo único que distingue «se ha clonado» de «se puede auditar».
    /// </summary>
    [Fact]
    public async Task Clonar_ahora_termina_vinculado_y_en_verde()
    {
        string origin = SourceRepo();
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast", Name = "XBlast", RepoUrl = origin, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string destino = Path.Combine(_root, "destino");
        Directory.CreateDirectory(destino);

        LinkCloneViewModel vm = Dialog();
        vm.IsCloneMode = true;
        vm.TargetParent = destino;
        vm.TargetPreview.Should().EndWith("origen", "se dice dónde va a quedar ANTES de crearlo");

        await vm.CloneNowCommand.ExecuteAsync(null);

        vm.Error.Should().BeEmpty();
        vm.Linked.Should().BeTrue();
        File.Exists(Path.Combine(vm.LinkedPath, "A.cs")).Should().BeTrue("el código está de verdad");
        _links.For("xblast").State.Should().Be(CloneLinkState.Vinculada);
    }

    [Fact]
    public async Task Clonar_sobre_una_carpeta_ocupada_avisa_en_vez_de_mezclar()
    {
        string origin = SourceRepo();
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast", Name = "XBlast", RepoUrl = origin, CurrentCycle = 1,
        });

        string destino = Path.Combine(_root, "ocupado");
        Directory.CreateDirectory(Path.Combine(destino, "origen"));
        File.WriteAllText(Path.Combine(destino, "origen", "algo.txt"), "no me pises");

        LinkCloneViewModel vm = Dialog();
        vm.IsCloneMode = true;
        vm.TargetParent = destino;

        await vm.CloneNowCommand.ExecuteAsync(null);

        vm.Linked.Should().BeFalse();
        vm.Error.Should().Contain("ya existe y no está vacía");
    }

    // ================================================================= §2 · deriva del inventario

    [Fact]
    public void Al_vincular_se_avisa_si_el_clon_esta_en_otro_commit_y_se_puede_re_escanear()
    {
        string clone = Clone("xblast");
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { }");
        SeedInventory(("A.cs", HashUtil.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("otra cosa"))));

        LinkCloneViewModel vm = Dialog(new ScriptedPicker(clone));
        vm.ChooseExistingCommand.Execute(null);
        vm.LinkExistingCommand.Execute(null);

        vm.Linked.Should().BeTrue();
        vm.HasDrift.Should().BeTrue();
        vm.DriftWarning.Should().Contain("commit distinto");
    }

    [Fact]
    public void Un_clon_que_coincide_con_el_inventario_no_avisa_de_nada()
    {
        string clone = Clone("xblast");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("class A { }");
        File.WriteAllBytes(Path.Combine(clone, "A.cs"), bytes);
        SeedInventory(("A.cs", HashUtil.Sha256Hex(bytes)));

        LinkCloneViewModel vm = Dialog(new ScriptedPicker(clone));
        vm.ChooseExistingCommand.Execute(null);
        vm.LinkExistingCommand.Execute(null);

        vm.HasDrift.Should().BeFalse();
    }

    /// <summary>Re-escanear es una OFERTA: solo corre si se pulsa, y entonces el aviso se va.</summary>
    [Fact]
    public async Task Re_escanear_desde_el_dialogo_pone_el_inventario_al_dia()
    {
        string clone = Clone("xblast");
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { }");
        SeedInventory(("A.cs", "sha-de-otro-commit"));

        LinkCloneViewModel vm = Dialog(new ScriptedPicker(clone));
        vm.ChooseExistingCommand.Execute(null);
        vm.LinkExistingCommand.Execute(null);
        vm.HasDrift.Should().BeTrue();

        await vm.RescanNowCommand.ExecuteAsync(null);

        vm.HasDrift.Should().BeFalse();
        InventoryCycle after = _hub.Store.TryReadInventory("xblast", 1)!;
        after.Units.Single(u => u.Path == "A.cs").ContentHash
            .Should().Be(HashUtil.Sha256Hex(File.ReadAllBytes(Path.Combine(clone, "A.cs"))));
    }

    // ================================================================= helpers

    private void SeedInventory(params (string Path, string Hash)[] units)
    {
        var cycle = new InventoryCycle { CycleN = 1 };
        foreach ((string path, string hash) in units)
        {
            cycle.Units.Add(new InventoryUnit
            {
                Path = path, Module = "raiz", Loc = 1, ContentHash = hash, State = UnitState.Pendiente,
            });
        }

        _hub.Store.WriteInventory("xblast", cycle);
    }

    /// <summary>Un repo local con un commit: el «remoto» del que se clona sin red (N-1).</summary>
    private string SourceRepo()
    {
        string path = Path.Combine(_root, "origen");
        Directory.CreateDirectory(path);
        Repository.Init(path);
        File.WriteAllText(Path.Combine(path, "A.cs"), "class A { }");
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
        var who = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
        repo.Commit("inicial", who, who);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Windows puede tener handles de git abiertos; el temporal se lo lleva el sistema.
        }
    }
}
