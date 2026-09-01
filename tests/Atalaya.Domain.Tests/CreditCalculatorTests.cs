using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F15 — el coste en AI credits, que es la unidad en la que GitHub factura desde el 1 de junio de
/// 2026.
/// <para>
/// Lo que se vigila aquí no es «que sume»: es que <b>la caché no se cuente dos veces ni ninguna</b>
/// —los dos proveedores la informan al revés el uno del otro— y que <b>el modelo salga del registro
/// de la sesión y jamás se asuma</b>. Un error en cualquiera de las dos cosas desvía todos los
/// costes de todos los paneles a la vez, y sin síntoma visible.
/// </para>
/// </summary>
public sealed class CreditCalculatorTests
{
    // ================================================================ el caso de control

    /// <summary>
    /// El caso numérico de control, con datos de una sesión REAL del hub y las tarifas de un
    /// modelo con caché barata: In 538.468 / Out 42.371 / CacheRead 368.618 a 1,25 / 10 / 0,125
    /// $/M debe dar ≈ 68,2 credits.
    /// <para>
    /// Copilot informa la entrada INCLUYENDO la caché, así que lo que se factura a tarifa plena es
    /// <c>In − CacheRead</c> = 169.850. Contarla dos veces daría 74,0 credits; no descontarla,
    /// 719,3. Por eso este número concreto vale como control.
    /// </para>
    /// </summary>
    [Fact]
    public void El_caso_de_control_da_68_2_credits()
    {
        CostResult cost = CreditCalculator.Calculate(
            "gpt-x", "copilot",
            inputTokens: 538_468, outputTokens: 42_371,
            cacheReadTokens: 368_618, cacheWriteTokens: 0,
            Table(new ModelRate("gpt-x", 1.25m, 10.00m, 0.125m)));

        cost.Credits.Should().BeApproximately(68.21m, 0.01m);
        cost.BillableInputTokens.Should().Be(169_850, "la entrada de Copilot incluye lo cacheado");
        cost.Usd.Should().BeApproximately(0.6821m, 0.0001m);
    }

    // ================================================================ semántica de la caché

