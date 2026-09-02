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
    /// <b>Claude Code la excluye</b>, y eso sigue escrito aunque ya no se use para cobrar nada.
    /// Verificado en una sesión real: <c>input_tokens</c> 6 con <c>cache_read_input_tokens</c>
    /// 19.990 — un 6 no puede contener a 19.990.
    /// <para>
    /// Desde F16-RETOQUE §1 esa casa no se tarifa, así que la fórmula no llega a aplicarse; lo que
    /// se fija aquí es el <b>mapa</b>, que es donde vive el conocimiento. El día que aparezca un
    /// proveedor que facture y cuente los tokens de esta manera, la semántica tiene que seguir
    /// estando bien descrita — costó medirla y tirarla sería tirar la medida.
    /// </para>
    /// </summary>
    [Fact]
    public void La_semantica_de_cada_casa_sigue_escrita_aunque_una_ya_no_se_tarife()
    {
        CreditCalculator.AccountingOf("copilot").Should().Be(TokenAccounting.InputIncludesCache);
        CreditCalculator.AccountingOf(null).Should().Be(TokenAccounting.InputIncludesCache);
        CreditCalculator.AccountingOf("claude-code").Should().Be(TokenAccounting.InputExcludesCache);

        // Y un proveedor que esta versión no conoce se supone como el histórico: el de Copilot.
        CreditCalculator.AccountingOf("proveedor-de-2027").Should().Be(TokenAccounting.InputIncludesCache);
    }

    /// <summary>
    /// <b>La casa que no factura se para en la puerta</b> (F16-RETOQUE §1). El consumo de Claude
    /// Code va contra la suscripción personal de quien lo usa, así que no hay factura que calcular:
    /// da igual que su modelo tenga tarifa en la tabla y que haya tokens de sobra — el resultado es
    /// «no se tarifa», y nunca un número ni uno de los tres motivos de «falta algo».
    /// <para>
    /// Se comprueba aquí, en el cálculo, porque es el embudo por el que pasan el pie, el informe,
    /// la lista de informes y las cifras de Métricas. Una regla puesta en cualquier otro sitio
    /// habría que recordarla N veces.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_casa_que_no_factura_no_se_tarifa_aunque_su_modelo_tenga_tarifa()
    {
        ModelRateTable rates = Table(new ModelRate("m", 10.00m, 0m, 1.00m));

        CostResult claude = CreditCalculator.Calculate("m", "claude-code", 1_000_000, 0, 900_000, 0, rates);

        claude.HasValue.Should().BeFalse();
        claude.Why.Should().Be(CostUnavailable.NotBilled);
        claude.Credits.Should().BeNull();

        // Y los MISMOS números por la casa que sí factura sí dan coste: lo que cambia es quién paga.
        CreditCalculator.Calculate("m", "copilot", 1_000_000, 0, 900_000, 0, rates)
            .Credits.Should().Be(190m);
    }

    /// <summary>Quién factura, dicho en una línea: todas menos la que corre contra una suscripción.</summary>
    [Theory]
    [InlineData("copilot", true)]
    [InlineData(null, true)]                  // antes de F14 solo había Copilot
    [InlineData("", true)]
    [InlineData("proveedor-de-2027", true)]   // suposición conservadora: su gasto se ve
    [InlineData("claude-code", false)]
    [InlineData("Claude-Code", false)]        // el identificador se casa sin distinguir mayúsculas
    public void Quien_factura_y_quien_no(string? provider, bool billed)
        => CreditCalculator.IsBilled(provider).Should().Be(billed);

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
    /// Una tarifa atada a un proveedor gana a la genérica: el mismo modelo puede costar distinto
    /// según quién lo revenda. El mecanismo se queda aunque hoy solo facture una casa — el día que
    /// facturen dos, es lo único que impide elegir una de las dos tarifas y equivocarse con la otra.
    /// </summary>
    [Fact]
    public void La_tarifa_del_proveedor_gana_a_la_generica()
    {
        var rates = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("claude-sonnet-5", 2m, 10m, 0.2m, 2.50m),
                new ModelRate("claude-sonnet-5", 2m, 10m, 0.2m, 4.00m, Provider: "otra-reventa"),
            },
        };

        rates.Find("claude-sonnet-5", "copilot")!.CacheWritePerMillion.Should().Be(2.50m);
        rates.Find("claude-sonnet-5", "otra-reventa")!.CacheWritePerMillion.Should().Be(4.00m);
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
    /// <b>La siembra es solo de lo que FACTURA</b> (F16-RETOQUE §1). Traía cuatro tarifas atadas a
    /// <c>claude-code</c>, bien medidas —reproducían al sexto decimal el coste que el propio CLI
    /// calcula— y se retiraron igual: la pregunta no era si el número salía, era quién paga.
    /// Mantener a mano una copia de la lista de precios de Anthropic para un consumo que nadie
    /// factura es un dato que caduca solo.
    /// <para>
    /// Los modelos de Anthropic que <b>sí</b> siguen en la tabla son los que Copilot revende: ésos
    /// los paga la organización, con la tarifa que publica GitHub. Por eso no vale con buscar
    /// «claude» — lo que no puede haber es una tarifa de una casa que no factura.
    /// </para>
    /// </summary>
    [Fact]
    public void La_siembra_no_trae_tarifas_de_una_casa_que_no_factura()
    {
        ModelRateTable seed = ModelRateSeed.Create();

        seed.Rates.Should().OnlyContain(r => CreditCalculator.IsBilled(r.Provider));

        // Y lo de Copilot NO se toca: sus modelos de Anthropic son factura de verdad.
        seed.Find("claude-opus-5", "copilot").Should().NotBeNull();
        seed.Find("claude-sonnet-4.5", "copilot").Should().NotBeNull();
        seed.Find("gpt-5.4", "copilot").Should().NotBeNull();
    }

    private static ModelRateTable Table(params ModelRate[] rates)
        => new() { Rates = rates.ToList() };
}
