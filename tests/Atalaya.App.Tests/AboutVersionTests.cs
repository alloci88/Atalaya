using System.Reflection;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-VERSION — «Acerca de» no puede hacer pasar un build local por una release, ni enlazar a
/// un 404.
/// <para>
/// El parte: el diálogo decía «Versión 1.0.0» sobre un binario publicado en local desde un árbol
/// que ya iba por la 1.0.3, y los enlaces llevaban a <c>github.com/maxam/atalaya</c>, que no
/// existe. Lo primero no era una mentira sobre lo compilado —el props decía 1.0.0— pero sí inducía
/// a error: alguien podía reportar un fallo «de la 1.0.0» venido de un build sin publicar.
/// </para>
/// </summary>
public sealed class AboutVersionTests
{
    // ================================================================ release o build local

    [Theory]
    [InlineData("1.0.3-dev+sha", true)]
    [InlineData("1.0.3-dev.4", true)]
    [InlineData("1.0.3-DEV", true)]
    [InlineData("1.0.3", false)]
    [InlineData("1.0.3+sha", false)]
    public void La_marca_de_desarrollo_se_reconoce_por_su_identificador(string version, bool expected)
        => new AboutInfo(null, version, null).IsDevelopmentBuild.Should().Be(expected);

    /// <summary>
    /// Un pre-release DE VERDAD —publicado por el workflow— no es un build local. Por eso se mira
    /// el identificador completo y no un «contiene dev».
    /// </summary>
    [Theory]
    [InlineData("1.1.0-rc.1")]
    [InlineData("2.0.0-beta")]
    public void Un_prerelease_publicado_no_se_lee_como_build_local(string version)
    {
        new AboutInfo(null, version, null).IsDevelopmentBuild.Should().BeFalse();
    }

    // ================================================================ la versión base

    [Theory]
    [InlineData("1.0.3", "1.0.3")]
    [InlineData("1.0.3-dev+0f920d9", "1.0.3")]
    [InlineData("1.0.3-dev.4+abc1234", "1.0.3")]
    [InlineData("1.0.3+abcdef1", "1.0.3")]
    [InlineData("", "")]
    public void La_version_base_deja_solo_el_numero(string version, string expected)
        => AboutInfo.BaseVersion(version).Should().Be(expected);

    // ================================================================ de dónde sale la versión

    /// <summary>
    /// Se lee de la informativa y NO de <c>Assembly.GetName().Version</c>, que es numérica de
    /// cuatro campos y se queda en 1.0.0.0 con muchísima facilidad — que es donde vivía el fallo.
    /// </summary>
    [Fact]
    public void La_version_sale_del_atributo_informativo_del_ensamblado()
    {
        Assembly app = typeof(AboutInfo).Assembly;
        string? informational = app
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        informational.Should().NotBeNullOrWhiteSpace();
        AboutInfo.CurrentVersion().Should().Be(informational!.Trim());
    }

    /// <summary>
    /// Y este ensamblado se construye en local, así que TIENE que llevar la marca. Es el test que
    /// habría cazado el parte: con el estampado roto, aquí saldría «1.0.0» pelado.
    /// </summary>
    [Fact]
    public void El_binario_de_los_tests_se_declara_build_local()
        => new AboutInfo(null, AboutInfo.CurrentVersion(), null).IsDevelopmentBuild
            .Should().BeTrue("los tests no los compila el workflow de release");

    // ================================================================ los enlaces

    [Fact]
    public void Los_enlaces_salen_del_despliegue_y_el_manual_se_deriva()
    {
        var info = new AboutInfo(null, "1.0.3",
            "https://github.com/Applied-Advanced-Solutions-AAS/Atalaya");

        info.HasRepository.Should().BeTrue();
        info.Repository.Should().Be("https://github.com/Applied-Advanced-Solutions-AAS/Atalaya");
        info.Manual.Should().Be(
            "https://github.com/Applied-Advanced-Solutions-AAS/Atalaya/blob/main/MANUAL.md");
    }

    [Fact]
    public void La_barra_final_del_ajuste_no_produce_una_url_con_doble_barra()
        => new AboutInfo(null, "1.0.3", "https://github.com/org/repo/").Manual
            .Should().Be("https://github.com/org/repo/blob/main/MANUAL.md");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_appRepoUrl_no_se_ensena_un_enlace_roto(string? url)
    {
        var info = new AboutInfo(null, "1.0.3", url);

        info.HasRepository.Should().BeFalse();
        info.Repository.Should().BeNull();
        info.Manual.Should().BeNull("un enlace a un 404 es peor que ningún enlace");
        info.HasNoLinks.Should().BeTrue();
        info.NoLinksNotice.Should().Contain("appRepoUrl", "el hueco dice qué falta y dónde");
    }

    /// <summary>
    /// Y la PÁGINA los esconde de verdad, que es la mitad que el compilador no vigila. Era un
    /// diálogo hasta F26 §C; el «Acerca de» es ahora una entrada del raíl (grupo Sistema).
    /// </summary>
    [Fact]
    public void El_acerca_de_esconde_los_enlaces_cuando_no_hay_repositorio()
    {
        string xaml = Source("src/Atalaya.App/Views/AboutView.xaml");

        xaml.Should().Contain("{Binding Info.HasRepository, Converter={StaticResource BoolToVisibility}}");
        xaml.Should().Contain("{Binding Info.NoLinksNotice}");
        xaml.Should().Contain("{Binding Info.Repository}").And.Contain("{Binding Info.Manual}");
    }

    // ================================================================ ni una URL a mano

    /// <summary>
    /// Cero URLs de repositorio escritas a mano en el código. La única fuente es
    /// <c>appsettings.deploy.json</c>: dos URLs escritas por separado es exactamente cómo una de
    /// ellas acabó apuntando a un repositorio que no existe.
    /// </summary>
    [Fact]
    public void Ninguna_url_de_repositorio_vive_en_el_codigo()
    {
        DirectoryInfo root = Root();
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(root.FullName, "src"), "*.*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml") || IsBuildOutput(file))
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                // Solo LITERALES: una URL dentro de comillas es una que el programa usa. Las de
                // los comentarios son ejemplos («owner/repo»), y prohibirlas sería prohibir
                // explicar el formato.
                if (!line.Contains("\"https://github.com/", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("docs.github.com", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("github.com/login", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("github.com/settings", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("users.noreply.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "el repositorio se declara UNA vez, en appsettings.deploy.json; escribirlo también en "
            + "el código es como el «Acerca de» acabó enlazando a un 404");
    }

    /// <summary>Y el despliegue sí lo declara, que es la otra mitad.</summary>
    [Fact]
    public void El_despliegue_declara_el_repositorio_de_la_aplicacion()
    {
        string json = File.ReadAllText(
            Path.Combine(Root().FullName, "src", "Atalaya.App", DeployConfig.FileName));

        json.Should().Contain("appRepoUrl");
        DeployConfig deploy = DeployConfig.Load(
            Path.Combine(Root().FullName, "src", "Atalaya.App"));
        deploy.AppRepoUrl.Should().StartWith("https://github.com/");
        deploy.ChecksForUpdates.Should().BeTrue();
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!;
    }

    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(Root().FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
}
