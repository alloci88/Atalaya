using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atalaya.Agents;
using Atalaya.App;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Wpf.Ui.Markup;

namespace Atalaya.Shots;

/// <summary>
/// Banco de capturas de las vistas densas (F26 Parte B).
///
/// Sesión en vivo y Arreglo asistido no se pueden fotografiar desde la aplicación instalada: hacen
/// falta una sesión de varias unidades y un arreglo con su conversación, y lanzarlos de verdad
/// gasta los créditos del usuario y escribe en el hub del equipo. Esto monta la MISMA carcasa
/// —MainWindow con su MainViewModel— sobre un hub temporal y el agente falso que ya usa la suite,
/// y la fotografía.
///
/// Lo que se ve aquí es la vista de verdad con datos de mentira. Lo que el agente falso NO produce
/// se dice en el parte; no se dibuja a mano.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Merge(app, "Dark");

        foreach (string theme in new[] { "dark", "light" })
        {
            Swap(app, theme);
            foreach (var (w, h, tag) in new[] { (1920d, 1032d, "completa"), (1280d, 720d, "1280") })
            {
                // Una combinación que falle no puede llevarse las otras tres por delante, y sobre
                // todo no puede irse en silencio: la primera versión de esto salía con código 0
                // habiendo hecho dos de ocho capturas.
                try
                {
                    Shoot(app, outDir, theme, tag, w, h);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  !! {theme}-{tag}: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }

            // Los dialogos, una vez por TEMA. Los cinco llevan ancho fijo y NoResize (o alto fijo),
            // asi que no cambian con el tamano de la ventana: fotografiarlos cuatro veces daria
            // cuatro ficheros identicos y haria creer que se ha mirado algo que no varia.
            try
            {
                ShootDialogs(outDir, theme);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  !! dialogos-{theme}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        app.Shutdown();
    }

    // ================================================================ los recursos de la app

    private static void Merge(Application app, string theme)
    {
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationThemeFor(theme) });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        foreach (string name in new[] { "Converters", "Tokens", "Palette.Dark", "Styles", "Pages" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Atalaya;component/Themes/{name}.xaml", UriKind.Absolute),
            });
        }
    }

    private static Wpf.Ui.Appearance.ApplicationTheme ApplicationThemeFor(string theme)
        => theme.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? Wpf.Ui.Appearance.ApplicationTheme.Light
            : Wpf.Ui.Appearance.ApplicationTheme.Dark;

    /// <summary>El mismo cambio de paleta que hace la aplicación, por el mismo camino.</summary>
    private static void Swap(Application app, string theme) => ThemeService.Apply(theme);

    // ================================================================ la foto

    private static void Shoot(Application app, string outDir, string theme, string tag, double w, double h)
    {
        var fixture = Fixture.Build();

        var window = new MainWindow(fixture.Shell)
        {
            Width = w,
            Height = h,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,          // fuera de pantalla: se mide y se pinta, pero no molesta
            Top = -32000,
            ShowActivated = false,
        };

        // La carcasa arranca maximizada la primera vez (D-956) y el hub del banco es siempre nuevo.
        // Aquí se quiere un tamaño EXACTO, así que se deshace: es la ventana la que se mide, no la
        // pantalla de esta máquina.
        window.WindowState = WindowState.Normal;
        window.Width = w;
        window.Height = h;
        window.Show();
        Pump();

        // 1) La sesión EN VIVO: se lanza y se fotografía a mitad, con unas unidades hechas, una
        //    corriendo y las demás pendientes. Es el estado que hay que poder mirar; la pantalla de
        //    «sesión terminada» es otra cosa y ya se veía.
        fixture.StartSession();
        fixture.WaitUntilUnit(3);
        fixture.Shell.ShowSessionCommand.Execute(null);
        Pump(12);
        Save(window, Path.Combine(outDir, $"{theme}-{tag}-05-sesion-en-vivo.png"));
        fixture.WaitForSession();

        // 2) El arreglo asistido, en su minuto útil: el agente ya autorizado y el fichero tocado,
        //    con la conversación viva a la izquierda y el diff lleno a la derecha. La primera
        //    versión de esto no contestaba al permiso, así que la foto salía con la tarjeta de
        //    autorización esperando y el panel diciendo que no se había tocado nada — la vista era
        //    real, pero era el minuto equivocado. El banco autoriza como lo haría el usuario.
        fixture.RunFix();
        fixture.Shell.ShowFixCommand.Execute(null);
        Pump(4);
        fixture.WaitForDiff();
        Pump(8);
        Save(window, Path.Combine(outDir, $"{theme}-{tag}-06-arreglo-asistido.png"));

        // 3) Y la PANTALLA DE CIERRE del arreglo, que es donde vive el aviso de «sin commitear».
        //    Se contesta también a la decisión para que el agente llegue al final.
        fixture.WaitForFix();
        Pump(12);
        Save(window, Path.Combine(outDir, $"{theme}-{tag}-07-arreglo-cierre.png"));

        // 4) Y «Última sesión»: la misma vista de la sesión, ya terminada, con su tarjeta de
        //    recuentos. Es otra pantalla, no la misma con menos cosas.
        fixture.Shell.ShowSessionCommand.Execute(null);
        Pump(16);
        Save(window, Path.Combine(outDir, $"{theme}-{tag}-08-ultima-sesion.png"));

        // NO se cierra: sin `Application.Run` no hay bucle de mensajes propio, y cerrar la última
        // ventana apaga el despachador — el banco salía con código 0 habiendo hecho dos capturas de
        // ocho, en silencio. Se esconde, y el proceso se lleva las ventanas al terminar.
        window.Hide();
        Pump();
        fixture.Dispose();
    }

    private static void ShootDialogs(string outDir, string theme)
    {
        var fixture = Fixture.Build();
        foreach ((string name, Window window) in Dialogs.Build(fixture))
        {
            // Fuera de pantalla, como la carcasa: se mide y se pinta, pero no roba el foco.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.Show();
            Pump(10);
            Save(window, Path.Combine(outDir, $"{theme}-{name}.png"));
            window.Hide();
            Pump(2);
        }

        fixture.Dispose();
    }

    /// <summary>Deja correr la cola del despachador: sin esto la ventana se fotografía a medio pintar.</summary>
    private static void Pump(int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void Save(Window window, string path)
    {
        var source = PresentationSource.FromVisual(window);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;

        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpiX),
            (int)Math.Ceiling(window.ActualHeight * dpiY),
            96 * dpiX, 96 * dpiY, PixelFormats.Pbgra32);
        bmp.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine($"  -> {Path.GetFileName(path)}");
    }
}
