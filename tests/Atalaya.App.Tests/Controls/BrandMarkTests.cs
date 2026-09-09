using System.Reflection;
using System.Windows.Media;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests.Controls;

/// <summary>
/// MEJ-0013 — el helper que fabrica la placa. Se prueba por reflexión a propósito: es privado,
/// y lo que hay que blindar es justo su NOMBRE, porque llamarlo <c>Freeze</c> lo confundía con
/// <see cref="System.Windows.Freezable.Freeze"/>, que no fabrica nada ni devuelve nada.
/// </summary>
public class BrandMarkTests
{
    private static MethodInfo? Helper(string name)
        => typeof(BrandMark).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    [Fact]
    public void El_helper_se_llama_FrozenBrush_y_no_Freeze()
    {
        Helper("FrozenBrush").Should().NotBeNull("la fábrica del pincel se nombra por lo que devuelve");

        typeof(BrandMark)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Should().NotContain(m => m.Name == "Freeze", "ese nombre choca con Freezable.Freeze()");
    }

    [Fact]
    public void FrozenBrush_devuelve_el_color_pedido_ya_congelado()
    {
        var brush = Helper("FrozenBrush")!.Invoke(null, new object[] { "#F4F5F7" });

        brush.Should().BeOfType<SolidColorBrush>()
            .Which.Color.Should().Be(Color.FromRgb(0xF4, 0xF5, 0xF7));
        ((SolidColorBrush)brush!).IsFrozen.Should().BeTrue("un recurso compartido entre hilos va congelado");
    }
}
