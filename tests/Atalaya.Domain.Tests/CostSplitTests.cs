using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F20 §1 — <b>en qué se reparte el coste, por concepto</b>.
/// <para>
/// Los tokens engañan cuando las tarifas difieren doce veces entre sí. En la aceptación de F19 se
/// leyeron 119.583 tokens de caché y se escribieron 126.904 —números casi iguales— y lo escrito
/// costó <b>trece veces</b> lo leído: el 60 % de la factura contra el 5 %. Sin este desglose eso
/// hay que calcularlo a mano, y es exactamente lo que hubo que hacer para saber dónde apretar.
/// </para>
/// </summary>
public class CostSplitTests
{
    /// <summary>
    /// Las tarifas de Opus 4.7 con las que se reprodujo al credit el total de la aceptación de F19:
    /// 5 / 25 / 0,50 / 6,25 $ por millón (entrada / salida / lectura / escritura).
    /// </summary>
    private static ModelRateTable Opus() => new()
    {
        Source = "Tarifas de Opus usadas en la aceptación de F19.",
        Rates = { new ModelRate("opus", string.Empty, 5.00m, 25.00m, 0.50m, CacheWritePerMillion: 6.25m) },
    };

    private static CostResult Cost(long input, long output, long read, long write)
        => CostCalculator.Calculate("opus", "copilot", input, output, read, write, Opus());

    /// <summary>
    /// <b>El caso que ordenó la fase</b>, con los números reales de la aceptación de F19. La
    /// escritura de caché es el 60 % de la factura y la lectura el 5 %, con tokens casi iguales.
    /// </summary>
    [Fact]
    public void El_reparto_reproduce_la_aceptacion_de_F19()
    {
        // Copilot mete la caché dentro de la entrada (D-785): 246.541 de In contienen las dos.
        CostResult cost = Cost(input: 246_541, output: 18_139, read: 119_583, write: 126_904);

        CostSplit split = cost.Split!;

        // Los mismos números de la aceptación, en dólares: 79,3 credits son 0,793 $.
        split.CacheWrite.Should().BeApproximately(0.793m, 0.001m);
        split.Output.Should().BeApproximately(0.453m, 0.001m);
        split.Cached.Should().BeApproximately(0.060m, 0.001m);
        split.Fresh.Should().BeApproximately(0.0003m, 0.0005m, "casi todo lo de entrada vino de caché");

        split.ShareOf(split.CacheWrite).Should().BeApproximately(0.60, 0.02);
        split.ShareOf(split.Output).Should().BeApproximately(0.35, 0.02);
        split.ShareOf(split.Cached).Should().BeApproximately(0.05, 0.02);

        cost.Usd.Should().BeApproximately(1.307m, 0.005m, "el total de la aceptación, al céntimo");
    }

    /// <summary>
    /// <b>Una sola aritmética.</b> El reparto no es una segunda cuenta que pueda discrepar del
    /// total: es el total desglosado, y sumarlo tiene que dar exactamente lo mismo. Dos cuentas
    /// parecidas para el mismo número acaban discrepando — ya pasó dos veces entre el azulejo y la
    /// gráfica (D-788).
    /// </summary>
    [Theory]
    [InlineData(100_000, 5_000, 40_000, 30_000)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(500_000, 60_000, 400_000, 90_000)]
    public void El_reparto_suma_exactamente_el_total(long input, long output, long read, long write)
    {
        CostResult cost = Cost(input, output, read, write);

        cost.Split!.Total.Should().Be(cost.Usd!.Value);
    }

    /// <summary>
    /// Los conceptos que valen cero no salen: una fila a cero se lee como «se midió y salió cero»,
    /// que es otra cosa que «aquí no hubo nada».
    /// </summary>
    [Fact]
    public void Los_conceptos_a_cero_no_se_enumeran()
    {
        CostSplit split = Cost(input: 1_000, output: 500, read: 0, write: 0).Split!;

        split.Items.Should().HaveCount(2);
        split.Items.Select(i => i.Concepto).Should().Equal("salida", "entrada fresca");
    }

    /// <summary>El orden es FIJO, para que dos informes se comparen de un vistazo.</summary>
    [Fact]
    public void El_orden_de_los_conceptos_no_depende_de_su_tamano()
    {
        Cost(9_000, 1, 1_000, 1_000).Split!.Items.Select(i => i.Concepto)
            .Should().Equal("escritura de caché", "salida", "lectura de caché", "entrada fresca");
    }

    /// <summary>
    /// Sin coste no hay reparto. Un modelo sin tarifa para su casa o una sesión sin tokens no
    /// producen un desglose de ceros: no producen desglose.
    /// </summary>
    [Fact]
    public void Sin_coste_no_hay_reparto()
    {
        var deCopilot = new ModelRateTable
        {
            Rates = { new ModelRate("opus", "copilot", 5.00m, 25.00m, 0.50m, CacheWritePerMillion: 6.25m) },
        };

        CostCalculator.Calculate("opus", "otra-casa", 1000, 100, 0, 0, deCopilot).Split.Should().BeNull();
        CostCalculator.Calculate("desconocido", "copilot", 1000, 100, 0, 0, Opus()).Split.Should().BeNull();
        CostCalculator.Calculate("opus", "copilot", 0, 0, 0, 0, Opus()).Split.Should().BeNull();
    }

    /// <summary>
    /// Un modelo que NO cobra la escritura aparte no puede producir un concepto «escritura»: esos
    /// tokens son entrada normal, y separarlos inventaría un cobro que nadie hace (D-786).
    /// </summary>
    [Fact]
    public void Sin_tarifa_de_escritura_no_hay_concepto_de_escritura()
    {
        var sinEscritura = new ModelRateTable
        {
            Rates = { new ModelRate("llano", string.Empty, 5.00m, 25.00m, 0.50m) },
        };

        CostSplit split = CostCalculator
            .Calculate("llano", "copilot", 100_000, 1_000, 20_000, 30_000, sinEscritura).Split!;

        split.CacheWrite.Should().Be(0m);
        split.Items.Should().NotContain(i => i.Concepto.Contains("escritura"));
    }
}
