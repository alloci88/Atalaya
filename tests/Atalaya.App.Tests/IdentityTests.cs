using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Resources;
using System.Reflection;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.4 — la identidad visual: el icono de la aplicación y la marca corporativa.
/// <para>
/// Casi todo lo de aquí protege una CONTENCIÓN. El icono tiene que estar en los sitios donde
/// Windows lo enseña y en ninguno más; el logotipo corporativo tiene que estar en tres sitios y
/// en ninguno más; y el logotipo que se despliega tiene que ser, byte a byte, el que entregó
/// comunicación. Lo que un test no vigila aquí es lo que acaba convirtiéndose en papel pintado.
/// </para>
/// </summary>
public sealed class IdentityTests : IDisposable
{
    private readonly string _root;

    public IdentityTests()
        => _root = Path.Combine(Path.GetTempPath(), "atalaya-f64", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un fichero bloqueado no puede tumbar la suite.
        }
    }

    // =============================================================== §1 · el icono

    /// <summary>
    /// El .ico lleva los seis tamaños. No es una lista de deseos: cada uno lo pide un sitio
    /// distinto de Windows —16 la barra de título, 32 el Alt-Tab, 48 el Explorador, 256 la vista
    /// de iconos grandes— y el que falte lo resuelve el sistema escalando el más cercano, que es
    /// exactamente el borrón que este fichero existe para evitar.
    /// </summary>
    [Fact]
    public void El_ico_trae_los_seis_tamanos_y_todos_con_imagen_dentro()
    {
        byte[] ico = File.ReadAllBytes(Asset("atalaya.ico"));

        BitConverter.ToUInt16(ico, 0).Should().Be(0, "campo reservado");
        BitConverter.ToUInt16(ico, 2).Should().Be(1, "tipo 1 = icono");
        int count = BitConverter.ToUInt16(ico, 4);
        count.Should().Be(6);

        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (16 * i);
            int width = ico[entry] == 0 ? 256 : ico[entry];   // 0 significa 256
            int bytes = BitConverter.ToInt32(ico, entry + 8);
            int offset = BitConverter.ToInt32(ico, entry + 12);

            bytes.Should().BeGreaterThan(0, $"el tamaño {width} tiene que traer imagen");
            (offset + bytes).Should().BeLessThanOrEqualTo(ico.Length, "la entrada apunta dentro del fichero");
            BitConverter.ToUInt16(ico, entry + 6).Should()
                .Be(32, "32 bits por píxel: el icono lleva canal alfa");
            sizes.Add(width);
        }

        sizes.Should().Equal(16, 24, 32, 48, 64, 256);
    }

    /// <summary>
    /// A 16 px no cabe el icono grande: cabe su silueta. Se comprueba que la variante pequeña
    /// haya soltado de verdad lo que no sobrevive al remuestreo —halo, degradado, tronera— y que
    /// la grande siga teniéndolo. Si alguien «unificara» los dos SVG, esto lo dice.
    /// </summary>
    [Fact]
    public void El_icono_pequeno_es_una_silueta_y_el_grande_no()
    {
        string big = File.ReadAllText(Asset("atalaya-icon.svg"));
        string small = File.ReadAllText(Asset("atalaya-icon-small.svg"));

        big.Should().Contain("<radialGradient", "el grande sí lleva el halo de la luz");
        big.Should().Contain("<linearGradient");

        // Se mira el ELEMENTO, no la palabra: los comentarios del propio SVG explican por qué no
        // están, y un test que se dejara engañar por su propia explicación no probaría nada.
        small.Should().NotContain("<defs>", "la variante pequeña no necesita definir nada");
        small.Should().NotContain("<radialGradient", "un halo a 16 px es suciedad alrededor de la torre");
        small.Should().NotContain("<linearGradient", "un degradado a 16 px se lee como un gris plano");
        small.Should().NotContain("#233047", "sin tronera: un rasgo interior de 1 px es ruido, no información");

        // Y los dos son el MISMO icono: mismo lienzo y mismos colores de marca.
        foreach (string svg in new[] { big, small })
        {
            svg.Should().Contain("viewBox=\"0 0 256 256\"");
            svg.Should().Contain("#7fb0ff", "la luz de vigía");
            svg.Should().Contain("#e8ecf4", "la piedra de la torre");
        }
    }

    /// <summary>
    /// Los cuatro sitios donde Windows enseña el icono: el ejecutable (que además lo usan el
    /// Explorador y los accesos directos), la ventana —de donde salen la barra de título, el
    /// Alt-Tab y la barra de tareas— y el aviso propio de la aplicación.
    /// </summary>
    [Fact]
    public void El_icono_esta_declarado_en_el_ejecutable_en_la_ventana_y_en_el_aviso()
    {
        string csproj = Source("src/Atalaya.App/Atalaya.App.csproj");
        csproj.Should().Contain("<ApplicationIcon>").And.Contain("atalaya.ico");
        csproj.Should().Contain("<Resource Include=", "la ventana no puede depender de un fichero suelto");

        string shell = Source("src/Atalaya.App/MainWindow.xaml");
        // La forma del pack URI lleva el ensamblado desde F26 §B —la corta se resuelve contra el
        // ensamblado de ENTRADA, y la carcasa la monta también el banco de capturas—, así que lo
        // que se busca es el RECURSO, no la sintaxis con la que se nombra.
        shell.Should().MatchRegex(@"Icon=""pack://application:,,,/[^""]*assets/atalaya\.ico""");

        // El mismo icono en los DOS sitios de la carcasa, no dos dibujos parecidos: el de la
        // ventana —de donde salen barra de tareas y Alt-Tab— y el del aviso efímero, que es
        // NUESTRO y por eso lo firma la aplicación. Al lado del texto de la barra de título NO va
        // ninguno: se probó y no convenció.
        //
        // Eran TRES hasta F26 §A: el aviso de versión nueva llevaba el icono de Atalaya en una
        // banda de dos líneas. Al pasar a una tira fina (D-957) el icono deja de ser una firma y
        // pasa a ser el símbolo de lo que la tira DICE —hay algo que atender—, así que lleva el
        // triángulo de aviso con el color de aviso. El icono de la aplicación en una tira de 24 px
        // no firmaba nada: solo ocupaba.
        Regex.Matches(shell, @"pack://application:,,,/[^""]*assets/atalaya\.ico").Count
            .Should().Be(2, "la ventana y el toast");
        shell.Should().NotContain("ui:TitleBar.Icon", "la barra de título se lee mejor limpia");
    }

    /// <summary>
    /// Un <c>.ico</c> pedido sin <c>DecodePixelWidth</c> se decodifica por su fotograma MÁS
    /// GRANDE y se encoge — que es exactamente el borrón que la variante de silueta existe para
    /// evitar. Sin esto, los dos SVG del pipeline no servirían de nada en pantalla: el de 16
    /// estaría en el fichero y no lo vería nadie.
    /// </summary>
    [Fact]
    public void Cada_uso_del_icono_pide_el_fotograma_de_su_tamano()
    {
        var usages = new Dictionary<string, int[]>
        {
            // El aviso efímero. El banner de versión tenía otro hasta F26 §A; ver arriba.
            ["src/Atalaya.App/MainWindow.xaml"] = new[] { 16 },
            ["src/Atalaya.App/Views/AboutView.xaml"] = new[] { 64 },
        };

        foreach ((string file, int[] expected) in usages)
        {
            string xaml = Source(file);

            // Ningún uso por la vía corta: «Source="pack://…ico"» no puede elegir fotograma. El
            // «(?<![A-Za-z])» está porque UriSource= TERMINA en Source= y sería un falso positivo.
            Regex.IsMatch(xaml, "(?<![A-Za-z])Source=\"pack://").Should()
                .BeFalse($"en {file}, un Source directo se queda con el fotograma de 256");

            Regex.Matches(xaml, "DecodePixelWidth=\"(\\d+)\"")
                .Select(m => int.Parse(m.Groups[1].Value))
                .Should().Equal(expected, $"en {file}");
        }

        // Y los tamaños que se piden son tamaños que el .ico TRAE.
        byte[] ico = File.ReadAllBytes(Asset("atalaya.ico"));
        int count = BitConverter.ToUInt16(ico, 4);
        var available = Enumerable.Range(0, count)
            .Select(i => ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)])
            .ToHashSet();

        available.Should().Contain(16).And.Contain(64);
    }

    /// <summary>
    /// Un <c>pack://</c> con una ruta que no existe compila y revienta al abrir la ventana. Aquí
    /// se comprueba que el recurso está DENTRO del ensamblado y con la ruta exacta que se pide.
    /// </summary>
    [Fact]
    public void El_recurso_del_icono_existe_en_el_ensamblado()
    {
        // El esquema «pack» lo registra WPF al inicializarse; en un runner de tests hay que
        // provocarlo o el Uri ni siquiera se puede construir.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        // Desde FUERA de la aplicación hay que nombrar el ensamblado: la forma corta que usa la
        // ventana —«/assets/atalaya.ico»— se resuelve contra Application.ResourceAssembly, que
        // aquí es el runner de tests. La RUTA que se comprueba es la misma.
        StreamResourceInfo? info = Application.GetResourceStream(
            new Uri("pack://application:,,,/Atalaya;component/assets/atalaya.ico", UriKind.Absolute));

        info.Should().NotBeNull("la ventana y el toast lo piden por esta ruta");
        using Stream stream = info!.Stream;
        stream.Length.Should().BeGreaterThan(1000, "es el .ico entero, no un hueco");
    }

    // =============================================================== §2 · la contención

    /// <summary>
    /// <b>Dónde NO va la marca.</b> Era de tres emplazamientos —bienvenida, Cuenta y «Acerca
    /// de»— y con F38 queda UNO: la cabecera de «Acerca de».
    /// <para>
    /// Los otros dos se caen por la misma regla, no por gusto. La bienvenida no tiene cuenta
    /// conectada de la que sacar el nombre de la organización, así que su marca solo podía estar
    /// vacía; y la tarjeta de Cuenta ya escribe la organización bajo el nombre del usuario, así
    /// que una segunda marca en la misma fila sería el mismo dato dos veces.
    /// </para>
    /// <para>
    /// La barra de título NO cuenta: lo que va ahí es el icono de la APLICACIÓN, que es de casa.
    /// Este test sigue siendo el que impide que dentro de seis meses haya una marca en el raíl.
    /// </para>
    /// </summary>
    [Fact]
    public void La_marca_solo_aparece_en_el_unico_sitio_acordado()
    {
        Regex.Matches(Source("src/Atalaya.App/Views/AboutView.xaml"), "controls:BrandMark")
            .Count.Should().Be(1, "la cabecera de «Acerca de», y ahí se acaba");

        foreach (string forbidden in new[]
                 {
                     "src/Atalaya.App/MainWindow.xaml",
                     "src/Atalaya.App/Views/AccountView.xaml",
                     "src/Atalaya.App/Views/SessionView.xaml",
                     "src/Atalaya.App/Views/FindingsView.xaml",
                     "src/Atalaya.App/Views/MetricsView.xaml",
                     "src/Atalaya.App/Views/ReportsView.xaml",
                     "src/Atalaya.App/Views/InventoryView.xaml",
                     "src/Atalaya.App/Views/PortfolioView.xaml",
                     "src/Atalaya.App/Views/SettingsView.xaml",
                     "src/Atalaya.App/Views/OnboardingView.xaml",
                 })
        {
            Source(forbidden).Should().NotContain("BrandMark", $"la marca no va en {forbidden}");
        }
    }

    /// <summary>
    /// <b>La marca sale de la organización configurada, y de ningún sitio más</b> (F38 §1).
    /// El único emplazamiento que queda ata su texto al dato del hub; lo que NO puede volver a
    /// hacer es pintarse siempre. Se mira en el XAML porque es ahí donde se rompería: cambiar el
    /// enlace por un literal compila igual de bien y no lo nota nadie hasta que un despliegue sin
    /// organización enseña el nombre de otra.
    /// </summary>
    [Fact]
    public void La_marca_de_la_cabecera_esta_atada_a_la_organizacion_del_hub()
    {
        string about = Source("src/Atalaya.App/Views/AboutView.xaml");

        about.Should().Contain("Organization=\"{Binding Info.Organization}\"",
            "el texto de la marca es el nombre que da el hub, no una constante");
        about.Should().NotContain("LogoHeight", "ya no hay logotipo que dimensionar");
    }

    /// <summary>
    /// <b>Sin organización configurada no hay marca, ni hueco reservado</b> (F38 §1), y se
    /// comprueba sobre el modelo: el despliegue por defecto sale sin <c>organizationLogin</c>
    /// —lo dejó vacío el arreglo del actualizador— y ésa es la situación normal, no un error.
    /// <para>
    /// La tarjeta de Cuenta esconde entonces su línea de organización en vez de escribir un
    /// rótulo con nada detrás, que es la versión de «hueco roto» de este caso.
    /// </para>
    /// </summary>
    [Fact]
    public void Sin_organizacion_configurada_la_cuenta_no_reserva_hueco_de_marca()
    {
        new DeployConfig().ChecksOrgMembership.Should()
            .BeFalse("un despliegue sin organización es una situación normal");
        new DeployConfig { OrganizationLogin = "   " }.ChecksOrgMembership.Should()
            .BeFalse("un nombre en blanco tampoco es una organización");
        new DeployConfig { OrganizationLogin = "Acme" }.ChecksOrgMembership.Should().BeTrue();

        Source("src/Atalaya.App/Views/AccountView.xaml").Should()
            .Contain("Visibility=\"{Binding ShowOrganization",
                "la línea entera se esconde, rótulo incluido");
    }

    /// <summary>
    /// <b>Lo que Windows enseña en Propiedades del ejecutable</b> (F38 §1.5). Company, Product y
    /// Copyright son los de la aplicación y no los de ninguna organización. Sin estas propiedades
    /// declaradas MSBuild las deriva del nombre del ensamblado y deja Copyright vacío: salían bien
    /// por accidente, y lo que sale bien por accidente se rompe sin que nadie lo vea.
    /// </summary>
    [Fact]
    public void El_binario_dice_de_quien_es()
    {
        Assembly app = typeof(AboutInfo).Assembly;

        app.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company.Should().Be("Atalaya");
        app.GetCustomAttribute<AssemblyProductAttribute>()!.Product.Should().Be("Atalaya");
        app.GetCustomAttribute<AssemblyCopyrightAttribute>()!.Copyright.Should()
            .StartWith("©").And.Contain("Álvaro López Ciller");
    }

    /// <summary>
    /// <b>El barrido: la marca de la organización donde nació Atalaya no vuelve</b> (F38).
    /// Es EL test de esta fase — el que impide que dentro de seis meses alguien la reintroduzca
    /// en un XAML, en un fixture o en el README sin que nadie se entere. DECISIONS y BACKLOG
    /// quedan fuera a propósito: son historia, y reescribirla para que no nombre lo que pasó
    /// sería mentir sobre por qué está esto aquí.
    /// </summary>
    [Fact]
    public void Ninguna_marca_de_la_antigua_organizacion_sobrevive_en_el_producto()
    {
        // Partidos a propósito: enteros, ESTE fichero sería el primer infractor que
        // encontrara su propio barrido.
        string[] words = { "max" + "am", "applied-" + "advanced" };
        var offenders = new List<string>();

        foreach (string file in ProductFiles())
        {
            string text = File.ReadAllText(file).ToLowerInvariant();
            foreach (string word in words)
            {
                if (text.Contains(word, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)} → «{word}»");
                }
            }
        }

        offenders.Should().BeEmpty("la marca de la antigua organización no vive en el producto (F38)");
    }

    /// <summary>
    /// Los ficheros que SÍ se barren: el código, los tests, los workflows, los scripts y los dos
    /// documentos que un usuario lee. Se excluyen <c>bin</c>/<c>obj</c> —artefactos, no fuentes—
    /// y los <c>.md</c> de DECISIONS y BACKLOG, que son historia.
    /// </summary>
    private static IEnumerable<string> ProductFiles()
    {
        string root = RepoRoot();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".csproj", ".props", ".targets", ".json", ".ps1", ".yml", ".yaml",
            ".resx", ".tsv", ".txt", ".sln", ".svg",
        };

        foreach (string dir in new[] { "src", "tests", ".github", "scripts", "assets" })
        {
            string full = Path.Combine(root, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                bool built = relative
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj");
                if (!built && extensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }

        yield return Path.Combine(root, "README.md");
        yield return Path.Combine(root, "MANUAL.md");
    }

    // =============================================================== §2 · la firma del informe

    [Fact]
    public void El_informe_de_sesion_lo_firma_la_organizacion()
    {
        string report = ReportBuilder.BuildSessionReport(
            AppOf(), SessionOf(), Array.Empty<Finding>(), pendingUnits: 0, largeUnits: 0, organization: "Acme");

        report.TrimEnd().Should().EndWith("Atalaya · Acme");
        report.Should().Contain("---", "la firma va separada del cuerpo");
    }

    /// <summary>
    /// Sin organización se firma solo «Atalaya». Escribir «Atalaya ·» y nada detrás sería enseñar
    /// el hueco de un dato que el hub todavía no da.
    /// </summary>
    [Fact]
    public void Sin_organizacion_la_firma_no_deja_un_separador_colgando()
    {
        string report = ReportBuilder.BuildSessionReport(
            AppOf(), SessionOf(), Array.Empty<Finding>(), pendingUnits: 0, largeUnits: 0);

        report.TrimEnd().Should().EndWith("Atalaya");
        report.Should().NotContain("Atalaya ·");
    }

    [Fact]
    public void El_consolidado_de_cierre_tambien_va_firmado()
    {
        string report = ReportBuilder.BuildCycleCloseReport(
            AppOf(), closedCycle: 1, promoted: 0, findings: Array.Empty<Finding>(), organization: "Acme");

        report.TrimEnd().Should().EndWith("Atalaya · Acme");
    }

    // =============================================================== §3 · Acerca de

    /// <summary>
    /// La versión es la del ENSAMBLADO, no una constante escrita a mano que se queda vieja.
    /// <para>
    /// BUGFIX-VERSION: se compara contra <see cref="AssemblyInformationalVersionAttribute"/> y ya
    /// NO contra <c>Assembly.GetName().Version</c>. La segunda es numérica de cuatro campos —se
    /// queda en 1.0.0.0 con facilidad— y este test la daba por buena: fijaba justo la fuente que
    /// hacía que un build local dijera «1.0.0».
    /// </para>
    /// </summary>
    [Fact]
    public void La_version_del_acerca_de_es_la_real_del_ensamblado()
    {
        string version = AboutInfo.CurrentVersion();

        version.Should().NotBeNullOrWhiteSpace().And.NotBe("—");
        version.Should().MatchRegex(@"^\d+\.\d+", "es un número de versión, no un texto");
        version.Should().Be(
            typeof(MetricsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Trim(),
            "la informativa del binario que se está ejecutando, con sus sufijos");

        // La vista la pinta a través de `Facts`, que la arma el view-model desde el ensamblado.
        // Lo que este test defiende es que NO haya un número escrito en el XAML, que es el defecto
        // de BUGFIX-VERSION: un literal envejece sin que nadie lo note.
        Source("src/Atalaya.App/Views/AboutView.xaml").Should()
            .Contain("{Binding Facts}")
            .And.NotContain("Versión 1.", "la versión no se escribe en el XAML");
    }

    [Fact]
    public void Sin_organizacion_el_acerca_de_no_ensena_un_bloque_vacio()
    {
        var sin = new AboutInfo(null, "1.2.3", "https://github.com/org/repo");
        sin.HasOrganization.Should().BeFalse();
        sin.Signature.Should().Be("Atalaya");

        var con = new AboutInfo("  Acme  ", "1.2.3", "https://github.com/org/repo");
        con.HasOrganization.Should().BeTrue();
        con.Organization.Should().Be("Acme", "se recorta el espacio sobrante");
        con.Signature.Should().Be("Atalaya · Acme", "la misma firma que va al pie del informe");
    }

    /// <summary>
    /// <b>«Acerca de» es una PÁGINA del raíl</b> (F26 §C, revisión), y la construye con la
    /// organización que diga el hub.
    /// <para>
    /// Hasta aquí era un modal que se abría desde el fondo de Ajustes → Avanzado. La regla de F6.4
    /// —que el gesto exista y traiga los datos REALES, no un número escrito a mano— no cambia; lo
    /// que cambia es dónde vive, y eso sí se comprueba: en el grupo Sistema y detrás de Ajustes,
    /// que es el orden que el raíl declara.
    /// </para>
    /// </summary>
    [Fact]
    public void El_acerca_de_es_una_pagina_del_rail_con_la_organizacion_del_hub()
    {
        string root = Path.Combine(_root, "settings");
        var paths = new AppPaths(Path.Combine(root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(paths, settings);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Acme" });

        var vm = new AboutViewModel(hub);

        vm.Info.Organization.Should().Be("Acme");
        vm.Info.Version.Should().Be(AboutInfo.CurrentVersion());
        vm.RailKey.Should().Be("about", "el raíl resalta su entrada mientras la página está delante");
        vm.Title.Should().Be("Acerca de");

        // Y ya no se abre desde Ajustes: un gesto que sigue existiendo en dos sitios es un gesto
        // que se mantiene en dos sitios.
        Source("src/Atalaya.App/Views/SettingsView.xaml").Should().NotContain("ShowAboutCommand");
        typeof(SettingsViewModel).GetProperty("ShowAboutCommand")
            .Should().BeNull("Ajustes ya no lo ofrece");
    }

    private sealed class NoFactoryResetConfirmer : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // =============================================================== El pipeline

    /// <summary>
    /// El .ico se regenera con un script, no a mano. Se comprueba que el script existe, que sabe
    /// de las dos fuentes y que la herramienta que llama está fuera de la solución: construir
    /// Atalaya no puede depender de tener un renderizador de SVG.
    /// </summary>
    [Fact]
    public void El_pipeline_de_regeneracion_esta_documentado_y_fuera_de_la_solucion()
    {
        string script = Source("scripts/build-assets.ps1");
        script.Should().Contain("atalaya-icon.svg").And.Contain("atalaya-icon-small.svg");
        script.Should().Contain("IconGen");
        // F38 · Y `assets/` se queda SOLO con el icono de casa. Un logotipo de organización
        // vuelto a dejar caer aquí es la forma en que esto reaparecería: se despliega solo, sin
        // que nadie escriba una línea de código.
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "assets"))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(
                new[] { "atalaya-icon.svg", "atalaya-icon-small.svg", "atalaya.ico" },
                "la marca de una organización es un dato del hub, no un asset (F38)");

        File.Exists(Path.Combine(RepoRoot(), "scripts", "IconGen", "IconGen.csproj")).Should().BeTrue();
        Source("Atalaya.sln").Should().NotContain("IconGen", "la herramienta no entra en la solución");

        Source("README.md").Should().Contain("build-assets.ps1", "y el README dice cómo se usa");
    }

    // =============================================================== Utilidades

    /// <summary>Una carpeta temporal con los ficheros pedidos dentro (contenido irrelevante).</summary>
    private string Dir(params string[] files)
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string file in files)
        {
            File.WriteAllBytes(Path.Combine(dir, file), new byte[] { 1, 2, 3 });
        }

        return dir;
    }

    private static AppConfig AppOf()
        => new() { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 };

    private static AuditSession SessionOf()
        => new()
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow,
            CycleN = 1,
        };

    private static string Asset(string file) => Path.Combine(RepoRoot(), "assets", file);

    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return dir!.FullName;
    }
}
