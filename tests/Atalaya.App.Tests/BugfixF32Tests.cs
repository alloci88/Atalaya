using System.Windows.Controls;
using System.Windows.Threading;
using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>BUGFIX-F32 — el botón que cerraba la aplicación.</b>
/// <para>
/// <b>El parte, con la pila delante.</b> Con el <c>dist</c> de F32, pulsar «Me quedo los cambios»
/// cerraba Atalaya: sin diálogo, sin toast y <b>sin una línea en el registro</b>. El evento 1026 de
/// .NET Runtime del Visor de sucesos lo dijo entero:
/// <c>System.InvalidOperationException: El subproceso que realiza la llamada no puede obtener
/// acceso a este objeto porque el propietario es otro subproceso</c>, en
/// <c>AsyncRelayCommand.NotifyCanExecuteChanged</c> ← <c>LiveFixService.AcceptChanges</c> ←
/// <c>CommitChanges</c> ← el <c>Task.Run</c> del comando.
/// </para>
/// <para>
/// <b>Y por qué los siete tests de F32 estaban en verde.</b> Llamaban a
/// <c>fix.CommitChanges()</c> <b>en el hilo del test</b> y nunca por el comando; y sobre todo, en
/// un test <b>no hay ningún <c>Button</c> enlazado</b>, así que <c>NotifyCanExecuteChanged</c> no
/// cruzaba a nadie y no podía fallar. Lo que faltaba probar no era el commit: era el camino.
/// </para>
/// </summary>
public sealed class BugfixF32Tests
{
    // ============================================================ (1) la excepción que lo cerraba

