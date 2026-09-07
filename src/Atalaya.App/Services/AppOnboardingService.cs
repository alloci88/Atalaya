using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;

namespace Atalaya.App.Services;

/// <summary>Lo que el alta necesita saber para hacer su trabajo. Sale del formulario, entero.</summary>
/// <param name="Importing">
/// Hay un baseline del sistema v4 dentro del clon y se va a traer. Es un paso <b>de más</b>, y por
/// eso el plan depende de él: enseñar un paso que no se va a ejecutar es la otra forma de mentir.
/// </param>
public sealed record OnboardingRequest(
    string Slug,
    string Name,
    string RepoUrl,
    string ClonePath,
    TechStack Stack,
    bool Importing = false,
    string CodeAuditPath = "");

/// <summary>Lo que deja la primera mitad del alta, para que la segunda no vuelva a leer nada.</summary>
/// <param name="Previous">
/// El inventario que ya hubiera en el hub para ese ciclo: lo trae el baseline v4 importado, y es
/// contra lo que se reconcilia el escaneo para no perder lo que el sistema anterior daba por
/// auditado.
/// </param>
public sealed record OnboardingScan(
    AppConfig App,
    ScanOutput Scan,
    InventoryCycle? Previous,
    IReadOnlyList<string> ImportLog);

/// <summary>
/// <b>El alta de una aplicación, paso a paso</b> (F30 §4).
/// <para>
/// <b>Por qué sale del view-model.</b> Los pasos que se enseñan tienen que ser los que se ejecutan,
/// y eso solo se puede probar si hay un servicio al que preguntárselos. Mientras el alta vivía
/// dentro de <c>OnboardingViewModel.Create</c> —dos <c>Task.Run</c> con el diálogo del ciclo en
/// medio— no había ninguna lista que cuadrar con ninguna otra: había un botón que se apagaba.
/// </para>
/// <para>
/// <b>Y por qué son dos mitades.</b> Entre el escaneo y el inventario va la lupa del ciclo 1
/// (F17 §4), que es un diálogo y vive en el hilo de interfaz. El corte ya existía; lo que cambia es
/// que ahora las dos mitades ejecutan pasos de <b>la misma</b> lista, en orden.
/// </para>
/// </summary>
public sealed class AppOnboardingService
{
    // Los identificadores, en un sitio: los usa el servicio para ejecutar y el test para cuadrar.
    public const string Importar = "importar";
    public const string Escanear = "escanear";
    public const string Registrar = "registrar";
    public const string Inventario = "inventario";
    public const string Medidas = "medidas";
    public const string Publicar = "publicar";

    private readonly HubContext _hub;
    private readonly InventoryScanner _scanner;
    private readonly MachineConfigStore _machines;
    private readonly ImportService _import;
    private readonly MeasuredFindingService _measured;

    public AppOnboardingService(
        HubContext hub,
        InventoryScanner scanner,
        MachineConfigStore machines,
        ImportService import,
        MeasuredFindingService measured)
    {
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _import = import;
        _measured = measured;
    }

    /// <summary>
    /// <b>Los pasos del alta</b>, que son los que se enseñan y los que se ejecutan.
    /// <para>
    /// Solo el primero se puede cancelar: leer el clon no escribe nada. En cuanto la aplicación
    /// queda registrada, cancelar dejaría media alta puesta.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StepSpec> Plan(bool importing)
    {
        var plan = new List<StepSpec>();
        if (importing)
        {
            plan.Add(new StepSpec(Importar, "Importar el baseline del sistema v4", Cancelable: true));
        }

        plan.Add(new StepSpec(Escanear, "Escanear el código del clon", Cancelable: true));
        plan.Add(new StepSpec(Registrar, "Registrar la aplicación"));
        plan.Add(new StepSpec(Inventario, "Escribir el inventario del ciclo"));
        plan.Add(new StepSpec(Medidas, "Reconciliar las unidades grandes"));
        plan.Add(new StepSpec(Publicar, "Publicar en el hub"));
        return plan;
    }

    /// <summary>La lista lista para colgarla de la vista.</summary>
    public static StepList NewSteps(bool importing, StepFlow flow = StepFlow.Vertical)
        => new(Plan(importing), flow);

