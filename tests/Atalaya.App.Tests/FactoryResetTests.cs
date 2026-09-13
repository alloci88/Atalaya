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
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.7 §5 — el restablecimiento de fábrica.
/// <para>
/// <b>Qué se fija.</b> Que vacía el hub y lo publica (contra un <c>--bare</c> local, norma N-1:
/// sin red); que deja esta máquina como recién instalada; que un segundo usuario ve desaparecer
/// todo al sincronizar; y —lo que de verdad importa— que si el push NO llega, <b>no se borra
/// nada</b>: ni el hub local ni el estado de la máquina.
/// </para>
/// <para>
/// Ese último caso es la razón de que la operación tenga un orden y un punto de retorno. El estado
/// peor posible no es «no se pudo resetear», es «tu máquina limpia y el hub lleno»: sin cuenta, sin
/// ajustes y sin clon, quien lo pulsó ya no puede ni reintentarlo ni explicar qué pasó.
/// </para>
/// </summary>
public sealed class FactoryResetTests : IDisposable
{
    private readonly string _root;
    private readonly string _remote;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly MachineConfigStore _machines;
    private readonly OpenSessionStore _openSession;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public FactoryResetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-reset", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        TestGit.Init(_remote, isBare: true);

        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _settings.Save(_settings.Current);   // que settings.json exista: es de lo que se borra
        _account = TestFactory.Account(_paths);
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _openSession = new OpenSessionStore(_paths);
    }

    private HubContext Hub() => new(
        _paths, _settings, _account,
        new DeployConfig { HubUrl = _remote },
        NullLoggerFactory.Instance);

    private FactoryResetService Reset(HubContext hub)
        => new(hub, _paths, _settings, _account, _openSession, Secrets);

    /// <summary>El almacén de claves de API: PROV-3 §3 lo mete en lo que el reset se lleva.</summary>
    private ProviderSecretStore Secrets => new(_paths);

    /// <summary>Una app con traza real: hallazgos, sesión, informe y ruta de clon en esta máquina.</summary>
    private void SeedApp(HubContext hub, string slug, string name)
    {
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        hub.Store.WriteApp(new AppConfig
        {
            Slug = slug, Name = name, RepoUrl = "https://example/x.git",
            Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        hub.Store.WriteFinding(slug, NewFinding());
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
        _machines.SetClonePath(slug, Path.Combine(_root, $"clon-{slug}"));
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

    private HubContext SeededHub(params string[] slugs)
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        foreach (string slug in slugs)
        {
            SeedApp(hub, slug, slug.ToUpperInvariant());
        }

        hub.Sync!.CommitAndPush($"app: onboard {slugs.Length}").Should().BeTrue(hub.Sync!.Why());
        return hub;
    }

    // ---------- El camino feliz: hub vaciado y máquina como recién instalada ----------

    [Fact]
    public void The_reset_empties_every_app_in_the_hub_and_publishes_it()
    {
        HubContext hub = SeededHub("webapp", "otra");

        FactoryResetResult result = Reset(hub).Reset("ana");

        result.Done.Should().BeTrue(result.Message);

        // En el remoto de verdad: un clon nuevo no ve ninguna app.
        string check = Path.Combine(_root, "check");
        Repository.Clone(_remote, check);
        Directory.Exists(Path.Combine(check, "apps")).Should().BeFalse(
            "un reset que solo ocurre en local reaparece en el siguiente pull");
        File.Exists(Path.Combine(check, "hub.json")).Should().BeTrue(
            "el hub sigue existiendo: lo que se vacía son las aplicaciones, no el repositorio");
    }

    [Fact]
    public void The_reset_commit_says_who_did_it()
    {
        HubContext hub = SeededHub("webapp");

        Reset(hub).Reset("ana").Done.Should().BeTrue();

        // El clon local ya no está; la traza se lee en el remoto, que es donde queda para todos.
        string check = Path.Combine(_root, "check-msg");
        Repository.Clone(_remote, check);
        using var repo = new Repository(check);
        repo.Head.Tip.Message.Should().Contain("hub: reset de fábrica por ana",
            "es la única traza que queda de la decisión");
    }

    [Fact]
    public void The_reset_leaves_this_machine_as_freshly_installed()
    {
        HubContext hub = SeededHub("webapp");
        Directory.Exists(_paths.Hub).Should().BeTrue();
        File.Exists(_paths.MachinesJson).Should().BeTrue();

        Reset(hub).Reset("ana").Done.Should().BeTrue();

        Directory.Exists(_paths.Hub).Should().BeFalse("el clon del hub se va entero");
        File.Exists(_paths.MachinesJson).Should().BeFalse("las rutas de clon son estado de esta máquina");
        File.Exists(_paths.SettingsJson).Should().BeFalse("los ajustes vuelven a los de fábrica");
        File.Exists(_paths.AuthDat).Should().BeFalse("la cuenta se desconecta");
        _account.IsConnected.Should().BeFalse();
        _settings.Current.MaxPassesPerUnit.Should().Be(SettingsLimits.DefaultMaxPassesPerUnit,
            "y los valores en memoria también son los de fábrica");
    }

    /// <summary>Los logs NO se borran: son justamente lo que hace falta si el reset sale mal.</summary>
    [Fact]
    public void The_reset_keeps_the_logs()
    {
        HubContext hub = SeededHub("webapp");
        Directory.CreateDirectory(_paths.Logs);
        File.WriteAllText(Path.Combine(_paths.Logs, "atalaya-.log"), "traza");

        Reset(hub).Reset("ana").Done.Should().BeTrue();

        File.Exists(Path.Combine(_paths.Logs, "atalaya-.log")).Should().BeTrue();
    }

    /// <summary>
    /// Dos clones contra el mismo <c>--bare</c>: el reset de uno llega al otro por su sync normal.
    /// Es la consecuencia que el diálogo promete y la que hace este botón peligroso de verdad.
    /// </summary>
    [Fact]
    public void A_second_user_sees_everything_disappear_after_syncing()
    {
        HubContext hub = SeededHub("webapp", "otra");

        var mariaPaths = new HubPaths(Path.Combine(_root, "maria"));
        using var maria = new HubSyncService(mariaPaths, ("Maria", "maria@example.com"));
        maria.EnsureCloned(_remote);
        var mariaStore = new HubStore(mariaPaths);
        mariaStore.ListAppSlugs().Should().HaveCount(2);

        Reset(hub).Reset("ana").Done.Should().BeTrue();

        maria.Pull();

        mariaStore.ListAppSlugs().Should().BeEmpty();
        Directory.Exists(mariaPaths.AppsDir).Should().BeFalse();
    }

    /// <summary>Un hub ya vacío no es un error: el reset sigue limpiando esta máquina.</summary>
    [Fact]
    public void Resetting_an_already_empty_hub_still_cleans_this_machine()
    {
        HubContext hub = Hub();
        hub.EnsureHub();

        FactoryResetResult result = Reset(hub).Reset("ana");

        result.Done.Should().BeTrue(result.Message);
        _account.IsConnected.Should().BeFalse();
        Directory.Exists(_paths.Hub).Should().BeFalse();
    }

    // ---------- La atomicidad: si el push no llega, no se borra NADA ----------

    /// <summary>
    /// El caso que justifica el orden entero. Sin remoto —sin permisos, sin red— el borrado no se
    /// puede publicar, así que se deshace: el clon vuelve a su commit anterior y la máquina queda
    /// exactamente como estaba.
    /// </summary>
    [Fact]
    public void A_failed_push_aborts_the_reset_without_touching_anything()
    {
        HubContext hub = SeededHub("webapp", "otra");
        string headBefore = hub.Sync!.HeadCommitSha!;

        DeleteTree(_remote);   // lo que ve quien no tiene permisos o se quedó sin red

        FactoryResetResult result = Reset(hub).Reset("ana");

        result.Done.Should().BeFalse();
        result.Message.Should().Contain("No se ha borrado nada");

        // El hub local, intacto: las apps siguen ahí y el clon en su commit de antes.
        hub.Store.ListAppSlugs().Should().Equal("otra", "webapp");
        Directory.Exists(hub.HubPaths.AppDir("webapp")).Should().BeTrue();
        hub.Sync.HeadCommitSha.Should().Be(headBefore, "el commit del borrado se deshace");

        // Y la máquina, intacta: es lo que no se puede perder.
        Directory.Exists(_paths.Hub).Should().BeTrue();
        File.Exists(_paths.MachinesJson).Should().BeTrue();
        File.Exists(_paths.SettingsJson).Should().BeTrue();
        _account.IsConnected.Should().BeTrue("desconectar sin haber borrado el hub deja lo peor de los dos mundos");
    }

    /// <summary>Sin hub configurado no hay nada que publicar: se dice y no se toca lo local.</summary>
    [Fact]
    public void With_no_hub_configured_the_reset_refuses_instead_of_wiping_the_machine()
    {
        var hub = new HubContext(
            _paths, _settings, _account, new DeployConfig(), NullLoggerFactory.Instance);

        FactoryResetResult result = Reset(hub).Reset("ana");

        result.Done.Should().BeFalse();
        result.Message.Should().Contain("no se ha borrado nada");
        _account.IsConnected.Should().BeTrue();
        File.Exists(_paths.SettingsJson).Should().BeTrue();
    }

    // ---------- Lo que enumera la confirmación ----------

    [Fact]
    public void The_impact_counts_what_is_actually_in_the_hub()
    {
        HubContext hub = SeededHub("webapp", "otra");

        FactoryResetImpact impact = Reset(hub).Describe();

        impact.Apps.Should().Be(2);
        impact.Findings.Should().Be(2);
        impact.Sessions.Should().Be(2);
        impact.HubIsEmpty.Should().BeFalse();
        impact.Describe().Should().Contain("2 aplicación(es)")
            .And.Contain("2 hallazgo(s)")
            .And.Contain("2 sesión(es)");
        impact.TeamWarning.Should().Contain("TODO el equipo").And.Contain("sincronización");
        impact.LocalWarning.Should().Contain("machines.json").And.Contain("cuenta");
        impact.Reassurance.Should().Contain("historial git");
    }

    // ---------- La confirmación fuerte ----------

    [Fact]
    public void The_red_button_stays_locked_until_the_word_RESET_is_typed()
    {
        var confirmation = new FactoryResetConfirmation(new FactoryResetImpact(3, 40, 12));

        confirmation.CanReset.Should().BeFalse("nada escrito todavía");

        confirmation.TypedWord = "RES";
        confirmation.CanReset.Should().BeFalse("un prefijo no es la palabra");

        confirmation.TypedWord = "reset";
        confirmation.CanReset.Should().BeFalse("la comparación es exacta: si no, vuelve a ser un sí/no");

        confirmation.TypedWord = "  RESET  ";
        confirmation.CanReset.Should().BeTrue("los espacios de sobra al teclear no son un error del usuario");
    }

    [Fact]
    public void The_confirmation_enumerates_the_damage_before_asking()
    {
        var confirmation = new FactoryResetConfirmation(new FactoryResetImpact(3, 40, 12));

        confirmation.Warning.Should().Contain("3 aplicación(es)").And.Contain("40 hallazgo(s)");
        confirmation.TeamWarning.Should().Contain("tus compañeros");
        confirmation.Prompt.Should().Contain("RESET");
    }

    // ---------- El flujo desde Ajustes ----------

    [Fact]
    public async Task Cancelling_the_dialog_resets_nothing()
    {
        HubContext hub = SeededHub("webapp");
        SettingsViewModel vm = Settings(hub, new StubConfirmer(answer: false));

        await vm.FactoryResetCommand.ExecuteAsync(null);

        hub.Store.ListAppSlugs().Should().Contain("webapp");
        _account.IsConnected.Should().BeTrue();
    }

    /// <summary>
    /// El diálogo puede devolver «sí» sin la palabra escrita (una vista mal enlazada, un
    /// confirmador ajeno): el view-model vuelve a mirar la puerta antes de destruir nada.
    /// </summary>
    [Fact]
    public async Task A_yes_without_the_typed_word_still_resets_nothing()
    {
        HubContext hub = SeededHub("webapp");
        SettingsViewModel vm = Settings(hub, new StubConfirmer(answer: true, typeTheWord: false));

        await vm.FactoryResetCommand.ExecuteAsync(null);

        hub.Store.ListAppSlugs().Should().Contain("webapp");
        _account.IsConnected.Should().BeTrue();
    }

    /// <summary>Y el fallo se cuenta por toast, que es donde se ve sin bajar a buscarlo (§4).</summary>
    [Fact]
    public async Task A_failed_reset_is_reported_by_toast()
    {
        HubContext hub = SeededHub("webapp");
        DeleteTree(_remote);
        var confirmer = new StubConfirmer(answer: true);
        SettingsViewModel vm = Settings(hub, confirmer);

        await vm.FactoryResetCommand.ExecuteAsync(null);

        confirmer.Asked.Should().NotBeNull("nunca se resetea sin preguntar");
        confirmer.Asked!.Warning.Should().Contain("1 aplicación(es)");
        _toasts.Items.Should().Contain(t => t.Text.Contains("No se ha borrado nada"));
        hub.Store.ListAppSlugs().Should().Contain("webapp");
    }

    private SettingsViewModel Settings(HubContext hub, IFactoryResetConfirmer confirmer)
        => new(
            _settings,
            new FakeCopilotAgent(),
            _toasts,
            Reset(hub),
            confirmer,
            hub,
            new NavigationService(new EmptyServices()));

    /// <summary>El confirmador de los tests: responde lo que se le diga y guarda lo que le pidieron.</summary>
    private sealed class StubConfirmer : IFactoryResetConfirmer
    {
        private readonly bool _answer;
        private readonly bool _typeTheWord;

        public StubConfirmer(bool answer, bool typeTheWord = true)
        {
            _answer = answer;
            _typeTheWord = typeTheWord;
        }

        public FactoryResetConfirmation? Asked { get; private set; }

        public bool Confirm(FactoryResetConfirmation confirmation)
        {
            Asked = confirmation;
            if (_typeTheWord)
            {
                confirmation.TypedWord = FactoryResetConfirmation.RequiredWord;
            }

            return _answer;
        }
    }

    /// <summary>La navegación no se ejercita aquí; basta con que exista.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Borrado a lo bruto: los objetos de git son de solo lectura.</summary>
    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }

        Directory.Delete(dir, recursive: true);
    }

    public void Dispose()
    {
        try
        {
            DeleteTree(_root);
        }
        catch
        {
            // Limpieza best-effort: un handle de git retenido no puede tumbar un test.
        }
    }
}
