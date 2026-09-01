using Atalaya.App.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-ARRANQUE — el test que faltaba.
/// <para>
/// La 1.1.3 se publicó con 1.661 tests en verde y no arrancaba. Ninguno de esos 1.661 montaba el
/// contenedor de verdad: cada servicio se probaba construido a mano con sus dobles, que es lo que
/// hace que los tests sean rápidos y también lo que hace que <b>el grafo de dependencias no lo
/// mire nadie</b>. Un registro que falta no rompe la compilación —el contenedor resuelve en
/// tiempo de ejecución— y no rompe ningún test unitario, porque ningún test unitario pide nada al
/// contenedor. Rompe el arranque, y solo el arranque.
/// </para>
/// <para>
/// Este test pide al contenedor <b>todos</b> los servicios que la aplicación registra, sobre una
/// carpeta de estado vacía — que es la otra mitad de lo que se escapó: en la máquina de quien
/// desarrolla siempre hay ajustes y cuenta, así que el camino del primer arranque no lo recorría
/// nadie hasta que lo recorrió un usuario.
/// </para>
/// </summary>
public sealed class StartupSelfCheckTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public StartupSelfCheckTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "atalaya-selfcheck", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// <b>Primer arranque, carpeta de estado vacía.</b> Sin ajustes, sin cuenta, sin hub — como la
    /// máquina de quien descomprime el zip y hace doble clic.
    /// </summary>
    [Fact]
    public async Task El_arranque_en_limpio_monta_el_contenedor_entero()
    {
        SelfCheckReport report = await StartupSelfCheck.RunAsync(new AppPaths(_root), shell: false);

        _output.WriteLine(report.Text);

        report.Ok.Should().BeTrue(report.Text);
        report.ExitCode.Should().Be(0);
    }

    /// <summary>
    /// Y el segundo arranque, con lo que dejó el primero. No es lo mismo: hay `settings.json`
    /// escrito, y las migraciones tienen algo sobre lo que correr.
    /// </summary>
    [Fact]
    public async Task El_segundo_arranque_tambien()
    {
        var paths = new AppPaths(_root);
        await StartupSelfCheck.RunAsync(paths, shell: false);

        SelfCheckReport report = await StartupSelfCheck.RunAsync(paths, shell: false);

        _output.WriteLine(report.Text);
        report.Ok.Should().BeTrue(report.Text);
    }

    /// <summary>El parte dice lo que hay que decir: qué falló, y que no se publique.</summary>
    [Fact]
    public void Un_parte_con_un_paso_roto_no_deja_publicar()
    {
        var report = new SelfCheckReport(new[]
        {
            new SelfCheckStep("contenedor", true),
            new SelfCheckStep("servicios", false, "MainViewModel: nadie registró IAlgo"),
        });

        report.Ok.Should().BeFalse();
        report.ExitCode.Should().Be(1);
        report.Text.Should().Contain("NO ARRANCA");
        report.Text.Should().Contain("nadie registró IAlgo", "la causa va en el parte, no solo el veredicto");
    }

    [Fact]
    public void El_interruptor_se_reconoce_y_el_parte_tiene_destino()
    {
        StartupSelfCheck.IsRequested(new[] { "--selfcheck" }).Should().BeTrue();
        StartupSelfCheck.IsRequested(new[] { "--SELFCHECK" }).Should().BeTrue();
        StartupSelfCheck.IsRequested(Array.Empty<string>()).Should().BeFalse();

        StartupSelfCheck.ReportPath(new[] { "--selfcheck", "--report", @"C:\tmp\parte.txt" })
            .Should().Be(@"C:\tmp\parte.txt");
        StartupSelfCheck.ReportPath(new[] { "--selfcheck" }).Should().BeNull();
        StartupSelfCheck.ReportPath(new[] { "--selfcheck", "--report" })
            .Should().BeNull("«--report» sin valor no es una ruta");
    }
}
