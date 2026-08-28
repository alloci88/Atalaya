using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// H9.1 §2 — NINGÚN ROJO SIN CAUSA ATRIBUIBLE.
/// <para>
/// El caso real: un arreglo de <b>+2 −2</b> en un fichero terminó con «BUILD: FALLÓ — 18 errores»,
/// y ni uno solo era del cambio. La solución auditada —legacy, con un <c>.vcxproj</c> de C++, un
/// recurso de instalador que no está versionado y referencias que no restauran en esa máquina— ya
/// fallaba ANTES de tocarla. La aplicación no lo sabía, así que presentó lo heredado como si fuera
/// del agente.
/// </para>
/// <para>
/// Lo que se fija aquí son las tres piezas del arreglo: <b>ámbito</b> (se compila el proyecto de lo
/// tocado), <b>línea base</b> (el veredicto es un delta contra el mismo commit sin tocar) y
/// <b>exclusión con nota</b> (lo que dotnet no puede compilar se nombra, no se cuenta).
/// </para>
/// </summary>
public sealed class BuildScopeTests : IDisposable
{
    private const string Touched = "Common/CommonStatics.cs";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;

    public BuildScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-build", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _paths = new AppPaths(Path.Combine(_root, "local"));

        Write("XBlast.sln", "Microsoft Visual Studio Solution File\r\n");
        Write("Common/Common.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");
        Write("Common/CommonStatics.cs", "public static class CommonStatics { }");
        Write("Tests/Common.Tests/Common.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            + "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\" /></ItemGroup>"
            + "<ItemGroup><ProjectReference Include=\"..\\..\\Common\\Common.csproj\" /></ItemGroup></Project>");
        Write("Native/CCCoreWrapper.vcxproj", "<Project ToolsVersion=\"4.0\" />");
        Write("Native/wrapper.cpp", "// c++");
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

    // ================================================================= ámbito

    /// <summary>
    /// <b>Por defecto se compila el PROYECTO de lo tocado.</b> Es más rápido, más barato y —lo que
    /// importa— el veredicto pertenece al cambio: lo que se compila es donde se ha escrito.
    /// </summary>
    [Fact]
    public void El_ambito_por_defecto_es_el_proyecto_de_los_ficheros_tocados()
    {
        BuildPlan plan = BuildScopeResolver.Resolve(_clone, new[] { Touched }, fullSolution: false);

        plan.Target.Kind.Should().Be(BuildTargetKind.Project);
        plan.Target.Relative.Should().Be("Common/Common.csproj");
        plan.Target.Label.Should().Be("el proyecto Common/Common.csproj");
    }

    /// <summary>Y con el proyecto vienen SUS tests: los que le apuntan con un ProjectReference.</summary>
    [Fact]
    public void El_proyecto_tocado_arrastra_los_tests_que_lo_cubren()
    {
        BuildPlan plan = BuildScopeResolver.Resolve(_clone, new[] { Touched }, fullSolution: false);

        plan.TestProjects.Should().ContainSingle()
            .Which.Should().EndWith("Common.Tests.csproj");
    }

    /// <summary>
    /// La solución entera sigue estando, pero la pide el USUARIO. El agente no tiene forma de
    /// ampliar el ámbito: no es suya la decisión ni es suyo el tiempo que cuesta.
    /// </summary>
    [Fact]
    public void La_solucion_entera_es_una_decision_del_usuario()
    {
        BuildPlan plan = BuildScopeResolver.Resolve(_clone, new[] { Touched }, fullSolution: true);

        plan.Target.Kind.Should().Be(BuildTargetKind.Solution);
        plan.Target.Relative.Should().Be("XBlast.sln");
    }

