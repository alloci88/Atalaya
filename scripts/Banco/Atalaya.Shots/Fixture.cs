using System.IO;
using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.Shots;

/// <summary>
/// Un hub temporal con una aplicaciÃ³n, su clon y un agente falso guionizado. Es lo mismo que monta
/// la suite de tests, con dos diferencias: aquÃ­ el agente escribe bastante --seis unidades con
/// varios hallazgos y varias pasadas-- porque lo que se va a mirar es cÃ³mo se ve una pantalla
/// llena, y aquÃ­ se construye la carcasa entera para poder fotografiarla.
/// </summary>
public sealed class Fixture : IDisposable
{
    private readonly string _root;

    private Fixture(string root, MainViewModel shell, LiveSessionService live, LiveFixService fix,
                    string slug, IReadOnlyList<string> units, Func<Ulid?> firstFinding,
                    HubContext hub, MachineConfigStore machines, CloneLinkService links,
                    IUlidFactory ulids, string clone)
    {
        _root = root;
        Shell = shell;
        Live = live;
        Fix = fix;
        Slug = slug;
        Units = units;
        FirstFinding = firstFinding;
        Hub = hub;
        Machines = machines;
        Links = links;
        Ulids = ulids;
        Clone = clone;
    }

    // Lo que el banco de DIALOGOS necesita del mismo montaje: los dialogos son ventanas sueltas
    // con su propio view-model, y sin estas piezas habria que montar un segundo hub.
    public HubContext Hub { get; }

    public MachineConfigStore Machines { get; }

    public CloneLinkService Links { get; }

    public IUlidFactory Ulids { get; }

    public string Clone { get; }

    public MainViewModel Shell { get; }

    public LiveSessionService Live { get; }

    public LiveFixService Fix { get; }

    public string Slug { get; }

    public IReadOnlyList<string> Units { get; }

    public Func<Ulid?> FirstFinding { get; }