    /// <summary>
    /// <b>Copilot incluye la caché en la entrada.</b> Verificado en una sesión real: In 538.468,
    /// CacheRead 368.618 y CacheWrite 169.826, y 538.468 − 368.618 = 169.850 ≈ CacheWrite.
    /// </summary>
    [Fact]
    public void Con_Copilot_la_entrada_incluye_la_cache_y_se_descuenta()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 900_000, cacheWriteTokens: 0,
            Table(new ModelRate("m", 10.00m, 0m, 1.00m)));

        // 100.000 a 10 $/M = 1 $ · 900.000 a 1 $/M = 0,9 $ → 1,9 $ = 190 credits
        cost.Credits.Should().Be(190m);
        cost.BillableInputTokens.Should().Be(100_000);
    }

    /// <summary>
    /// <b>Claude Code la excluye.</b> Verificado en una sesión real: <c>input_tokens</c> 6 con
    /// <c>cache_read_input_tokens</c> 19.990 — un 6 no puede contener a 19.990.
    /// </summary>
    [Fact]
    public void Con_Claude_Code_la_entrada_excluye_la_cache_y_no_se_descuenta()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "claude-code",
            inputTokens: 100_000, outputTokens: 0,
            cacheReadTokens: 900_000, cacheWriteTokens: 0,
            Table(new ModelRate("m", 10.00m, 0m, 1.00m)));

        cost.Credits.Should().Be(190m, "los mismos 100.000 facturables, pero ya venían aparte");
        cost.BillableInputTokens.Should().Be(100_000);
    }

    /// <summary>
    /// La prueba de que la semántica importa: los MISMOS números con las MISMAS tarifas dan costes
    /// distintos según quién los informe. Si algún día alguien unifica los dos caminos, este test
    /// es el que se pone rojo.
    /// </summary>
    [Fact]
    public void La_misma_sesion_cuesta_distinto_segun_quien_cuente_los_tokens()
    {
        ModelRateTable rates = Table(new ModelRate("m", 10.00m, 0m, 1.00m));

        decimal copilot = CreditCalculator.Calculate("m", "copilot", 1_000_000, 0, 900_000, 0, rates).Credits!.Value;
        decimal claude = CreditCalculator.Calculate("m", "claude-code", 1_000_000, 0, 900_000, 0, rates).Credits!.Value;

        copilot.Should().Be(190m);
        claude.Should().Be(1_090m);
        claude.Should().NotBe(copilot);
    }

    /// <summary>
    /// La comprobación definitiva de la semántica de Anthropic: con los tokens que informó el CLI
    /// de Claude Code en una sesión real, esta fórmula reproduce <b>exactamente</b> el coste que el
    /// propio CLI calculó. Dos aritméticas independientes que coinciden hasta el sexto decimal.
    /// </summary>
    [Theory]
    // Haiku 4.5: 980 in / 19 out / sin caché — el CLI dijo 0,001075 $.
    [InlineData(980, 19, 0, 0, 1.00, 5.00, 0.10, 2.00, 0.001075)]
    // Sonnet 5: 6 in / 560 out / 19.990 leídos de caché / 10.374 escritos — el CLI dijo 0,051106 $.
    // La escritura va a 4 $/M porque Claude Code usa caché de UNA HORA (2 × la entrada), no la de
    // cinco minutos (2,50 $/M) que publica la tabla de GitHub.
    [InlineData(6, 560, 19_990, 10_374, 2.00, 10.00, 0.20, 4.00, 0.051106)]
    public void La_formula_reproduce_el_coste_que_el_propio_CLI_calculo(
        long input, long output, long cacheRead, long cacheWrite,
        double inRate, double outRate, double cachedRate, double writeRate,
        double expectedUsd)
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "claude-code", input, output, cacheRead, cacheWrite,
            Table(new ModelRate("m", (decimal)inRate, (decimal)outRate, (decimal)cachedRate, (decimal)writeRate)));

        cost.Usd.Should().BeApproximately((decimal)expectedUsd, 0.000001m);
    }

    // ================================================================ la escritura de caché

    /// <summary>
    /// Un modelo que NO cobra la escritura aparte (null, no cero): esos tokens son entrada normal y
    /// se quedan dentro del reparto. Null y cero no son lo mismo — cero afirmaría que escribir en
    /// caché es gratis, que es otra cosa y que nadie ha dicho.
    /// </summary>
    [Fact]
    public void Sin_tarifa_de_escritura_esos_tokens_son_entrada_normal()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 0, cacheWriteTokens: 400_000,
            Table(new ModelRate("m", 10.00m, 0m, 1.00m, CacheWritePerMillion: null)));

        cost.BillableInputTokens.Should().Be(1_000_000, "no se sacan del montón si no se cobran aparte");
        cost.Credits.Should().Be(1_000m, "1.000.000 de tokens a 10 $/M son 10 $, es decir 1.000 credits");
    }

    /// <summary>Y uno que sí la cobra: salen del montón de entrada y van a su tarifa.</summary>
    [Fact]
    public void Con_tarifa_de_escritura_esos_tokens_salen_del_monton_y_van_a_la_suya()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 0, cacheWriteTokens: 400_000,
            Table(new ModelRate("m", 10.00m, 0m, 1.00m, CacheWritePerMillion: 20.00m)));

        // 600.000 a 10 $/M = 6 $ · 400.000 a 20 $/M = 8 $ → 14 $ = 1.400 credits
        cost.BillableInputTokens.Should().Be(600_000);
        cost.Credits.Should().Be(1_400m);
    }

    /// <summary>
    /// Si las cachés suman más que la entrada, el supuesto no encaja con estos datos. Antes que
    /// emitir un coste NEGATIVO —que se propagaría a los agregados sin que nadie lo notara— se
    /// cobra cero por lo que no cuadra.
    /// </summary>
    [Fact]
    public void Nunca_sale_un_coste_negativo_aunque_los_numeros_no_cuadren()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "copilot",
            inputTokens: 100, outputTokens: 0,
            cacheReadTokens: 900_000, cacheWriteTokens: 0,
            Table(new ModelRate("m", 10.00m, 0m, 1.00m)));

        cost.BillableInputTokens.Should().Be(0);
        cost.Credits.Should().BeGreaterThanOrEqualTo(0m);
    }

    // ================================================================ el modelo manda

    /// <summary>
    /// <b>La misma sesión con dos tarifas distintas da dos costes distintos.</b> Es el corazón de
    /// F15: el modelo se lee del registro de CADA sesión, así que dos sesiones del mismo periodo
    /// con modelos distintos van cada una con el suyo.
    /// </summary>
    [Theory]
    // barato: 1.000.000 × 1 $/M + 200.000 × 5 $/M = 1 + 1 = 2 $ → 200 credits
    // caro:    1.000.000 × 5 $/M + 200.000 × 25 $/M = 5 + 5 = 10 $ → 1.000 credits
    [InlineData("barato", 1.00, 5.00, 200.0)]
    [InlineData("caro", 5.00, 25.00, 1000.0)]
    public void La_misma_sesion_cuesta_lo_que_diga_la_tarifa_de_SU_modelo(
        string model, double inRate, double outRate, double expectedCredits)
    {
        var rates = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("barato", 1.00m, 5.00m, 0.10m),
                new ModelRate("caro", 5.00m, 25.00m, 0.50m),
            },
        };

        CostResult cost = CreditCalculator.Calculate(
            model, "copilot",
            inputTokens: 1_000_000, outputTokens: 200_000,
            cacheReadTokens: 0, cacheWriteTokens: 0,
            rates);

        cost.Credits.Should().Be((decimal)expectedCredits);
        cost.Model.Should().Be(model);
    }

    /// <summary>Sin modelo registrado no se aplica la tarifa de otro «parecido»: se dice.</summary>
    [Fact]
    public void Sin_modelo_registrado_el_coste_es_no_aplicable()
    {
        CostResult cost = CreditCalculator.Calculate(
            null, "copilot", 1000, 100, 0, 0, Table(new ModelRate("m", 1m, 1m, 1m)));

        cost.HasValue.Should().BeFalse();
        cost.Why.Should().Be(CostUnavailable.ModelUnknown);
    }

    /// <summary>Con un modelo sin tarifa configurada, tampoco: se señala y no se inventa.</summary>
    [Fact]
    public void Con_un_modelo_sin_tarifa_el_coste_es_no_aplicable()
    {
        CostResult cost = CreditCalculator.Calculate(
            "modelo-nuevo", "copilot", 1000, 100, 0, 0, Table(new ModelRate("otro", 1m, 1m, 1m)));

        cost.HasValue.Should().BeFalse();
        cost.Why.Should().Be(CostUnavailable.RateMissing);
        cost.Model.Should().Be("modelo-nuevo");
    }

    /// <summary>Sin tabla ninguna —hub sin sembrar— es lo mismo: falta la tarifa.</summary>
    [Fact]
    public void Sin_tabla_de_tarifas_el_coste_es_no_aplicable()
        => CreditCalculator.Calculate("m", "copilot", 1000, 100, 0, 0, rates: null)
            .Why.Should().Be(CostUnavailable.RateMissing);

    /// <summary>Y sin tokens no hay nada que calcular: «—», jamás un cero.</summary>
    [Fact]
    public void Sin_tokens_registrados_no_se_inventa_un_cero()
    {
        CostResult cost = CreditCalculator.Calculate(
            "m", "copilot", 0, 0, 0, 0, Table(new ModelRate("m", 1m, 1m, 1m)));

        cost.HasValue.Should().BeFalse();
        cost.Why.Should().Be(CostUnavailable.TokensMissing);
    }

    // ================================================================ la tabla

    /// <summary>
    /// Una tarifa atada a un proveedor gana a la genérica. Es lo que permite que Claude Sonnet 5
    /// cueste una cosa por Copilot (caché de 5 min) y otra por Claude Code (caché de 1 h).
    /// </summary>
    [Fact]
    public void La_tarifa_del_proveedor_gana_a_la_generica()
    {
        var rates = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("claude-sonnet-5", 2m, 10m, 0.2m, 2.50m),
                new ModelRate("claude-sonnet-5", 2m, 10m, 0.2m, 4.00m, Provider: "claude-code"),
            },
        };

        rates.Find("claude-sonnet-5", "copilot")!.CacheWritePerMillion.Should().Be(2.50m);
        rates.Find("claude-sonnet-5", "claude-code")!.CacheWritePerMillion.Should().Be(4.00m);
    }

    [Fact]
    public void El_modelo_se_casa_sin_distinguir_mayusculas()
        => Table(new ModelRate("GPT-5.4", 1m, 1m, 1m)).Find("gpt-5.4", "copilot").Should().NotBeNull();

    // ================================================================ la siembra

    /// <summary>
    /// La siembra trae tarifas de verdad y con fecha. No se comprueban aquí sus valores uno a uno
    /// —son datos de fuera y cambian—, sino que existe lo que la aplicación va a usar de serie y
    /// que nada nació sin fecha ni con un precio imposible.
    /// </summary>
    [Fact]
    public void La_siembra_trae_las_tarifas_con_su_fecha_y_su_procedencia()
    {
        ModelRateTable seed = ModelRateSeed.Create();

        seed.Rates.Should().NotBeEmpty();
        seed.ReviewedOn.Should().Be(ModelRateSeed.VerifiedOn);
        seed.Source.Should().Contain("docs.github.com");
        seed.Rates.Should().OnlyContain(r => r.EffectiveFrom != null);
        seed.Rates.Should().OnlyContain(r =>
            r.InputPerMillion >= 0 && r.OutputPerMillion >= 0 && r.CachedInputPerMillion >= 0);

        // Los promocionales llevan su caducidad escrita: cuando venzan, el precio sube.
        seed.Rates.Where(r => r.Note is { Length: > 0 } n && n.Contains("Promocional"))
            .Should().NotBeEmpty("hay tarifas con fecha de caducidad y hay que poder verlas");
    }

    /// <summary>
    /// Y los modelos que la aplicación ofrece de serie con Claude Code —sus alias resueltos— tienen
    /// tarifa sembrada. Sin esto, la primera auditoría con Claude Code saldría «tarifa no
    /// configurada» de fábrica.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4-5-20251001")]
    public void Los_modelos_que_resuelve_Claude_Code_nacen_con_tarifa(string model)
        => ModelRateSeed.Create().Find(model, "claude-code").Should().NotBeNull();

    private static ModelRateTable Table(params ModelRate[] rates)
        => new() { Rates = rates.ToList() };
}