    /// <summary>
    /// La primera mitad: el baseline si lo hay, el escaneo y el registro. Después de esto la
    /// aplicación ya existe en el clon del hub, y el ciclo 1 puede preguntar por su lupa.
    /// </summary>
    public OnboardingScan Prepare(OnboardingRequest request, StepList steps)
    {
        // 1) El baseline v4, ANTES del escaneo. Trae su propio app.json —con el ciclo en el que se
        //    quedó el sistema anterior— y su inventario con el estado auditado de cada unidad.
        //    Importar DESPUÉS lo pisaría con lo que acabamos de escanear, y el alta perdería justo
        //    lo que venía a rescatar.
        IReadOnlyList<string> log = request.Importing
            ? steps.Run(Importar, () => _import.Import(
                request.Slug, request.Name, request.RepoUrl, request.CodeAuditPath, push: false))
            : Array.Empty<string>();

        // 2) El escaneo, con la política que la app estrena: la de fábrica, o la que traiga su
        //    app.json si el baseline ya la dejó en el hub (F13).
        (AppConfig app, ScanOutput scan) = steps.Run(Escanear, () =>
        {
            AppConfig config = _hub.Store.TryReadApp(request.Slug) ?? new AppConfig
            {
                Slug = request.Slug,
                Name = request.Name,
                RepoUrl = request.RepoUrl,
                Stack = request.Stack,
                CurrentCycle = 1,
            };
            config.Name = request.Name;
            config.RepoUrl = request.RepoUrl;
            config.Stack = request.Stack;
            config.CurrentCycle = Math.Max(1, config.CurrentCycle);

            ScanOutput output = _scanner.Scan(request.ClonePath, config, config.CurrentCycle);
            config.Stack = output.Stack;
            return (config, output);
        });

        // 3) Y queda registrada: su ficha en el hub, y dónde está su clon EN ESTA MÁQUINA — que no
        //    viaja, pero sin ello la app nace sin poder auditarse desde el puesto que la dio de alta.
        steps.Run(Registrar, () =>
        {
            _hub.Store.WriteApp(app);
            _machines.SetClonePath(request.Slug, request.ClonePath);
        });

        return new OnboardingScan(app, scan, _hub.Store.TryReadInventory(request.Slug, app.CurrentCycle), log);
    }

    /// <summary>
    /// La segunda mitad, ya con la lupa elegida: el inventario del ciclo, los hallazgos que la
    /// aplicación mide, y la publicación.
    /// </summary>
    /// <returns><c>false</c> si el hub no aceptó la publicación: el alta queda en local.</returns>
    public bool Finish(
        OnboardingRequest request, OnboardingScan prepared, CycleConfig config, StepList steps)
    {
        InventoryCycle written = steps.Run(Inventario, () =>
        {
            // El inventario del ciclo vigente sale del CÓDIGO que hay en el clon, pero arrastrando
            // el estado de lo que el baseline daba por auditado: es la misma reconciliación del
            // re-escaneo (D-302), no una segunda escrita aparte.
            InventoryCycle inventory = prepared.Previous is null
                ? prepared.Scan.Inventory
                : Rescanner.Reconcile(prepared.Previous, prepared.Scan.Inventory).Merged;
            inventory.Config = config;
            inventory.OpenedUtc ??= DateTimeOffset.UtcNow;
            inventory.OpenThemeHistory(config.Theme, inventory.OpenedUtc, _hub.ResolveIdentity().Name);
            _hub.Store.WriteInventory(request.Slug, inventory);
            return inventory;
        });

        // Los hallazgos de «unidad demasiado grande» los pone al día el MISMO servicio que los
        // mantiene después (F5.16): dos caminos para el mismo hecho acaban discrepando.
        steps.Run(Medidas, () => _measured.Reconcile(request.Slug, written, request.ClonePath));

        return steps.Run(Publicar, () => _hub.Sync?.CommitAndPush(request.Importing
            ? $"app: onboard {request.Slug} ({prepared.Scan.Stack}) + import v4"
            : $"app: onboard {request.Slug} ({prepared.Scan.Stack})") ?? true);
    }
}
