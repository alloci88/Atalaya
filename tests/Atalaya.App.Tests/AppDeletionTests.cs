using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.3 §4 — eliminar / hard-reset de una aplicación desde el Portafolio.
/// <para>
/// <b>Qué se fija.</b> Que el borrado se lleva la carpeta ENTERA del hub y llega al remoto (contra
/// un <c>--bare</c> local, norma N-1: sin red); que la confirmación no se abre paso sin escribir el
/// nombre de la app; que el icono está deshabilitado mientras alguien la audita; y que un segundo
/// usuario, en su propio clon, la ve desaparecer al sincronizar.
/// </para>
/// <para>
/// El último es el que de verdad importa: un borrado que solo ocurre en la máquina de quien pulsa
/// deja la app viva para el resto y la hace reaparecer en el siguiente pull.
/// </para>
/// </summary>
public sealed class AppDeletionTests : IDisposable
{
    private readonly string _root;
    private readonly string _remote;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly MachineConfigStore _machines;
    private readonly OpenSessionStore _openSession;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public AppDeletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-delete", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        Repository.Init(_remote, isBare: true);

        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _openSession = new OpenSessionStore(_paths);
    }

    private HubContext Hub() => new(
        _paths, _settings, _account,
        new DeployConfig { HubUrl = _remote },
        NullLoggerFactory.Instance);

    private AppDeletionService Deletion(HubContext hub) => new(hub, _machines, _openSession);

    /// <summary>Una app con traza en las seis carpetas: es lo que el hard-reset debe llevarse.</summary>
    private void SeedApp(HubContext hub, string slug = "webapp", string name = "Web App")
    {
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        hub.Store.WriteApp(new AppConfig
        {
            Slug = slug, Name = name, RepoUrl = "https://example/x.git",
            Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        hub.Store.WriteInventory(slug, new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        Finding finding = NewFinding();
        hub.Store.WriteFinding(slug, finding);
        hub.Store.WriteSilence(slug, new Silence
        {
            FindingUlid = finding.Id,
            Reason = SilenceReason.FalsoPositivo,
            By = "ana",
            Utc = DateTimeOffset.UtcNow,
        });
        hub.Store.WriteSession(new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Lotes,
            By = "ana",
            Machine = "PC",
            Commit = "abc1234",
            StartedUtc = DateTimeOffset.UtcNow,
            EndedUtc = DateTimeOffset.UtcNow,
        });
        hub.Store.WriteReport(slug, _ulids.NewUlid().ToString(), "# informe");
        _machines.SetClonePath(slug, Path.Combine(_root, "clon-de-la-app"));
    }

    private Finding NewFinding()
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "ana");
        return new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Critica,
            Confidence = Confidence.Media,
            Title = "Conn leaked",
            Description = "desc",
            Impact = "impact",
            Recommendation = "reco",
            Locations = { new Location("A.cs", 12, "sha256:snip") },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
    }

    // ---------- El borrado en el hub ----------

    [Fact]
    public void Deleting_an_app_takes_its_whole_folder_and_publishes_it()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        AppDeletionResult result = Deletion(hub).Delete("webapp", "ana");

        result.Removed.Should().BeTrue();
        result.Pushed.Should().BeTrue();
        Directory.Exists(hub.HubPaths.AppDir("webapp")).Should().BeFalse();
        hub.Store.ListAppSlugs().Should().NotContain("webapp");

        // Y en el remoto de verdad: un clon nuevo no ve nada de la app en HEAD.
        string check = Path.Combine(_root, "check");
        Repository.Clone(_remote, check);
        Directory.Exists(Path.Combine(check, "apps", "webapp")).Should().BeFalse(
            "un borrado que solo ocurre en local reaparece en el siguiente pull");
    }

    [Fact]
    public void The_deletion_commit_says_who_did_it()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        Deletion(hub).Delete("webapp", "ana");

        using var repo = new Repository(hub.HubPaths.Root);
        repo.Head.Tip.Message.Should().Contain("app: hard-reset de webapp por ana",
            "es la única traza que queda de la decisión");
    }

    [Fact]
    public void Deleting_an_app_clears_its_local_state_on_this_machine()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();
        _machines.Load().ClonePathFor("webapp").Should().NotBeNull();

        Deletion(hub).Delete("webapp", "ana");

        _machines.Load().ClonePathFor("webapp").Should().BeNull(
            "la ruta del clon es por máquina; dejarla apuntaría a una app que ya no existe");
    }

    /// <summary>Solo esa app: el hard-reset no puede llevarse por delante a las vecinas.</summary>
    [Fact]
    public void Deleting_one_app_leaves_the_others_untouched()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub, "webapp", "Web App");
        SeedApp(hub, "otra", "Otra");
        hub.Sync!.CommitAndPush("app: onboard x2").Should().BeTrue();

        Deletion(hub).Delete("webapp", "ana");

        hub.Store.ListAppSlugs().Should().Equal("otra");
        hub.Store.ListFindings("otra").Should().ContainSingle();
        _machines.Load().ClonePathFor("otra").Should().NotBeNull();
    }

    /// <summary>El desglose que lee la confirmación sale del hub, no de una promesa genérica.</summary>
    [Fact]
    public void The_impact_counts_what_is_actually_there()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);

        AppDeletionImpact impact = Deletion(hub).Describe("webapp")!;

        impact.Name.Should().Be("Web App");
        impact.Findings.Should().Be(1);
        impact.Sessions.Should().Be(1);
        impact.Reports.Should().Be(1);
        impact.Silences.Should().Be(1);
        impact.Describe().Should().Contain("1 hallazgo(s)").And.Contain("1 informe(s)");
    }

    // ---------- El segundo usuario ----------

    /// <summary>
    /// Dos clones contra el mismo <c>--bare</c>: el borrado de uno llega al otro por su sync
    /// normal, que es lo que convierte «lo quité de mi pantalla» en «ya no existe para el equipo».
    /// </summary>
    [Fact]
    public void A_second_user_sees_the_app_disappear_after_syncing()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        // Maria clona y la ve.
        var mariaPaths = new HubPaths(Path.Combine(_root, "maria"));
        using var maria = new HubSyncService(mariaPaths, ("Maria", "maria@example.com"));
        maria.EnsureCloned(_remote);
        var mariaStore = new HubStore(mariaPaths);
        mariaStore.ListAppSlugs().Should().Contain("webapp");

        Deletion(hub).Delete("webapp", "ana").Pushed.Should().BeTrue();

        maria.Pull();

        mariaStore.ListAppSlugs().Should().NotContain("webapp");
        Directory.Exists(mariaPaths.AppDir("webapp")).Should().BeFalse();
    }

    // ---------- La confirmación fuerte ----------

    [Fact]
    public void The_red_button_stays_locked_until_the_app_name_is_typed()
    {
        var confirmation = new DeleteAppConfirmation(
            new AppDeletionImpact("webapp", "Web App", 12, 3, 3, 1));

        confirmation.CanDelete.Should().BeFalse("nada escrito todavía");

        confirmation.TypedName = "Web";
        confirmation.CanDelete.Should().BeFalse("un prefijo no es el nombre");

        confirmation.TypedName = "web app";
        confirmation.CanDelete.Should().BeFalse("la comparación es exacta: si no, vuelve a ser un sí/no");

        confirmation.TypedName = "  Web App  ";
        confirmation.CanDelete.Should().BeTrue("los espacios de sobra al teclear no son un error del usuario");
    }

    [Fact]
    public void The_confirmation_names_what_is_lost_and_what_is_not()
    {
        var confirmation = new DeleteAppConfirmation(
            new AppDeletionImpact("webapp", "Web App", 12, 3, 3, 1));

        confirmation.Warning.Should().Contain("Web App")
            .And.Contain("12 hallazgo(s)")
            .And.Contain("3 sesión(es)")
            .And.Contain("3 informe(s)")
            .And.Contain("1 silencio(s)");
        confirmation.Reassurance.Should().Contain("historial git")
            .And.Contain("recuperable por un administrador");
        confirmation.Prompt.Should().Contain("Web App");
    }

    // ---------- El icono y el flujo del Portafolio ----------

    /// <summary>
    /// Con un claim vivo la app se está auditando: la papelera queda deshabilitada y el tooltip
    /// dice qué hacer. Un hard-reset a mitad de sesión dejaría al auditor escribiendo hallazgos en
    /// una carpeta recién borrada.
    /// </summary>
    [Fact]
    public void The_delete_icon_is_disabled_while_the_app_is_being_audited()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        var query = new PortfolioQuery(hub.Store);

        query.Build("webapp")!.CanDelete.Should().BeTrue("nadie la está auditando");

        hub.Store.WriteClaim("webapp", new Claim
        {
            Unit = "A.cs", Module = "M", By = "otro", Machine = "PC-otro",
            Utc = DateTimeOffset.UtcNow, TtlMinutes = 30,
        });

        AppCard claimed = query.Build("webapp")!;
        claimed.CanDelete.Should().BeFalse();
        claimed.DeleteTooltip.Should().Be("Detén la sesión primero");
    }

    /// <summary>Un claim caducado no bloquea nada: para eso tiene TTL.</summary>
    [Fact]
    public void An_expired_claim_does_not_block_the_delete_icon()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Store.WriteClaim("webapp", new Claim
        {
            Unit = "A.cs", Module = "M", By = "otro", Machine = "PC-otro",
            Utc = DateTimeOffset.UtcNow.AddHours(-2), TtlMinutes = 30,
        });

        new PortfolioQuery(hub.Store).Build("webapp")!.CanDelete.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_dialog_deletes_nothing()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        PortfolioViewModel vm = Portfolio(hub, new StubConfirmer(answer: false));
        await vm.LoadAsync();

        await vm.DeleteAppCommand.ExecuteAsync(vm.Apps.Single());

        vm.Apps.Should().ContainSingle();
        Directory.Exists(hub.HubPaths.AppDir("webapp")).Should().BeTrue();
    }

    /// <summary>
    /// Y confirmando, la tarjeta se va EN EL ACTO: esperar a la recarga la dejaría un rato en
    /// pantalla apuntando a una app que ya no existe.
    /// </summary>
    [Fact]
    public async Task Confirming_removes_the_card_and_the_app()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        var confirmer = new StubConfirmer(answer: true);
        PortfolioViewModel vm = Portfolio(hub, confirmer);
        await vm.LoadAsync();

        await vm.DeleteAppCommand.ExecuteAsync(vm.Apps.Single());

        confirmer.Asked.Should().NotBeNull("nunca se borra sin preguntar");
        confirmer.Asked!.RequiredName.Should().Be("Web App");
        vm.Apps.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        Directory.Exists(hub.HubPaths.AppDir("webapp")).Should().BeFalse();
    }

    /// <summary>
    /// El diálogo puede devolver «sí» sin que el nombre esté escrito (una vista mal enlazada, un
    /// confirmador ajeno): el view-model vuelve a mirar la puerta antes de borrar.
    /// </summary>
    [Fact]
    public async Task A_yes_without_the_typed_name_still_deletes_nothing()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        SeedApp(hub);
        hub.Sync!.CommitAndPush("app: onboard webapp").Should().BeTrue();

        PortfolioViewModel vm = Portfolio(hub, new StubConfirmer(answer: true, typeTheName: false));
        await vm.LoadAsync();

        await vm.DeleteAppCommand.ExecuteAsync(vm.Apps.Single());

        Directory.Exists(hub.HubPaths.AppDir("webapp")).Should().BeTrue();
        vm.Apps.Should().ContainSingle();
    }

    private PortfolioViewModel Portfolio(HubContext hub, IDeleteAppConfirmer confirmer)
    {
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                hub, new FindingIngestionService(hub, _ulids), new ReconciliationService(hub),
                _machines, _ulids, agent, _settings),
            agent,
            _openSession);

        return new PortfolioViewModel(
            new PortfolioQuery(hub.Store),
            new NavigationService(new EmptyServices()),
            Deletion(hub),
            confirmer,
            live,
            hub,
            new ToastCenter(),
            TestFactory.Links(hub, _paths),
            TestFactory.LinkFlow(hub, _paths),
            new DriftQuery(hub),
            new ActiveApp(),
            TestFactory.CostGaps(hub));
    }

    /// <summary>El confirmador de los tests: responde lo que se le diga y guarda lo que le pidieron.</summary>
    private sealed class StubConfirmer : IDeleteAppConfirmer
    {
        private readonly bool _answer;
        private readonly bool _typeTheName;

        public StubConfirmer(bool answer, bool typeTheName = true)
        {
            _answer = answer;
            _typeTheName = typeTheName;
        }

        public DeleteAppConfirmation? Asked { get; private set; }

        public bool Confirm(DeleteAppConfirmation confirmation)
        {
            Asked = confirmation;
            if (_typeTheName)
            {
                confirmation.TypedName = confirmation.RequiredName;
            }

            return _answer;
        }
    }

    /// <summary>La navegación no se ejercita aquí; basta con que exista.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Limpieza best-effort: un handle de git retenido no puede tumbar un test.
        }
    }
}
