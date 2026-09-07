using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R13 — el editor que eliges es el que abre, y abre en la línea del hallazgo.
/// <para>
/// Todo lo de aquí es <b>función pura sobre el registro</b>, sin ningún editor instalado: el patrón
/// que D-208 estrenó con el tope de tiempo. Un test que necesitara Visual Studio en la máquina de
/// build no protegería nada, porque no se ejecutaría nunca.
/// </para>
/// </summary>
public sealed class EditorRegistryTests
{
    private const string File1 = @"C:\clon\src\Motor.cs";

    /// <summary>
    /// <b>La regla del parte (a).</b> Con el ajuste en X, el comando construido es el de X — para
    /// TODOS los editores del registro, no para los dos que había en el <c>if</c>. El defecto era
    /// justo esto: se elegía Visual Studio y abría otra cosa.
    /// </summary>
    [Fact]
    public void Cada_editor_construye_SU_comando_y_no_el_de_otro()
    {
        var built = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (EditorDefinition editor in EditorRegistry.All)
        {
            EditorCommand command = EditorRegistry.Build(editor, FakeExe(editor), File1, line: 142);

            command.Ok.Should().BeTrue($"«{editor.Name}» tiene que poder construir su comando");
            built[editor.Id] = command.Display;
        }

        built[EditorRegistry.VisualStudioId].Should().Contain("devenv").And.Contain("/Edit");
        built["vscode"].Should().Contain("-g").And.Contain("Motor.cs:142:1");
        built["notepadpp"].Should().Contain("-n142");
        built["rider"].Should().Contain("rider64").And.Contain("--line 142");
        built["studio"].Should().Contain("studio64").And.Contain("--line 142");
        built["idea"].Should().Contain("idea64").And.Contain("--line 142");
        built["netbeans"].Should().Contain("--open").And.Contain("Motor.cs:142");
        built["sublime"].Should().Contain("Motor.cs:142:1");
        built[EditorRegistry.SystemId].Should().Contain("Motor.cs");

        // Y ninguno construye el comando de otro: dos editores distintos, dos comandos distintos.
        built.Values.Distinct().Should().HaveCount(built.Count);
    }

    /// <summary>
    /// <b>Todo editor declara sintaxis de línea o declara que no la tiene</b> — ninguno cae en un
    /// caso por defecto silencioso (misma idea que el test de claves huérfanas de D-1021).
    /// <para>
    /// Es la regla que faltaba: Visual Studio abría sin línea porque era el <c>else</c> de un
    /// <c>if</c>, no porque nadie hubiera decidido que no la admite.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_editor_se_queda_sin_declarar_su_sintaxis_de_linea()
    {
        foreach (EditorDefinition editor in EditorRegistry.All)
        {
            if (editor.Line.Supported)
            {
                editor.Line.Template.Should().Contain(EditorRegistry.FilePlaceholder,
                    $"«{editor.Name}» dice que admite línea: su plantilla tiene que decir qué fichero");
                editor.Line.WhyNot.Should().BeNull();
                continue;
            }

            editor.Line.WhyNot.Should().NotBeNullOrWhiteSpace(
                $"«{editor.Name}» no admite línea, así que tiene que decir POR QUÉ: es lo que sale "
                + "en el toast en vez de un salto que no ha ocurrido");
        }
    }

    /// <summary>Visual Studio abre sin línea, y lo dice. No se finge un salto que no existe.</summary>
    [Fact]
    public void Visual_Studio_abre_sin_linea_y_lo_declara()
    {
        EditorDefinition vs = EditorRegistry.Find(EditorRegistry.VisualStudioId)!;
        EditorCommand command = EditorRegistry.Build(vs, @"C:\vs\devenv.exe", File1, line: 142);

        command.CarriesLine.Should().BeFalse();
        command.Arguments.Should().NotContain("142");
        command.NoLineReason.Should().Be("no lo permite desde la línea de comandos");
    }

    /// <summary>Un ejecutable de mentira: aquí no hay ningún editor instalado, ni hace falta.</summary>
    private static string? FakeExe(EditorDefinition editor)
        => editor.Kind == EditorKind.Program ? @"C:\fake\" + editor.Executables[0] : null;
}
