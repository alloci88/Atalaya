using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9 §3 — el inventario entero, conducido: la deriva llega, los contadores cuadran, el filtro
/// recorta y «Seleccionar cambiadas» marca lo que hay que re-auditar y nada más.
/// <para>
/// Se conduce la página con un clon de VERDAD y su historial. Un doble de git probaría el doble;
/// lo que aquí importa es que la respuesta que da la vista sale del repositorio real.
/// </para>
/// </summary>
public sealed class DriftInventoryFlowTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ToastCenter _toasts = new();
    private readonly ServiceProvider _provider;
    private int _n;

    public DriftInventoryFlowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-drift-flow", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(_clone, RepoUrl);
        _machines.SetClonePath("app", _clone);

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(_machines);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ICopilotAgent>(new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            sp.GetRequiredService<ICopilotAgent>(),
            sp.GetRequiredService<OpenSessionStore>(),
            sp.GetRequiredService<HubContext>()));
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<SessionViewModel>();
        services.AddSingleton<CostEstimator>();
        services.AddSingleton<GroupExpansionMemory>();
        services.AddSingleton<IAuditLaunchConfirmer>(new AlwaysConfirms());
        services.AddSingleton(_toasts);
        services.AddSingleton<CloneLinkService>();
        services.AddSingleton<InventoryRescanService>();
        services.AddSingleton<IFolderPicker, TestFactory.NoFolderPicker>();
        services.AddSingleton<ILinkCloneDialog, TestFactory.NoLinkCloneDialog>();
        services.AddSingleton<LinkCloneFlow>();
        services.AddSingleton<GovernanceService>();
        services.AddSingleton<IPatternSilencesDialog, TestFactory.NoPatternSilencesDialog>();
        services.AddSingleton<DirectiveScanner>();
        services.AddSingleton<DirectiveService>();
        services.AddSingleton<IDirectivesDialog, TestFactory.NoDirectivesDialog>();
        services.AddSingleton(sp => new DriftQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton<IDeletedUnitsDialog, TestFactory.NoDeletedUnitsDialog>();
        services.AddTransient<InventoryViewModel>();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task La_pagina_cuenta_cambiadas_y_arregladas_por_separado()
    {
        string c1 = Commit("inicial", ("A.cs", "1"), ("B.cs", "1"), ("C.cs", "1"));
        Audit(c1, "A.cs", "B.cs", "C.cs");

        // B la toca alguien; C la arregla Atalaya y la commitea el usuario; A no se toca.
        Commit("alguien toca B", ("B.cs", "2"));
        File.WriteAllText(Path.Combine(_clone, "C.cs"), "arreglada");
        RecordFix("C.cs");
        Commit("fix de C");

        InventoryViewModel vm = await Page();

        vm.ChangedUnits.Should().Be(1);
        vm.FixedPendingVerify.Should().Be(1);
        vm.NoHistoryUnits.Should().Be(0);
        vm.HasDrift.Should().BeTrue();
        vm.DriftBranchLabel.Should().Contain("master");
    }

    [Fact]
    public async Task Seleccionar_cambiadas_marca_solo_las_cambiadas()
    {
        string c1 = Commit("inicial", ("A.cs", "1"), ("B.cs", "1"), ("C.cs", "1"));
        Audit(c1, "A.cs", "B.cs");
        Pending("C.cs");
        Commit("toca B y C", ("B.cs", "2"), ("C.cs", "2"));

        InventoryViewModel vm = await Page();
        vm.SelectChangedCommand.Execute(null);

        vm.SelectedUnits().Should().Equal(new[] { "B.cs" },
            "A no cambió y C nunca se auditó — eso es cobertura, no deriva");
        vm.ChangedToggleLabel.Should().Be("Deseleccionar cambiadas", "el botón es simétrico");
    }

    [Fact]
    public async Task Seleccionar_cambiadas_no_arrastra_las_arregladas_sin_verificar()
    {
        string c1 = Commit("inicial", ("A.cs", "1"), ("B.cs", "1"));
        Audit(c1, "A.cs", "B.cs");
        Commit("alguien toca A", ("A.cs", "2"));
        File.WriteAllText(Path.Combine(_clone, "B.cs"), "arreglada");
        RecordFix("B.cs");
        Commit("fix de B");

        InventoryViewModel vm = await Page();
        vm.SelectChangedCommand.Execute(null);

        vm.SelectedUnits().Should().Equal(new[] { "A.cs" },
            "lo arreglado se comprueba verificando, no gastando una auditoría entera");
    }

    [Fact]
    public async Task Sin_cambiadas_el_boton_lo_dice_y_no_marca_nada()
    {
        string c1 = Commit("inicial", ("A.cs", "1"));
        Audit(c1, "A.cs");

        InventoryViewModel vm = await Page();
        vm.SelectChangedCommand.Execute(null);

        vm.SelectedUnits().Should().BeEmpty();
        _toasts.Items.Should().Contain(t => t.Text.Contains("Ninguna unidad auditada ha cambiado"));
    }

    [Fact]
    public async Task El_filtro_de_cambiadas_recorta_y_ordena_por_numero_de_commits()
    {
        string c1 = Commit("inicial", ("A.cs", "1"), ("B.cs", "1"), ("C.cs", "1"));
        Audit(c1, "A.cs", "B.cs", "C.cs");
        Commit("toca C", ("C.cs", "2"));
        Commit("toca B (1)", ("B.cs", "2"));
        Commit("toca B (2)", ("B.cs", "3"));
        Commit("toca B (3)", ("B.cs", "4"));

        InventoryViewModel vm = await Page();
        vm.DriftFilter = 1;

        var shown = vm.Modules.SelectMany(m => m.Units).Select(u => u.Path).ToList();
        shown.Should().Equal(new[] { "B.cs", "C.cs" },
            "más toqueteada, antes; A no cambió y no sale");
    }

    [Fact]
    public async Task El_indicador_de_la_fila_es_ortogonal_al_estado_de_auditoria()
    {
        string c1 = Commit("inicial", ("A.cs", "1"));
        Audit(c1, "A.cs");
        Commit("toca A", ("A.cs", "2"));

        InventoryViewModel vm = await Page();
        UnitNode a = vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == "A.cs");

        a.StateLabel.Should().Be("Auditada");
        a.HasDrift.Should().BeTrue();
        a.DriftLabel.Should().Be("Cambiada desde la auditoría (1 commit)");
        a.DriftTooltip.Should().Contain("Candidata a re-auditar");
    }

    [Fact]
    public async Task Sin_clon_la_pagina_lo_dice_en_vez_de_enseñar_un_cero()
    {
        string c1 = Commit("inicial", ("A.cs", "1"));
        Audit(c1, "A.cs");
        MachineConfig config = _machines.Load();
        config.ClonePaths.Remove("app");
        _machines.Save(config);

        InventoryViewModel vm = await Page();

        vm.ChangedUnits.Should().Be(0);
        vm.HasDrift.Should().BeFalse("sin deriva conocida no se estrenan líneas");
        vm.HasDriftProblem.Should().BeTrue();
        vm.DriftProblem.Should().Contain("Vincula tu clon");
    }

    // ============================================ F12 §C · la vista se refresca sola

    /// <summary>
    /// F12 §C — la fila del inventario tiene que enterarse de que el arreglo ya se verificó, sin
    /// reiniciar la aplicación. En el banco de pruebas no se enteraba: la marca «Arreglada —
    /// pendiente de verificar» seguía puesta hasta el reinicio, porque la caché de la deriva no
    /// tenía la cobertura en su clave.
    /// <para>
    /// La página se pide DOS veces al mismo contenedor, así que las dos comparten el
    /// <see cref="DriftQuery"/> singleton: es el escenario real —volver al inventario— y no un
    /// servicio recién construido, que se habría enterado por no tener caché que consultar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_fila_del_inventario_se_entera_de_la_verificacion_sin_reiniciar()
    {
        string c1 = Commit("inicial", ("A.cs", "1"));
        Audit(c1, "A.cs");
        File.WriteAllText(Path.Combine(_clone, "A.cs"), "arreglada");
        Ulid finding = RecordFixFor("A.cs");
        Commit("fix de A");

        InventoryViewModel antes = await Page();
        antes.FixedPendingVerify.Should().Be(1);
        Row(antes, "A.cs").DriftLabel.Should().Be("Arreglada — pendiente de verificar");

        VerifyGreen(finding);

        InventoryViewModel despues = await Page();
        despues.FixedPendingVerify.Should().Be(0);
        despues.ChangedUnits.Should().Be(0, "y no reaparece por la otra puerta");
        Row(despues, "A.cs").HasDrift.Should().BeFalse("el ciclo del arreglo está cerrado");
    }

    /// <summary>Y la tarjeta del portafolio, que lee la misma consulta, tampoco se queda atrás.</summary>
    [Fact]
    public async Task La_tarjeta_del_portafolio_se_entera_de_la_verificacion_sin_reiniciar()
    {
        string c1 = Commit("inicial", ("A.cs", "1"));
        Audit(c1, "A.cs");
        File.WriteAllText(Path.Combine(_clone, "A.cs"), "arreglada");
        Ulid finding = RecordFixFor("A.cs");
        Commit("fix de A");

        PortfolioViewModel antes = await Cards();
        antes.Apps.Single().FixedPendingVerify.Should().Be(1);

        VerifyGreen(finding);

        PortfolioViewModel despues = await Cards();
        despues.Apps.Single().FixedPendingVerify.Should().Be(0);
        despues.Apps.Single().ChangedUnits.Should().Be(0);
    }

    private static UnitNode Row(InventoryViewModel vm, string path)
        => vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == path);

    private async Task<PortfolioViewModel> Cards()
    {
        var vm = new PortfolioViewModel(
            new PortfolioQuery(_hub.Store),
            _provider.GetRequiredService<NavigationService>(),
            new AppDeletionService(_hub, _machines, _provider.GetRequiredService<OpenSessionStore>()),
            new NeverDeletes(),
            _provider.GetRequiredService<LiveSessionService>(),
            _hub,
            _toasts,
            _provider.GetRequiredService<CloneLinkService>(),
            _provider.GetRequiredService<LinkCloneFlow>(),
            _provider.GetRequiredService<DriftQuery>());
        await vm.LoadAsync();
        return vm;
    }

    /// <summary>Un arreglo CON su hallazgo, que es lo que permite que una verificación lo cubra.</summary>
    private Ulid RecordFixFor(string path)
    {
        Ulid finding = _ulids.NewUlid();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "t");
        _hub.Store.WriteFinding("app", new Finding
        {
            Id = finding,
            RuleId = "R-1",
            Title = $"hallazgo de {path}",
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Locations = { new Location(path, 1) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });

        _hub.Store.WriteFix(new FixRecord
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            By = "t",
            Utc = DateTimeOffset.UtcNow,
            FindingId = finding.ToString(),
            Files = { new FixFileStamp(
                path,
                Domain.Hashing.HashUtil.NormalizedContentHash(
                    File.ReadAllBytes(Path.Combine(_clone, path)))) },
        });

        return finding;
    }

    private void VerifyGreen(Ulid findingId)
    {
        Finding f = _hub.Store.TryReadFinding("app", findingId.ToString())!;
        f.Resolve(new ResolutionStamp(
            DateTimeOffset.UtcNow, ResolutionVia.Verify, AuditMode.Verify, "def5678", "t",
            "el defecto ya no está"));
        _hub.Store.WriteFinding("app", f);
    }

    // ================================================================= helpers

    private async Task<InventoryViewModel> Page()
    {
        var vm = _provider.GetRequiredService<InventoryViewModel>();
        vm.SetApp("app");
        await vm.LoadAsync();
        return vm;
    }

    private string Commit(string message, params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            File.WriteAllText(Path.Combine(_clone, path), content);
        }

        using var repo = new Repository(_clone);
        Commands.Stage(repo, "*");
        var who = new Signature("T", "t@e.com", DateTimeOffset.UtcNow.AddSeconds(_n++));
        return repo.Commit(message, who, who).Sha[..7];
    }

    private void Audit(string commit, params string[] paths)
    {
        Ulid session = _ulids.NewUlid();
        _hub.Store.WriteSession(new AuditSession
        {
            Id = session, AppSlug = "app", Mode = AuditMode.Lotes, By = "t", Machine = "m",
            StartedUtc = DateTimeOffset.UtcNow, Commit = commit, CycleN = 1,
        });

        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1) ?? new InventoryCycle { CycleN = 1 };
        foreach (string path in paths)
        {
            InventoryUnit unit = inv.Units.FirstOrDefault(u => u.Path == path)
                ?? Add(inv, path);
            unit.State = UnitState.Auditada;
            unit.AuditedInSession = session;
        }

        _hub.Store.WriteInventory("app", inv);
    }

    private void Pending(params string[] paths)
    {
        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1) ?? new InventoryCycle { CycleN = 1 };
        foreach (string path in paths.Where(p => inv.Units.All(u => u.Path != p)))
        {
            Add(inv, path);
        }

        _hub.Store.WriteInventory("app", inv);
    }

    private static InventoryUnit Add(InventoryCycle inv, string path)
    {
        var unit = new InventoryUnit { Path = path, Module = "raiz" };
        inv.Units.Add(unit);
        return unit;
    }

    private void RecordFix(params string[] paths)
        => _hub.Store.WriteFix(new FixRecord
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            By = "t",
            Utc = DateTimeOffset.UtcNow,
            Files = paths.Select(p => new FixFileStamp(
                p,
                Domain.Hashing.HashUtil.NormalizedContentHash(
                    File.ReadAllBytes(Path.Combine(_clone, p))))).ToList(),
        });

    /// <summary>El portafolio de estos tests no borra nada: el confirmador dice que no.</summary>
    private sealed class NeverDeletes : IDeleteAppConfirmer
    {
        public bool Confirm(DeleteAppConfirmation confirmation) => false;
    }

    private sealed class AlwaysConfirms : IAuditLaunchConfirmer
    {
        public bool Confirm(AuditLaunchConfirmation confirmation) => true;
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, true);
        }
        catch
        {
            // Limpieza best-effort.
        }
    }
}
