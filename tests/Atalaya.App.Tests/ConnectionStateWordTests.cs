using System.Text.RegularExpressions;
using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte C — <b>cada estado de conexión se dice con icono, COLOR y PALABRA</b>.
/// <para>
/// <b>De dónde viene.</b> Cuenta pintaba sus estados con un glifo suelto —«✓», «✕», «…», «•»— y
/// dos <i>converters</i> con los hexadecimales dentro. Dos defectos en uno: «…» y «•» no
/// significan nada para quien no los escribió, y un pincel devuelto por un converter está YA
/// RESUELTO, así que al cambiar de tema la lista se queda con los colores del anterior (D-971).
/// Ahora los tres los pone el sistema con <c>DataTrigger</c> y <c>DynamicResource</c>.
/// </para>
/// <para>
/// <b>Por qué se rompe en silencio.</b> Un <c>DataTrigger</c> que falta no falla: el estado cae al
/// valor por defecto del estilo. Añadir un estado nuevo a <see cref="CheckState"/> sin añadir su
/// disparador deja una fila que dice «No comprobado» en gris para siempre — con la comprobación
/// hecha, verde y delante. Es la peor clase de error de interfaz: uno que se lee bien y miente.
/// </para>
/// </summary>
public sealed class ConnectionStateWordTests
{
    /// <summary>
    /// Los estados que se PINTAN. <see cref="CheckState.Skipped"/> queda fuera a propósito: no
    /// aplica en este despliegue y su fila no se enseña (<c>ConnectionStep.IsVisible</c>), así que
    /// exigirle palabra sería exigir un rótulo para algo que nadie ve.
    /// </summary>
    public static TheoryData<CheckState> Visibles => new()
    {
        CheckState.Pending, CheckState.Running, CheckState.Ok, CheckState.Failed, CheckState.Optional,
    };

    [Theory]
    [MemberData(nameof(Visibles))]
    public void Cada_estado_visible_tiene_su_palabra(CheckState state)
    {
        string estilo = Style("Check.Word");

        // `Pending` es el valor por defecto del estilo —«No comprobado»— y por eso no tiene
        // disparador propio: es el que sale cuando ninguno se cumple.
        if (state == CheckState.Pending)
        {
            estilo.Should().Contain("Value=\"No comprobado\"",
                "el estado en reposo lleva la palabra puesta por defecto, no un hueco");
            return;
        }

        estilo.Should().MatchRegex(
            $"Value=\"{state}\"[\\s\\S]{{0,400}}?Property=\"Text\"",
            $"«{state}» tiene que decir QUÉ es, no solo pintarse de un color");
    }

    [Theory]
    [MemberData(nameof(Visibles))]
    public void Cada_estado_visible_tiene_su_color_del_tema(CheckState state)
    {
        string estilo = Style("Check.Word");

        if (state == CheckState.Pending)
        {
            estilo.Should().Contain("Property=\"Foreground\" Value=\"{DynamicResource Brush.TextFaint}\"");
            return;
        }

        estilo.Should().MatchRegex(
            $"Value=\"{state}\"[\\s\\S]{{0,400}}?Property=\"Foreground\" Value=\"{{DynamicResource ",
            $"«{state}» toma su color de la paleta, no de un converter que lo congela");
    }

    /// <summary>
    /// Y el glifo. «Comprobando» es la excepción y está declarada: no lleva icono sino anillo,
    /// porque lo que está pasando se mueve.
    /// </summary>
    [Theory]
    [MemberData(nameof(Visibles))]
    public void Cada_estado_visible_tiene_su_icono(CheckState state)
    {
        string glifo = Style("Check.Glyph");

        if (state == CheckState.Running)
        {
            glifo.Should().MatchRegex(
                "Value=\"Running\"[\\s\\S]{0,300}?Property=\"Visibility\" Value=\"Collapsed\"",
                "comprobando no lleva glifo: lleva anillo, que es lo que se mueve");
            Style("Check.Spinner").Should().Contain("Value=\"Running\"");
            return;
        }

        if (state == CheckState.Pending)
        {
            glifo.Should().Contain("Property=\"Data\" Value=\"{x:Static c:Icons.Pending}\"");
            return;
        }

        glifo.Should().MatchRegex(
            $"Value=\"{state}\"[\\s\\S]{{0,300}}?Property=\"Data\" Value=\"{{x:Static c:Icons\\.",
            $"«{state}» tiene icono propio");
    }

    /// <summary>
    /// Los dos converters que hacían esto ya no existen. Se comprueba porque un converter muerto
    /// que sigue registrado vuelve solo: alguien lo encuentra, lo usa, y el tema deja de alcanzar
    /// esa vista otra vez.
    /// </summary>
    [Fact]
    public void Los_converters_de_estado_ya_no_existen()
    {
        string converters = File.ReadAllText(
            Path.Combine(Root(), "src", "Atalaya.App", "Converters.cs"));
        string diccionario = File.ReadAllText(
            Path.Combine(Root(), "src", "Atalaya.App", "Themes", "Converters.xaml"));

        converters.Should().NotContain("CheckStateToBrushConverter");
        converters.Should().NotContain("CheckStateToGlyphConverter");
        diccionario.Should().NotContain("CheckStateToBrush").And.NotContain("CheckStateToGlyph");
    }

    private static string Style(string key)
    {
        string styles = File.ReadAllText(
            Path.Combine(Root(), "src", "Atalaya.App", "Themes", "Styles.xaml"));

        int start = styles.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"`Styles.xaml` declara {key}");

        int end = styles.IndexOf("</Style>", start, StringComparison.Ordinal);
        return styles[start..(end < 0 ? styles.Length : end)];
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
