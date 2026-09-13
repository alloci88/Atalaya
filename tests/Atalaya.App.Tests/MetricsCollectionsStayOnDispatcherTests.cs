using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>La agregación de Métricas no escribe una colección observable fuera del despachador.</b>
/// <para>
/// <b>Qué regla protege.</b> D-327 manda que agregar corra FUERA del hilo de interfaz —«el panel
/// no puede congelar la ventana mientras cuenta»—, y <c>LoadAsync</c> lo cumple con un
/// <c>await Task.Run(...)</c>. Lo que la regla NO permite es que el RESULTADO se vuelque desde
/// donde se calculó: las quince colecciones del view-model están enlazadas a la vista, y una
/// <see cref="ObservableCollection{T}"/> no tiene cerrojo. La frontera es exactamente ese
/// <c>await</c>: delante, cuenta quien sea; detrás, escribe el despachador.
/// </para>
/// <para>
/// <b>Qué se rompería en silencio sin este test.</b> Un <c>ConfigureAwait(false)</c> puesto «para
/// no bloquear» en la línea del <c>Task.Run</c>, o un <c>LoadAsync</c> llamado desde un aviso del
/// hub que llega en un hilo del pool, sacan la continuación entera del despachador sin cambiar ni
/// una línea de lo que el panel enseña. Con una sola carga en vuelo no pasa nada visible; con dos
/// —y hay un camino real que las solapa: <c>Reload()</c> hace <c>_ = LoadAsync()</c> sin esperar
/// (<c>MetricsViewModel.cs:249-254</c>)— dos hilos insertan en la misma lista y lo que sale es un
/// <see cref="IndexOutOfRangeException"/> dentro de <c>List{T}.Insert</c>, una vez de cada tantas,
/// en cualquier test del panel. Nadie lo lee como una carrera: se lee como «ese test falla a
/// veces».
/// </para>
/// <para>
/// <b>Lo que este test midió al nacer</b> (BUGFIX-PARPADEO, N-2). Con el despachador puesto, las
/// 17 cargas del ejercicio dejan 858 avisos repartidos por 14 colecciones y <b>los 858 salen del
/// mismo hilo</b>: el producto marshala bien. Pero lo hace SIN una sola línea que lo diga — lo
/// único que devuelve la continuación al despachador es el contexto de sincronización ambiente
/// que captura el <c>await</c> de la línea 602—. El mismo ejercicio corrido sin ese contexto, que
/// es como corre hoy un test de xUnit, deja <b>403 de los 858 avisos repartidos por cinco hilos</b>
/// y la primera escritura de fuera es <c>SyncAppOptions</c> sobre <c>AppOptions</c>. Ahí está la
/// distancia entre «esto funciona» y «esto está protegido», y es la que cubre este test.
/// </para>
/// <para>
/// <b>Por qué NO reusa <see cref="WpfUiThread"/>.</b> Aquel monta además LA
/// <see cref="System.Windows.Application"/> del dominio, y por eso obliga a entrar en
/// <see cref="AppCollection"/> — que corre sola y serializa contra todo lo que haya dentro. Aquí
/// no hace falta ninguna: el view-model de Métricas no resuelve un solo recurso del tema, y los
/// pinceles que reparte salen ya congelados (<c>MetricsViewModel.Brush</c>), así que no tienen
/// afinidad de hilo. Lo único que se necesita es un hilo STA con su <see cref="Dispatcher"/>
/// bombeando y su <see cref="DispatcherSynchronizationContext"/> puesto — que es lo que WPF pone
/// en el hilo de la ventana— y eso cabe en esta clase sin tocar el reparto en paralelo de la
/// suite.
/// </para>
/// </summary>
public sealed class MetricsCollectionsStayOnDispatcherTests : IDisposable
{
    /// <summary>Cuántas veces se repite el gesto que solapa dos cargas.</summary>
    private const int Rounds = 8;