    /// <summary>Tocar dos proyectos ya no es «un cambio en un proyecto»: se compila la solución, y se dice.</summary>
    [Fact]
    public void Un_cambio_que_toca_dos_proyectos_amplia_el_ambito_y_lo_explica()
    {
        Write("Otro/Otro.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("Otro/Cosa.cs", "// x");

        BuildPlan plan = BuildScopeResolver.Resolve(
            _clone, new[] { Touched, "Otro/Cosa.cs" }, fullSolution: false);

        plan.Target.Kind.Should().Be(BuildTargetKind.Solution);
        plan.Note.Should().Contain("2 proyectos");
    }

    /// <summary>
    /// El <c>.vcxproj</c> del caso real. No se intenta compilar y no se cuenta como fallo: se
    /// nombra, que es lo único honesto que se puede hacer sin traerse Visual Studio de dependencia.
    /// </summary>
    [Fact]
    public void Un_proyecto_de_C_mas_mas_queda_fuera_del_alcance_con_su_nota()
    {
        BuildPlan plan = BuildScopeResolver.Resolve(_clone, new[] { Touched }, fullSolution: false);

        plan.ExcludedProjects.Should().ContainSingle()
            .Which.Should().Be("Native/CCCoreWrapper.vcxproj");
    }

    /// <summary>Y si lo tocado ES el C++, se dice que no hay nada que Atalaya pueda compilar.</summary>
    [Fact]
    public void Tocar_solo_codigo_C_mas_mas_no_finge_una_compilacion()
    {
        BuildPlan plan = BuildScopeResolver.Resolve(
            _clone, new[] { "Native/wrapper.cpp" }, fullSolution: false);

        plan.Target.Kind.Should().Be(BuildTargetKind.None);
        plan.Note.Should().Contain("toolset de Visual Studio");
    }

    // ================================================================= el delta

    /// <summary>
    /// <b>EL CASO REAL, reproducido.</b> La solución ya fallaba con 18 errores antes del cambio, y
    /// sigue fallando con los mismos 18 después. Veredicto: <b>0 errores nuevos</b> — verde—, con
    /// los 18 preexistentes nombrados aparte. Antes de H9.1 esto era «BUILD: FALLÓ, 18 errores».
    /// </summary>
    [Fact]
    public void Los_errores_preexistentes_no_cuentan_como_fallo_del_cambio()
    {
        var runner = new ScriptedProcess()
            .Then(1, Errors(18))    // línea base, con el árbol limpio
            .Then(1, Errors(18))    // después del cambio: los mismos
            .Then(0, "Aprobadas: 42");

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: true, fullSolution: true), default);

