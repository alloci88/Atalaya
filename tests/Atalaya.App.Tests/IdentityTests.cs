using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Resources;
using System.Reflection;
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
            ["src/Atalaya.App/Views/AboutDialog.xaml"] = new[] { 64 },
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

    // =============================================================== §2 · el logotipo

    /// <summary>
    /// Sin asset, el hueco DESAPARECE. Ni marco vacío, ni interrogante, ni traza: un despliegue
    /// sin marca es una situación normal, no un error. Es la diferencia entre un hueco preparado
    /// y un hueco roto.
    /// </summary>
    [Fact]
    public void Sin_ningun_asset_no_hay_nada_que_pintar()
    {
        var assets = new BrandAssets(Dir());

        assets.HasAny.Should().BeFalse();
        assets.Resolve(dark: true).Should().BeNull();
        assets.Resolve(dark: false).Should().BeNull();
    }

    /// <summary>
    /// El caso de hoy: solo está el logotipo normal, con las letras en gris oscuro. En claro se
    /// lee tal cual; en oscuro NO, y por eso pide placa. Recolorearlo no es decisión nuestra, así
    /// que lo que se cambia es lo de debajo.
    /// </summary>
    [Fact]
    public void Con_solo_el_logotipo_normal_el_tema_oscuro_pide_placa()
    {
        string dir = Dir(BrandAssets.LogoFile);
        var assets = new BrandAssets(dir);

        assets.HasAny.Should().BeTrue();
        assets.Resolve(dark: false).Should().Be(
            new BrandLogo(Path.Combine(dir, BrandAssets.LogoFile), NeedsPlate: false));
        assets.Resolve(dark: true).Should().Be(
            new BrandLogo(Path.Combine(dir, BrandAssets.LogoFile), NeedsPlate: true));
    }

    /// <summary>
    /// Y el día que comunicación entregue el negativo, basta con dejarlo en la carpeta: el tema
    /// oscuro lo usa y la placa deja de hacer falta. Sin recompilar y sin tocar una línea.
    /// </summary>
    [Fact]
    public void Con_la_version_en_negativo_el_tema_oscuro_la_usa_y_suelta_la_placa()
    {
        string dir = Dir(BrandAssets.LogoFile, BrandAssets.DarkLogoFile);
        var assets = new BrandAssets(dir);

        assets.Resolve(dark: true).Should().Be(
            new BrandLogo(Path.Combine(dir, BrandAssets.DarkLogoFile), NeedsPlate: false));
        assets.Resolve(dark: false).Should().Be(
            new BrandLogo(Path.Combine(dir, BrandAssets.LogoFile), NeedsPlate: false),
            "en claro sigue mandando el logotipo normal");
    }

    /// <summary>
    /// Y si por lo que sea solo está el negativo, se enseña igual y SIN placa: una placa clara
    /// bajo letras claras sería peor que ninguna.
    /// </summary>
    [Fact]
    public void Con_solo_el_negativo_se_usa_en_los_dos_temas_y_nunca_con_placa()
    {
        string dir = Dir(BrandAssets.DarkLogoFile);
        var assets = new BrandAssets(dir);

        assets.Resolve(dark: true)!.NeedsPlate.Should().BeFalse();
        assets.Resolve(dark: false)!.NeedsPlate.Should().BeFalse();
        assets.Resolve(dark: false)!.Path.Should().EndWith(BrandAssets.DarkLogoFile);
    }

    /// <summary>
    /// <b>El logotipo NO se toca.</b> Lo desplegado tiene que ser byte a byte lo que entregó
    /// comunicación: ni recoloreado, ni redibujado, ni siquiera vuelto a codificar. La única
    /// preparación permitida era técnica —fondo a transparencia— y esta fuente ya venía con él,
    /// así que el paso correcto era no hacer nada.
    /// </summary>
    [Fact]
    public void El_logotipo_desplegado_es_byte_a_byte_el_que_entrego_comunicacion()
    {
        byte[] source = File.ReadAllBytes(Asset("maxam-logo-source.png"));
        byte[] deployed = File.ReadAllBytes(Asset(BrandAssets.LogoFile));

        deployed.Should().Equal(source);
    }

    /// <summary>
    /// Las DOS variantes viajan junto al ejecutable —sin eso el hueco se colapsaría siempre— y
    /// las fuentes no: no se despliega lo que no se pinta.
    /// </summary>
    [Fact]
    public void Las_dos_variantes_se_despliegan_junto_al_ejecutable_y_las_fuentes_no()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, BrandAssets.FolderName);

        File.Exists(Path.Combine(dir, BrandAssets.LogoFile)).Should().BeTrue("el csproj lo copia a la salida");
        File.Exists(Path.Combine(dir, BrandAssets.DarkLogoFile)).Should()
            .BeTrue("la versión en negativo llegó y también se despliega");

        Directory.EnumerateFiles(dir, "*-source.png").Should()
            .BeEmpty("las fuentes se quedan en el repositorio");
        Source("src/Atalaya.App/Atalaya.App.csproj").Should()
            .Contain("maxam-logo*-source.png", "el comodín excluye las fuentes de las DOS variantes");
    }

    // =============================================================== §2 · la contención

    /// <summary>
    /// <b>Dónde NO va la marca.</b> Tres emplazamientos y ni uno más: la bienvenida, la página
    /// Cuenta y el «Acerca de». La aplicación es la herramienta; el logo es la firma, no el papel
    /// pintado. Este test es el que impide que dentro de seis meses haya un logo en el rail.
    /// <para>
    /// La barra de título NO cuenta: lo que va ahí es el icono de la APLICACIÓN, que es de casa.
    /// </para>
    /// </summary>
    [Fact]
    public void La_marca_solo_aparece_en_los_tres_sitios_acordados()
    {
        var placements = new Dictionary<string, int>
        {
            ["src/Atalaya.App/Views/AccountView.xaml"] = 2,   // bienvenida + organización
            ["src/Atalaya.App/Views/AboutDialog.xaml"] = 1,
        };

        foreach ((string file, int expected) in placements)
        {
            Regex.Matches(Source(file), "controls:BrandMark").Count.Should().Be(expected, $"en {file}");
        }

        foreach (string forbidden in new[]
                 {
                     "src/Atalaya.App/MainWindow.xaml",
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
            Source(forbidden).Should().NotContain("maxam", $"ni por la puerta de atrás en {forbidden}");
        }
    }

    // =============================================================== §2 · la firma del informe

    [Fact]
    public void El_informe_de_sesion_lo_firma_la_organizacion()
    {
        string report = ReportBuilder.BuildSessionReport(
            AppOf(), SessionOf(), Array.Empty<Finding>(), pendingUnits: 0, largeUnits: 0, organization: "Maxam");

        report.TrimEnd().Should().EndWith("Atalaya · Maxam");
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
            AppOf(), closedCycle: 1, promoted: 0, findings: Array.Empty<Finding>(), organization: "Maxam");

        report.TrimEnd().Should().EndWith("Atalaya · Maxam");
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

        Source("src/Atalaya.App/Views/AboutDialog.xaml").Should()
            .Contain("{Binding VersionLabel}")
            .And.NotContain("Versión 1.", "la versión no se escribe en el XAML");
    }

    [Fact]
    public void Sin_organizacion_el_acerca_de_no_ensena_un_bloque_vacio()
    {
        var sin = new AboutInfo(null, "1.2.3", "https://github.com/org/repo");
        sin.HasOrganization.Should().BeFalse();
        sin.Signature.Should().Be("Atalaya");
        sin.VersionLabel.Should().Be("Versión 1.2.3");

        var con = new AboutInfo("  Maxam  ", "1.2.3", "https://github.com/org/repo");
        con.HasOrganization.Should().BeTrue();
        con.Organization.Should().Be("Maxam", "se recorta el espacio sobrante");
        con.Signature.Should().Be("Atalaya · Maxam", "la misma firma que va al pie del informe");
    }

    /// <summary>El gesto existe, está al final de Ajustes y abre el diálogo con los datos reales.</summary>
    [Fact]
    public void Ajustes_ofrece_acerca_de_y_lo_abre_con_la_organizacion_del_hub()
    {
        string xaml = Source("src/Atalaya.App/Views/SettingsView.xaml");
        xaml.Should().Contain("{Binding ShowAboutCommand}");
        xaml.IndexOf("ShowAboutCommand", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("SaveCommand", StringComparison.Ordinal), "va al final de la página");

        string root = Path.Combine(_root, "settings");
        var paths = new AppPaths(Path.Combine(root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(paths, settings);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Maxam" });

        var dialog = new RecordingAboutDialog();
        var vm = new SettingsViewModel(
            settings,
            new Atalaya.Copilot.FakeCopilotAgent(),
            new ToastCenter(),
            new FactoryResetService(hub, paths, settings, TestFactory.Account(paths), new OpenSessionStore(paths)),
            new NoFactoryResetConfirmer(),
            hub,
            new NavigationService(new NoServices()),
            dialog);

        vm.ShowAboutCommand.Execute(null);

        dialog.Shown.Should().ContainSingle();
        dialog.Shown[0].Organization.Should().Be("Maxam");
        dialog.Shown[0].Version.Should().Be(AboutInfo.CurrentVersion());
    }

    private sealed class RecordingAboutDialog : IAboutDialog
    {
        public List<AboutInfo> Shown { get; } = new();

        public void Show(AboutInfo info) => Shown.Add(info);
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