    /// <summary>Lo que <c>ApplyTiles</c> notifica por carga: un <c>Clear</c> y cuatro <c>Add</c>.</summary>
    private const int CardNotificationsPerLoad = 5;

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public MetricsCollectionsStayOnDispatcherTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "atalaya-metricas-despachador", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
    }

    /// <summary>
    /// El panel se carga como lo carga la aplicación —construido en el hilo de interfaz, con el
    /// despachador bombeando— y se le hace el gesto que solapa dos agregaciones: cambiar el
    /// periodo con una carga todavía en vuelo. Ninguna de las colecciones enlazadas puede avisar
    /// de un cambio desde un hilo que no sea el del despachador.
    /// </summary>
    [Fact]
    public void Ninguna_coleccion_del_panel_avisa_de_un_cambio_fuera_del_hilo_del_despachador()
    {
        Seed();

        using var ui = new DispatcherThread();
        var watch = new CollectionWatch(ui.ManagedThreadId);
        IReadOnlyList<string> names = Array.Empty<string>();
        int cardNotifications = 0;

        ui.Run(async () =>
        {
            // 1 · El view-model nace EN el hilo de interfaz, como lo construye el contenedor
            //     cuando el raíl navega a Métricas.
            MetricsViewModel vm = Panel();

            List<(string Name, INotifyCollectionChanged Collection)> collections =
                ObservableCollectionsOf(vm);
            foreach ((string name, INotifyCollectionChanged collection) in collections)
            {
                watch.Watch(name, collection);
            }

            names = collections.Select(c => c.Name).ToList();

            // 2 · EL DETECTOR SE PRUEBA A SÍ MISMO ANTES DE PROBAR NADA. Una notificación
            //     disparada a propósito desde el pool tiene que aparecer en la lista con su hilo;
            //     si no apareciera, el verde de más abajo no distinguiría «el producto escribe
            //     bien» de «este test no está mirando».
            var canario = new ObservableCollection<string>();
            watch.Watch("canario", canario);
            await Task.Run(() => canario.Add("escrito desde el pool"));

            watch.Offenders.Should().ContainSingle(
                "el detector tiene que ver una escritura desde fuera del despachador; si no la ve, "
                + "no puede afirmar nada sobre las del producto")
                .Which.Should().Contain("«canario»");
            watch.ForgetOffenders();

            // 3 · La primera carga: entrar en la página.
            await vm.LoadAsync();

            // 4 · Y el camino que solapa DOS agregaciones. `Reload()` hace `_ = LoadAsync()` sin
            //     esperar, así que cambiar el periodo mientras la anterior sigue en su `Task.Run`
            //     deja dos cargas vivas compitiendo por las mismas colecciones.
            for (int round = 0; round < Rounds; round++)
            {
                Task enVuelo = vm.LoadAsync();
                vm.SelectedRange = vm.RangeOptions.Single(
                    r => r.Range == (round % 2 == 0 ? MetricsRange.Weeks8 : MetricsRange.Weeks4));
                await enVuelo;
            }

            // 5 · La segunda carga de cada vuelta no se puede esperar —nadie guarda su tarea—, así
            //     que se le da tiempo a llegar. Se espera a que se pose el contador, y no a un
            //     número concreto: desde BUGFIX-PARPADEO una carga adelantada por otra más nueva
            //     SE RETIRA sin escribir, así que cuántas escriben de verdad depende de quién gane
            //     cada vuelta. Lo que este test mira no es cuántas, es DESDE DÓNDE.
            int quieto = -1;
            for (int i = 0; i < 400; i++)
            {
                int ahora = watch.CountOf("Cards");
                if (ahora == quieto && ahora >= CardNotificationsPerLoad)
                {
                    break;
                }

                quieto = ahora;
                await Task.Delay(20);
            }

            cardNotifications = watch.CountOf("Cards");
        });

        names.Should().Contain(
            new[] { "AppOptions", "Cards", "Coverage", "SeverityCards", "TopRules", "Sessions" },
            "la suscripción se hace por reflexión: si dejara de encontrar las colecciones del panel "
            + "este test pasaría mirando una lista vacía");

        // EL CONTADOR SIGUE, PERO YA NO CUENTA CARGAS. Aquí se exigían
        // `CardNotificationsPerLoad * (1 + 2 * Rounds)` avisos —una escritura por cada carga
        // lanzada— para que un verde no pudiera salir de un panel que nunca cargó. Desde
        // BUGFIX-PARPADEO esa cuenta es falsa por construcción: la guarda de generación hace que
        // una carga adelantada por otra más nueva **se retire sin escribir**, que es justo lo que
        // se arregló, así que de las 17 cargas escriben las que ganan su vuelta y no todas.
        //
        // Lo que queda es lo que este test sí puede afirmar: que el panel cargó al menos una vez
        // —con cero avisos, la suscripción miraría un panel muerto—. Que el detector no está
        // ciego lo prueba el canario de abajo, que es la guarda de verdad contra el falso verde.
        cardNotifications.Should().BeGreaterThanOrEqualTo(
            CardNotificationsPerLoad,
            "sin un solo aviso en «Cards» el panel no llegó a cargar, y este test estaría mirando "
            + "quince colecciones que nadie ha tocado");

        string offenders = string.Join(
            Environment.NewLine + Environment.NewLine, watch.Offenders);

        offenders.Should().BeEmpty(
            "la agregación corre fuera del hilo de UI a propósito (D-327), pero su RESULTADO se "
            + "escribe en colecciones enlazadas: volcarlo desde el hilo que contó hace que dos "
            + "cargas solapadas inserten a la vez en la misma lista");
    }

    // ============================================ El hilo de interfaz

    /// <summary>
    /// Un hilo STA con su <see cref="Dispatcher"/> bombeando y el mismo contexto de sincronización
    /// que WPF pone en el hilo de la ventana. Sin ese contexto, la continuación de un
    /// <c>await</c> no vuelve al despachador — y entonces el test mediría el arnés, no el producto.
    /// </summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher _dispatcher = null!;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();

            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(_dispatcher));
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "atalaya-ui-metricas",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }

        public int ManagedThreadId => _thread.ManagedThreadId;

        /// <summary>
        /// Corre trabajo asíncrono en el hilo de interfaz. El hilo del test espera en un evento
        /// —no en el despachador— para que el despachador siga bombeando las continuaciones.
        /// </summary>
        public void Run(Func<Task> body)
        {
            Exception? failure = null;
            using var done = new ManualResetEventSlim();

            _dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait();

            if (failure is not null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }

        public void Dispose() => _dispatcher.InvokeShutdown();
    }

    // ============================================ El detector

    /// <summary>
    /// Apunta de qué hilo llega cada <see cref="INotifyCollectionChanged.CollectionChanged"/> y
    /// guarda, de los que no son del despachador, la colección, el hilo y la pila desde la que se
    /// escribió. La pila es lo que convierte un rojo en un arreglo: dice qué método del view-model
    /// volcó el resultado.
    /// </summary>
    private sealed class CollectionWatch(int dispatcherThreadId)
    {
        private readonly object _gate = new();
        private readonly List<string> _offenders = [];
        private readonly Dictionary<string, int> _counts = [];

        public IReadOnlyList<string> Offenders
        {
            get
            {
                lock (_gate)
                {
                    return _offenders.ToList();
                }
            }
        }

        public void Watch(string name, INotifyCollectionChanged collection)
            => collection.CollectionChanged += (_, e) => Record(name, e);

        public int CountOf(string name)
        {
            lock (_gate)
            {
                return _counts.TryGetValue(name, out int n) ? n : 0;
            }
        }

        public void ForgetOffenders()
        {
            lock (_gate)
            {
                _offenders.Clear();
            }
        }

        private void Record(string name, NotifyCollectionChangedEventArgs e)
        {
            Thread current = Thread.CurrentThread;
            int id = current.ManagedThreadId;

            lock (_gate)
            {
                _counts[name] = (_counts.TryGetValue(name, out int n) ? n : 0) + 1;

                if (id == dispatcherThreadId)
                {
                    return;
                }

                _offenders.Add(
                    $"«{name}» avisó de un {e.Action} desde el hilo {id} "
                    + $"(«{current.Name ?? "sin nombre"}», del pool: {current.IsThreadPoolThread}); "
                    + $"el despachador es el hilo {dispatcherThreadId}."
                    + Environment.NewLine
                    + Stack());
            }
        }

        /// <summary>La pila, recortada: lo que interesa son los marcos del view-model.</summary>
        private static string Stack()
            => string.Join(
                Environment.NewLine,
                new StackTrace(fNeedFileInfo: true)
                    .ToString()
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Take(14));
    }

    // ============================================ Utilidades

    /// <summary>
    /// Todas las colecciones observables del view-model, por reflexión y no por una lista escrita
    /// a mano: la colección número dieciséis que alguien añada mañana entra en la vigilancia sin
    /// que nadie se acuerde de este fichero.
    /// </summary>
    private static List<(string Name, INotifyCollectionChanged Collection)> ObservableCollectionsOf(
        object viewModel)
        => viewModel.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                        && typeof(INotifyCollectionChanged).IsAssignableFrom(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => (p.Name, Collection: p.GetValue(viewModel) as INotifyCollectionChanged))
            .Where(x => x.Collection is not null)
            .Select(x => (x.Name, x.Collection!))
            .ToList();

    private MetricsViewModel Panel()
        => TestFactory.Metrics(
            _hub,
            _paths,
            _settings,
            TestFactory.NavigationWith(TestFactory.Reports(_hub)),
            new ToastCenter());

    /// <summary>
    /// Bastante hub para que TODAS las colecciones del panel tengan algo que escribir: dos apps
    /// —para que haya leyenda y varias filas—, inventario de ciclo, sesiones con coste, hallazgos
    /// vivos de tres gravedades y uno resuelto.
    /// </summary>
    private void Seed()
    {
        foreach ((string slug, string name) in new[] { ("app", "App"), ("otra", "Otra") })
        {
            _hub.Store.WriteApp(new AppConfig
            {
                Slug = slug,
                Name = name,
                RepoUrl = "u",
                CurrentCycle = 1,
            });

            WriteInventory(slug, audited: 4, pending: 6, large: 2);
            WriteSession(slug, cost: 120m, daysAgo: 3);
            WriteSession(slug, cost: 40m, daysAgo: 20);
            WriteFinding(slug, DateTimeOffset.UtcNow.AddDays(-2), Severity.Critica, "criterio.a");
            WriteFinding(slug, DateTimeOffset.UtcNow.AddDays(-30), Severity.Alta, "criterio.b");
            WriteFinding(slug, DateTimeOffset.UtcNow.AddDays(-90), Severity.Baja, "criterio.c");
            WriteResolved(slug, daysAgo: 1);
        }
    }

    private void WriteSession(string slug, decimal? cost, int daysAgo)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            CycleN = 1,
        };
        session.Units.Add(new UnitVerdictRecord("src/A.cs", "src", "auditada", null));

        TestRates.CostAsCredits(session, cost, inputTokens: 100);
        _hub.Store.WriteSession(session);
    }

    private void WriteFinding(string slug, DateTimeOffset detected, Severity severity, string rule)
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        _hub.Store.WriteFinding(slug, new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = rule,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = severity,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "t",
            Locations = { new Location("a.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });
    }

    private void WriteInventory(string slug, int audited, int pending, int large)
    {
        var inv = new InventoryCycle { CycleN = 1 };
        for (int i = 0; i < audited; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"a{i}.cs", Module = "m", State = UnitState.Auditada });
        }

        for (int i = 0; i < pending; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"p{i}.cs", Module = "m", State = UnitState.Pendiente });
        }

        for (int i = 0; i < large; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"g{i}.cs", Module = "m", State = UnitState.Grande });
        }

        _hub.Store.WriteInventory(slug, inv);
    }

    private void WriteResolved(string slug, int daysAgo)
    {
        DateTimeOffset when = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        var stamp = new DetectionStamp(when.AddDays(-30), AuditMode.Lotes, "abc", "alvaro");
        var finding = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "criterio.a",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "t",
            Locations = { new Location("a.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };

        finding.Resolve(new ResolutionStamp(when, ResolutionVia.Auditor, AuditMode.Lotes, "c", "alvaro", "ok"));
        _hub.Store.WriteFinding(slug, finding);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // El árbol temporal puede quedar tomado; no es parte de lo probado.
        }
    }
}