        verdict.NewErrors.Should().Be(0);
        verdict.PreexistingErrors.Should().Be(18);
        verdict.Ok.Should().BeTrue("ninguno de los 18 lo ha traído este cambio");
        verdict.Headline.Should().Contain("0 error(es) nuevo(s)").And.Contain("18 preexistente(s)");
        verdict.Summary.Should().Contain("ya fallaban").And.Contain("No son tuyos");
    }

    /// <summary>
    /// El otro lado de la misma moneda, que es el que hace que esto valga algo: un error que SÍ
    /// trae el cambio se señala como nuevo y tumba el veredicto. Un delta que nunca dice «rojo» no
    /// es honestidad, es un semáforo estropeado.
    /// </summary>
    [Fact]
    public void Un_error_que_trae_el_cambio_se_senala_como_nuevo()
    {
        string introduced = $"{_clone}\\Common\\CommonStatics.cs(12,9): error CS0103: "
            + $"El nombre 'longitud' no existe en el contexto actual [{_clone}\\Common\\Common.csproj]";
        var runner = new ScriptedProcess()
            .Then(1, Errors(18))
            .Then(1, Errors(18) + "\r\n" + introduced);

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: true, fullSolution: true), default);

        verdict.NewErrors.Should().Be(1);
        verdict.PreexistingErrors.Should().Be(18);
        verdict.Ok.Should().BeFalse();
        verdict.New.Single().Should().Contain("CS0103");
        verdict.Summary.Should().Contain("Errores NUEVOS");
        verdict.Summary.Should().Contain("TESTS: no se ejecutaron porque la compilación falló por el cambio");
    }

    /// <summary>
    /// Un <c>MSB4019</c> —el <c>Microsoft.Cpp.Default.props</c> que solo trae Visual Studio— no es
    /// un error del cambio ni de nadie: es dotnet diciendo que ese proyecto no es suyo.
    /// </summary>
    [Fact]
    public void El_error_del_toolset_de_C_mas_mas_no_cuenta_como_fallo()
    {
        string msb = $"{_clone}\\Native\\CCCoreWrapper.vcxproj(1,1): error MSB4019: "
            + "No se encontró el proyecto importado \"Microsoft.Cpp.Default.props\"";
        var runner = new ScriptedProcess()
            .Then(1, msb)
            .Then(0, "Aprobadas: 42");

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: false, fullSolution: true), default);

        verdict.NewErrors.Should().Be(0);
        verdict.Ok.Should().BeTrue();
        verdict.Excluded.Should().Contain("Native/CCCoreWrapper.vcxproj");
        verdict.Summary.Should().Contain("Fuera del alcance").And.Contain("no cuentan");
    }

    // ================================================================= la caché

    /// <summary>
    /// Medir la línea base cuesta una compilación entera, así que se guarda por (objetivo, commit).
    /// La precondición del arreglo asistido —árbol limpio— es lo que la hace reutilizable: sobre el
    /// mismo commit, lo que la solución hacía antes de tocarla es lo mismo para todos.
    /// </summary>
    [Fact]
    public void La_linea_base_se_mide_una_vez_por_commit_y_se_reutiliza()
    {
        var first = new ScriptedProcess().Then(1, Errors(18)).Then(1, Errors(18)).Then(0, "ok");
        Runner(first).Run(Request(pristine: true, fullSolution: true), default);
        first.Calls.Count(c => c.Args.StartsWith("build")).Should().Be(2, "midió la base y compiló");

        var second = new ScriptedProcess().Then(1, Errors(18)).Then(0, "ok");
        BuildVerdict verdict = Runner(second).Run(Request(pristine: true, fullSolution: true), default);

        second.Calls.Count(c => c.Args.StartsWith("build")).Should().Be(1, "la base salió de la caché");
        verdict.PreexistingErrors.Should().Be(18);
        verdict.HasBaseline.Should().BeTrue();
    }

    /// <summary>Otro commit es otro código: la línea base guardada no vale y se vuelve a medir.</summary>
    [Fact]
    public void Cambiar_de_commit_invalida_la_linea_base()
    {
        Runner(new ScriptedProcess().Then(1, Errors(18)).Then(1, Errors(18)).Then(0, "ok"))
            .Run(Request(pristine: true, fullSolution: true), default);

        var other = new ScriptedProcess().Then(1, Errors(18)).Then(1, Errors(18)).Then(0, "ok");
        Runner(other).Run(
            Request(pristine: true, fullSolution: true) with { Commit = "ffffffff" }, default);

        other.Calls.Count(c => c.Args.StartsWith("build")).Should().Be(2);
    }

    /// <summary>
    /// Con el árbol ya tocado NO se puede medir una línea base: lo que se compilara llevaría el
    /// cambio dentro. Entonces se cuenta todo como nuevo y se DICE, que es lo contrario de
    /// inventarse una base.
    /// </summary>
    [Fact]
    public void Con_el_arbol_ya_tocado_no_se_inventa_una_linea_base()
    {
        var runner = new ScriptedProcess().Then(1, Errors(18));

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: false, fullSolution: true), default);

        runner.Calls.Count(c => c.Args.StartsWith("build")).Should().Be(1, "no hay base que medir");
        verdict.HasBaseline.Should().BeFalse();
        verdict.NewErrors.Should().Be(18);
        verdict.Summary.Should().Contain("Sin línea base para este commit");
    }

    // ================================================================= lo que se ejecuta

    /// <summary>
    /// Con ámbito de proyecto se compila ESE proyecto y se pasan SUS tests. Ni la solución, ni
    /// «dotnet test» a secas sobre todo lo que haya.
    /// </summary>
    [Fact]
    public void Con_ambito_de_proyecto_se_compila_el_proyecto_y_se_pasan_sus_tests()
    {
        var runner = new ScriptedProcess().Then(0, "Compilación correcta.").Then(0, "Aprobadas: 7");

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: false), default);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Args.Should().StartWith("build").And.Contain("Common.csproj");
        runner.Calls[1].Args.Should().StartWith("test").And.Contain("Common.Tests.csproj");
        verdict.Ok.Should().BeTrue();
        verdict.TestsRun.Should().BeTrue();
        verdict.TestsOk.Should().BeTrue();
    }

    /// <summary>
    /// Un proyecto sin tests no se maquilla: compila, pero nadie lo prueba, y el agente tiene que
    /// decirlo en su resumen.
    /// </summary>
    [Fact]
    public void Un_proyecto_sin_tests_lo_dice_en_vez_de_fingir_que_pasan()
    {
        Directory.Delete(Path.Combine(_clone, "Tests"), recursive: true);
        var runner = new ScriptedProcess().Then(0, "Compilación correcta.");

        BuildVerdict verdict = Runner(runner).Run(Request(pristine: false), default);

        verdict.Ok.Should().BeTrue();
        verdict.TestsRun.Should().BeFalse();
        verdict.Summary.Should().Contain("no hay ningún proyecto de test");
    }

    // ================================================================= la narración

    /// <summary>Lo que se canta en la conversación: verde con matices, nunca un rojo prestado.</summary>
    [Fact]
    public void La_conversacion_no_canta_rojo_por_errores_que_ya_estaban()
    {
        string line = LiveFixService.Narrate(new BuildVerdict(
            true, "…", TargetLabel: "la solución XBlast.sln",
            NewErrors: 0, PreexistingErrors: 18, TestsRun: true, TestsOk: true));

        line.Should().Contain("0 errores nuevos");
        line.Should().Contain("18 error(es) preexistente(s)");
        line.Should().Contain("ya fallaban antes del cambio");
    }

    // ================================================================= plomería

    private BuildRunner Runner(IProcessRunner process)
        => new(process, TimeSpan.FromMinutes(1), new BuildBaselineStore(_paths));

    private BuildRequest Request(bool pristine, bool fullSolution = false)
        => new(_clone, new[] { Touched }, fullSolution, "abc1234", pristine);

    /// <summary>Los 18 errores heredados del caso real, en el formato en el que los escribe dotnet.</summary>
    private string Errors(int count)
        => string.Join("\r\n", Enumerable.Range(1, count).Select(i =>
            $"{_clone}\\Legacy\\Viejo{i}.cs({i},5): error CS0246: No se encontró el tipo o el espacio "
            + $"de nombres 'Falta{i}' [{_clone}\\Legacy\\Legacy.csproj]"));

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_clone, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Un <see cref="IProcessRunner"/> con guion: cada llamada devuelve el siguiente resultado
    /// preparado. Es lo que permite ejercitar la línea base —que son DOS compilaciones de la misma
    /// cosa con resultados distintos— sin compilar nada.
    /// </summary>
    private sealed class ScriptedProcess : IProcessRunner
    {
        private readonly Queue<(int Code, string Output)> _script = new();

        public List<(string File, string Args)> Calls { get; } = new();

        public ScriptedProcess Then(int exitCode, string output)
        {
            _script.Enqueue((exitCode, output));
            return this;
        }

        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, arguments));
            return _script.Count > 0
                ? new ProcessOutcome(_script.Dequeue() is var (code, output) ? code : 0, output, false)
                : new ProcessOutcome(0, "Compilación correcta.", false);
        }
    }
}