    /// <summary>
    /// <b>El aviso del servicio cruza de hilo sin reventar, con un <c>Button</c> enlazado de
    /// verdad.</b>
    /// <para>
    /// Es la reproducción exacta: un botón suscrito a <c>CanExecuteChanged</c> —que es lo que hace
    /// WPF al enlazar un <c>Command</c>— y el aviso levantado desde un hilo de fondo, como lo
    /// levanta el commit. Cebo comprobado: sin la guarda del view-model, esto lanza
    /// <c>InvalidOperationException</c> con el mismo texto del evento 1026.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_aviso_del_servicio_desde_un_hilo_de_fondo_no_tumba_la_ventana()
    {
        ViewLayout.OnUiThread(() =>
        {
            using var fix = new FixServiceProbe();
            var vm = new AssistedFixViewModel(fix.Service, new ToastCenter(), new NoDiscard());

            // Lo que hace WPF al enlazar `Command`: suscribirse a `CanExecuteChanged`. Sin esto el
            // aviso no cruza a nadie y el defecto no se puede reproducir.
            var button = new Button { Command = vm.CommitChangesCommand };
            button.Command.Should().BeSameAs(vm.CommitChangesCommand);

            Exception? escaped = null;
            Task.Run(() =>
            {
                try
                {
                    // El mismo cruce que hacia `AcceptChanges()` dentro del `Task.Run` del
                    // comando: el servicio avisa desde un hilo de fondo y el view-model, al
                    // atenderlo, toca el comando enlazado.
                    fix.Service.StatusMessage = "commiteando desde un hilo de fondo";
                }
                catch (Exception ex)
                {
                    escaped = ex;
                }
            }).GetAwaiter().GetResult();

            escaped.Should().BeNull(
                "un aviso desde un hilo de fondo no puede llevarse por delante la aplicación");

            // Y la cola del dispatcher se vacía sin lanzar: el aviso llegó, solo que por su hilo.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        });
    }

    // ============================================================ (2) el manejador que no existía

    /// <summary>
    /// <b>Una excepción no capturada en el hilo de interfaz deja de cerrar la aplicación</b>, y
    /// deja su pila escrita.
    /// <para>
    /// Es el <b>segundo</b> defecto, independiente del primero: D-802 puso un <c>try</c> alrededor
    /// del arranque y nadie puso nada para después, así que cualquier excepción no capturada
    /// cerraba Atalaya en silencio. Arreglar la de F32 no habría arreglado esto — la siguiente
    /// habría hecho lo mismo—.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_excepcion_no_capturada_en_la_interfaz_se_apunta_y_la_aplicacion_sigue()
    {
        ViewLayout.OnUiThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var apuntado = new List<string>();
            var dicho = new List<string>();

            using (UnhandledErrors.Install(dispatcher, apuntado.Add, dicho.Add))
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("el hilo no es el suyo")));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            // Se apuntó, con tipo, mensaje y pila — que es lo que faltaba en el registro.
            apuntado.Should().ContainSingle();
            apuntado[0].Should().Contain("hilo de interfaz")
                .And.Contain("System.InvalidOperationException")
                .And.Contain("el hilo no es el suyo")
                .And.Contain("BugfixF32Tests", "la pila, que es lo que se fue al Visor de sucesos");

            // Y se dijo, en vez de cerrarse sin más.
            dicho.Should().ContainSingle().Which.Should().Be(UnhandledErrors.UserMessage);
        });
    }

    /// <summary>Sin manejador, la misma excepción se va por donde se iba: nadie la apunta.</summary>
    [Fact]
    public void Sin_el_manejador_no_queda_ni_una_linea_que_leer()
    {
        ViewLayout.OnUiThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var apuntado = new List<string>();
            bool llego = false;

            // El cebo del test de arriba, escrito: se engancha y se suelta ANTES de lanzar.
            UnhandledErrors.Install(dispatcher, apuntado.Add).Dispose();
            dispatcher.UnhandledException += (_, e) =>
            {
                llego = true;
                e.Handled = true;   // Si no, este test sí tumbaría la tanda.
            };

            dispatcher.BeginInvoke(
                DispatcherPriority.Normal, new Action(() => throw new InvalidOperationException("x")));
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            llego.Should().BeTrue("la excepción llegó al dispatcher");
            apuntado.Should().BeEmpty("y sin manejador nadie la apunta: eso es morir en silencio");
        });
    }

    /// <summary>El parte lleva las capas: una excepción envuelta se lee entera, no solo la de fuera.</summary>
    [Fact]
    public void El_parte_de_una_excepcion_lleva_el_tipo_el_mensaje_y_la_pila_de_cada_capa()
    {
        Exception dentro;
        try
        {
            throw new InvalidOperationException("el de dentro");
        }
        catch (Exception ex)
        {
            dentro = ex;
        }

        string parte = UnhandledErrors.Describe(
            new ApplicationException("el de fuera", dentro), "tarea sin observar");

        parte.Should().Contain("tarea sin observar")
            .And.Contain("System.ApplicationException: el de fuera")
            .And.Contain("System.InvalidOperationException: el de dentro")
            .And.Contain("BugfixF32Tests");
    }

    // ---------------------------------------------------------------- ayudas

    /// <summary>
    /// Un servicio de arreglo de mentira con lo justo: poder levantar <c>Changed</c> desde donde
    /// haga falta. No se monta una sesión entera porque lo que se prueba aquí es el HILO.
    /// </summary>
    private sealed class FixServiceProbe : IDisposable
    {
        private readonly string _root;

        public FixServiceProbe()
        {
            _root = Path.Combine(Path.GetTempPath(), "atalaya-f32-hilo", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(_root, "local"));
            var settings = new SettingsService(paths);
            settings.Load();
            HubContext hub = TestFactory.Hub(paths, settings);
            hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
            var machines = new MachineConfigStore(paths.MachinesJson);
            var busy = new AgentBusyGate();

            Service = new LiveFixService(
                hub, () => null, machines, new UlidFactory(SystemClock.Instance), settings,
                new ReferenceCollector(), new FixSnapshotStore(paths),
                new AssistedFixLauncher(settings, new CloneLinkService(hub, machines), machines, busy),
                busy);
        }

        public LiveFixService Service { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    // ============================================================ (3) un bucle avisa UNA vez

    /// <summary>
    /// <b>Un bucle no tapa la pantalla</b> (BUGFIX-F36-1).
    /// <para>
    /// <b>De dónde viene, medido.</b> Un estilo puesto sobre un tipo que no era el suyo reventaba
    /// al COLOCAR, y colocar se reintenta en cada pasada de render: <b>23 excepciones en 460 ms</b>.
    /// Cada una abría su <c>MessageBox</c>, y un modal <b>bombea mensajes</b> — dentro de su bucle
    /// corría otra pasada de layout, que volvía a lanzar, que abría otro modal <b>sobre la misma
    /// pila</b>—. Veintitrés bucles modales anidados agotaron la pila del hilo de interfaz:
    /// <c>0xC00000FD</c> en el Visor de sucesos y la aplicación cerrada. <b>Ese final no lo puede
    /// contener ningún manejador gestionado</b>: cuando la pila se acaba, el CLR ni siquiera intenta
    /// llamar a nadie.
    /// </para>
    /// <para>
    /// <b>La regla que queda</b>: misma pila dentro de la ventana, un solo aviso. Lo que se agrupa
    /// es el AVISO —lo que tapa la pantalla y lo que anida bucles modales—; el registro se queda la
    /// primera entera y una línea corta con el número de repetición, así que no se pierde ni el
    /// diagnóstico ni la cuenta.
    /// </para>
    /// </summary>
    [Fact]
    public void Veintitres_excepciones_iguales_seguidas_avisan_una_sola_vez()
    {
        ViewLayout.OnUiThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var apuntado = new List<string>();
            var dicho = new List<string>();
            var reloj = new DateTimeOffset(2026, 9, 8, 11, 5, 15, TimeSpan.Zero);

            using (UnhandledErrors.Install(dispatcher, apuntado.Add, dicho.Add, () => reloj))
            {
                // La misma pila 23 veces en menos de medio segundo: el bucle de layout del `dist`.
                for (int i = 0; i < 23; i++)
                {
                    reloj = reloj.AddMilliseconds(20);
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reventar));
                    dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
            }

            // UN aviso. Veintitrés modales encima del mismo hilo son lo que agotó la pila.
            dicho.Should().ContainSingle("un bucle avisa una vez, no una por pasada de render");

            // Y el registro se queda la primera ENTERA —con su pila, que es lo que se diagnostica—
            // y una línea corta por cada repetición, numerada.
            apuntado.Should().HaveCount(23);
            apuntado[0].Should().Contain("hilo de interfaz").And.Contain("BugfixF32Tests");
            apuntado[1].Should().StartWith("Excepción repetida").And.Contain("nº 1")
                .And.NotContain("   at ", "una pila repetida 22 veces es un muro, no un registro");
            apuntado[22].Should().Contain("nº 22");
        });
    }

    /// <summary>
    /// <b>Pasada la ventana, se vuelve a avisar — y el aviso lleva la cuenta de lo que se calló.</b>
    /// Un error que sigue pasando diez minutos después no es el mismo suceso, y callarlo para
    /// siempre sería cambiar un defecto por otro: la pantalla tapada por un silencio.
    /// </summary>
    [Fact]
    public void Pasada_la_ventana_se_vuelve_a_avisar_y_el_aviso_dice_cuantas_se_callaron()
    {
        ViewLayout.OnUiThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var dicho = new List<string>();
            var reloj = new DateTimeOffset(2026, 9, 8, 11, 5, 15, TimeSpan.Zero);

            using (UnhandledErrors.Install(dispatcher, _ => { }, dicho.Add, () => reloj))
            {
                for (int i = 0; i < 3; i++)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reventar));
                    dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }

                reloj = reloj.Add(UnhandledErrors.RepeatWindow).AddSeconds(1);
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reventar));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            dicho.Should().HaveCount(2);
            dicho[0].Should().Be(UnhandledErrors.UserMessage, "la primera no se ha callado nada");
            dicho[1].Should().StartWith(UnhandledErrors.UserMessage)
                .And.Contain("2 repeticiones más", "las dos que se callaron entre medias, contadas");
        });
    }

    /// <summary>
    /// <b>Dos errores DISTINTOS avisan los dos</b>, aunque lleguen seguidos. Lo que se agrupa es la
    /// repetición de uno, no el hecho de que haya varios: agrupar por tiempo y no por pila taparía
    /// el segundo problema con el primero.
    /// </summary>
    [Fact]
    public void Dos_excepciones_distintas_seguidas_avisan_las_dos()
    {
        ViewLayout.OnUiThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var dicho = new List<string>();
            var reloj = new DateTimeOffset(2026, 9, 8, 11, 5, 15, TimeSpan.Zero);

            using (UnhandledErrors.Install(dispatcher, _ => { }, dicho.Add, () => reloj))
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reventar));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("otra cosa")));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            dicho.Should().HaveCount(2);
        });
    }

    /// <summary>El cebo de los tres de arriba: siempre la MISMA pila y el mismo mensaje.</summary>
    private static void Reventar()
        => throw new InvalidOperationException(
            "El TargetType \"PathText\" no coincide con el tipo de elemento \"TextBlock\".");

    private sealed class NoDiscard : IFixDiscardConfirmer
    {
        public bool Confirm(IReadOnlyList<string> files) => false;
    }
}