    public static Fixture Build()
    {
        Script.Reset();
        string root = Path.Combine(Path.GetTempPath(), "atalaya-shots", Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "local");
        string clone = Path.Combine(root, "clone");
        Directory.CreateDirectory(local);

        var paths = new AppPaths(local);
        var settings = new SettingsService(paths);
        settings.Load();

        var account = new GitHubAccountService(new AccountStore(paths), SystemClock.Instance);
        var hub = new HubContext(
            paths, settings, account, new DeployConfig(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Acme" });

        const string slug = "atalayabanco";
        hub.Store.WriteApp(new AppConfig
        {
            Slug = slug,
            Name = "atalayabanco-app-for-tests",
            RepoUrl = "https://example.invalid/org/atalayabanco.git",
            CurrentCycle = 1,
        });

        string[] units = Code.Write(clone);

        // El inventario del ciclo. Sin él la sesión no tiene nada que reclamar y termina con cero:
        // el coordinador audita lo que está EN EL HUB, no lo que hay en el disco.
        var cycle = new InventoryCycle { CycleN = 1 };
        foreach (string unit in units)
        {
            cycle.Units.Add(new InventoryUnit
            {
                Path = unit,
                Module = unit.Split('/')[^2],
                State = UnitState.Pendiente,
            });
        }

        hub.Store.WriteInventory(slug, cycle);

        var machines = new MachineConfigStore(paths.MachinesJson);
        machines.SetClonePath(slug, clone);

        var ulids = new UlidFactory(SystemClock.Instance);
        var agent = new FakeCopilotAgent(
            auditScript: Script.Audit,
            fixScript: Script.Fix,
            modelName: "claude-opus-4.7");

        var openSession = new OpenSessionStore(paths);
        var busy = new AgentBusyGate();

        var live = new LiveSessionService(
            () => new SessionCoordinator(
                hub,
                new FindingIngestionService(hub, ulids),
                new ReconciliationService(hub),
                machines, ulids, agent, settings),
            agent, openSession, hub, busy: busy);

        var links = new CloneLinkService(hub, machines);
        var fix = new LiveFixService(
            hub, () => agent, machines, ulids, settings,
            new ReferenceCollector(), new FixSnapshotStore(paths),
            new AssistedFixLauncher(settings, links, machines, busy), busy);

        var pages = new Pages();
        var navigation = new NavigationService(pages);

        var shell = new MainViewModel(
            navigation,
            hub, settings,
            account,
            live, fix,
            new InterruptedSessionRecovery(hub, openSession),
            new DisplayIdService(hub),
            new ToastCenter());

        pages.Session = () => new SessionViewModel(live, navigation);
        pages.Fix = () => new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard(), navigation);

        return new Fixture(root, shell, live, fix, slug, units,
            () => hub.Store.ListFindings(slug).FirstOrDefault()?.Id,
            hub, machines, links, ulids, clone);
    }

    /// <summary>Lanza la sesión y NO espera: la foto es de la sesión EN MARCHA.</summary>
    public void StartSession()
        => _ = Live.StartAsync(new SessionRequest(Slug, AuditMode.Lotes, Units, Units.Count), Units);

    /// <summary>Espera a que la sesión llegue a una unidad concreta.</summary>
    public void WaitUntilUnit(int index)
        => Wait(() => Live.UnitIndex >= index || !Live.IsRunning, seconds: 120);

    public void WaitForSession() => Wait(() => !Live.IsRunning, seconds: 180);

    /// <summary>Y el arreglo, sobre el primer hallazgo que dejo la sesion.</summary>
    ///
    /// El agente pide permiso antes de tocar un fichero (ApproveFileAsync) y se queda esperando.
    /// La primera version del banco no contestaba, asi que la captura salia con la tarjeta de
    /// autorizacion en pantalla y el panel de cambios diciendo que no se habia tocado nada: la
    /// vista era real, pero era el minuto equivocado. Aqui se autoriza como lo haria el usuario
    /// --pulsando «Autorizar» en la tarjeta, por el mismo camino que la vista-- y asi el arreglo
    /// llega hasta el final y el diff se llena.
    public void RunFix()
    {
        if (FirstFinding() is not { } id)
        {
            return;
        }

        _ = Fix.StartAsync(new FixSessionRequest(Slug, id));
    }

    /// <summary>
    /// Espera al minuto que hay que fotografiar: el fichero ya tocado --el diff lleno-- con el
    /// arreglo todavia en marcha. La pantalla de cierre es otra cosa y ya se veia; lo que no se
    /// veia era el panel de cambios con algo dentro.
    /// </summary>
    public void WaitForDiff()
        => Wait(
            () => (Fix.Files.Count > 0 && HasPendingDecision()) || !Fix.IsRunning,
            seconds: 120,
            each: AnswerPermissions);

    /// <summary>Hay una decision esperando: el arreglo esta parado y la vista, quieta.</summary>
    private bool HasPendingDecision()
        => Fix.Conversation.Any(e => e is FixQuestion { Kind: FixAskKind.Decision, IsAnswered: false });

    public void WaitForFix() => Wait(() => !Fix.IsRunning, seconds: 120, each: AnswerEverything);

    /// <summary>Autoriza Y decide: lo que hace falta para que el arreglo llegue a su cierre.</summary>
    private void AnswerEverything()
    {
        foreach (FixEntry entry in Fix.Conversation.ToArray())
        {
            if (entry is FixQuestion { IsAnswered: false } q)
            {
                Fix.Answer(q, q.Choices.Count > 0
                    ? q.Choices[0].Label
                    : "Dejalos para su arreglo");
            }
        }
    }

    /// <summary>Autoriza las tarjetas de permiso que esten esperando. Idempotente.</summary>
    private void AnswerPermissions()
    {
        foreach (FixEntry entry in Fix.Conversation.ToArray())
        {
            if (entry is FixQuestion { Kind: FixAskKind.Autorizacion, IsAnswered: false } question)
            {
                Fix.Answer(question, LiveFixService.ApproveLabel);
            }
        }
    }

    private static void Wait(Func<bool> until, int seconds, Action? each = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !until())
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            each?.Invoke();
            Thread.Sleep(80);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Construye las dos pÃ¡ginas densas cuando la navegaciÃ³n las pide.</summary>
    private sealed class Pages : IServiceProvider
    {
        public Func<SessionViewModel>? Session { get; set; }

        public Func<AssistedFixViewModel>? Fix { get; set; }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(SessionViewModel))
            {
                return Session?.Invoke();
            }

            if (serviceType == typeof(AssistedFixViewModel))
            {
                return Fix?.Invoke();
            }

            return null;
        }
    }

    private sealed class AlwaysDiscard : IFixDiscardConfirmer
    {
        public bool Confirm(IReadOnlyList<string> files) => true;
    }
}

