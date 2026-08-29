using Atalaya.Domain;
using Atalaya.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F10 §1 — los pesos de la deuda y la densidad, que son la definición entera de la métrica.
/// <para>
/// No se prueba «que la suma sume»: se prueba la propiedad que hace útil la escala —que una
/// crítica no se pueda diluir en un montón de bajas— y que una unidad sin líneas no tenga
/// densidad, porque ahí el 0 sería una división inventada.
/// </para>
/// </summary>
public sealed class DebtWeightsTests
{
    [Theory]
    [InlineData(Severity.Critica, 10)]
    [InlineData(Severity.Alta, 5)]
    [InlineData(Severity.Media, 2)]
    [InlineData(Severity.Baja, 1)]
    public void Cada_severidad_pesa_lo_que_dice_la_rubrica(Severity severity, int weight)
        => DebtWeights.Of(severity).Should().Be(weight);

    /// <summary>
    /// La razón de ser de la escala geométrica: nueve bajas no llegan a una crítica. Con pesos
    /// 4·3·2·1 sí llegarían, y el mapa mandaría atacar el módulo con más ruido en vez del que
    /// tiene el problema grave.
    /// </summary>
    [Fact]
    public void Una_critica_no_se_diluye_en_un_monton_de_bajas()
    {
        var nueveBajas = Enumerable.Repeat(Severity.Baja, 9);
        var cuatroMedias = Enumerable.Repeat(Severity.Media, 4);

        DebtWeights.Sum(nueveBajas).Should().BeLessThan(DebtWeights.Of(Severity.Critica));
        DebtWeights.Sum(cuatroMedias).Should().BeLessThan(DebtWeights.Of(Severity.Critica));

        // Y el umbral es exactamente ese: diez bajas, cinco medias o dos altas la igualan.
        DebtWeights.Sum(Enumerable.Repeat(Severity.Baja, 10)).Should().Be(DebtWeights.Of(Severity.Critica));
        DebtWeights.Sum(Enumerable.Repeat(Severity.Media, 5)).Should().Be(DebtWeights.Of(Severity.Critica));
        DebtWeights.Sum(Enumerable.Repeat(Severity.Alta, 2)).Should().Be(DebtWeights.Of(Severity.Critica));
    }

    [Fact]
    public void La_deuda_es_la_suma_de_los_pesos_sin_tope_ni_media()
    {
        var mezcla = new[] { Severity.Critica, Severity.Alta, Severity.Media, Severity.Media, Severity.Baja };

        DebtWeights.Sum(mezcla).Should().Be(10 + 5 + 2 + 2 + 1);
        DebtWeights.Sum(Array.Empty<Severity>()).Should().Be(0);
    }

    /// <summary>La densidad es por MIL líneas, que es la unidad en la que se leen los umbrales.</summary>
    [Fact]
    public void La_densidad_es_deuda_por_cada_mil_lineas()
    {
        DebtWeights.Density(10, 1000).Should().Be(10);
        DebtWeights.Density(10, 500).Should().Be(20);
        DebtWeights.Density(39, 189).Should().BeApproximately(206.3, 0.1);
    }

    /// <summary>
    /// Sin líneas no hay densidad. Devolver 0 diría «está limpia» de algo que no se ha podido
    /// medir — el mismo error que pintar de frío una unidad sin auditar.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sin_lineas_que_dividir_la_densidad_no_existe(int loc)
        => DebtWeights.Density(5, loc).Should().BeNull();

    /// <summary>Una unidad medida y sin deuda SÍ tiene densidad, y vale cero. No es lo mismo.</summary>
    [Fact]
    public void Cero_deuda_sobre_lineas_medidas_es_densidad_cero_y_no_desconocida()
        => DebtWeights.Density(0, 400).Should().Be(0);
}
