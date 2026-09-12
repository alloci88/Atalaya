using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F15 — el coste de unos tokens con la tarifa de su modelo. <b>En dólares desde PROV-2 §3</b>:
/// era el AI credit, la moneda de una casa, y dejó de valer en cuanto dos pueden gastar en el
/// mismo periodo. La equivalencia (1 credit = 0,01 $) no se ha perdido: vive en la
/// <c>ProviderBilling</c> de quien la usa y se aplica al enseñar la cifra, no al calcularla.
/// <para>
/// Lo que se vigila aquí no es «que sume»: es que <b>la caché no se cuente dos veces ni ninguna</b>
/// —los dos proveedores la informan al revés el uno del otro— y que <b>el modelo salga del registro
/// de la sesión y jamás se asuma</b>. Un error en cualquiera de las dos cosas desvía todos los
/// costes de todos los paneles a la vez, y sin síntoma visible.
/// </para>
/// </summary>
public sealed class CostCalculatorTests
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
    public void El_caso_de_control_da_0_68_dolares_que_son_68_2_credits()
    {
        CostResult cost = CostCalculator.Calculate(
            "gpt-x", "copilot",
            inputTokens: 538_468, outputTokens: 42_371,
            cacheReadTokens: 368_618, cacheWriteTokens: 0,
            Table(new ModelRate("gpt-x", string.Empty, 1.25m, 10.00m, 0.125m)));

        cost.BillableInputTokens.Should().Be(169_850, "la entrada de Copilot incluye lo cacheado");

        // La MISMA cifra de siempre, en la unidad nueva: 0,6821 $ son 68,21 credits a 0,01 $.
        // Que este número no se mueva es el punto entero de PROV-2 §3.
        cost.Usd.Should().BeApproximately(0.6821m, 0.0001m);
        (cost.Usd!.Value / 0.01m).Should().BeApproximately(68.21m, 0.01m);
    }

    // ================================================================ semántica de la caché

    /// <summary>
    /// <b>Copilot incluye la caché en la entrada.</b> Verificado en una sesión real: In 538.468,
    /// CacheRead 368.618 y CacheWrite 169.826, y 538.468 − 368.618 = 169.850 ≈ CacheWrite.
    /// </summary>
    [Fact]
    public void Con_Copilot_la_entrada_incluye_la_cache_y_se_descuenta()
    {
        CostResult cost = CostCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 900_000, cacheWriteTokens: 0,
            Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m)));

        // 100.000 a 10 $/M = 1 $ · 900.000 a 1 $/M = 0,9 $ → 1,9 $ (190 credits)
        cost.Usd.Should().Be(1.9m);
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
    public void La_semantica_de_entrada_la_trae_quien_la_declara_y_cambia_el_reparto()
    {
        ModelRateTable rates = Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m));

        // Los MISMOS tokens, con las dos formas de contar: 1.000.000 de entrada y 900.000 leídos
        // de caché. Con la caché dentro, a tarifa plena van 100.000; con la caché fuera, el millón
        // entero. Invertirlo desvía todos los costes, y por eso lo declara quien lo sabe.
        CostResult dentro = CostCalculator.Calculate(
            "m", "una-casa", 1_000_000, 0, 900_000, 0, rates,
            new ProviderCostTraits(Accounting: TokenAccounting.InputIncludesCache));

        CostResult fuera = CostCalculator.Calculate(
            "m", "una-casa", 1_000_000, 0, 900_000, 0, rates,
            new ProviderCostTraits(Accounting: TokenAccounting.InputExcludesCache));

        dentro.BillableInputTokens.Should().Be(100_000);
        fuera.BillableInputTokens.Should().Be(1_000_000);
        fuera.Usd.Should().BeGreaterThan(dentro.Usd!.Value);
    }

    /// <summary>
    /// Y sin que nadie declare nada se supone <b>la forma del histórico</b>: la entrada incluye la
    /// caché, que es con lo que se escribió todo lo que hay en el hub antes de que hubiera dos
    /// casas. Un valor por defecto distinto reinterpretaría meses de historia de golpe.
    /// </summary>
    [Fact]
    public void Sin_rasgos_se_supone_la_forma_del_historico()
        => CostCalculator.Calculate(
                "m", null, 1_000_000, 0, 900_000, 0,
                Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m)))
            .BillableInputTokens.Should().Be(100_000);

    /// <summary>
    /// <b>Lo que decide si un consumo se tarifa es que exista TARIFA para su casa y su modelo</b>
    /// (PROV-2 §3). Aquí vivía <c>IsBilled</c>, que lo decidía comparando el proveedor con una
    /// cadena literal y paraba a esa casa en la puerta antes de mirar tokens ni tarifas.
    /// <para>
    /// El comportamiento por defecto no cambia —a la casa que declara su frase no le sale un
    /// hueco, porque nadie le ha escrito tarifa— pero deja de ser una excepción escrita en el
    /// dominio: los mismos números, con una tarifa suya en la tabla, sí dan importe.
    /// </para>
    /// </summary>
    [Fact]
    public void Se_tarifa_lo_que_tiene_tarifa_para_su_proveedor_y_su_modelo()
    {
        // La tabla de la casa de fábrica, con su proveedor escrito: es lo que hay en un hub
        // migrado, y lo que hace que su tarifa no le ponga precio a otra casa.
        ModelRateTable rates = Table(new ModelRate("m", "copilot", 10.00m, 0m, 1.00m));

        var suscripcion = new ProviderCostTraits(
            "otra-casa", TokenAccounting.InputIncludesCache, "incluido en tu suscripción");

        CostResult otra = CostCalculator.Calculate(
            "m", "otra-casa", 1_000_000, 0, 900_000, 0, rates, suscripcion);

        otra.HasValue.Should().BeFalse();
        otra.Why.Should().Be(CostUnavailable.RateMissing);
        otra.IsUnpriced.Should().BeTrue("su casa dice qué se lee, así que no es un hueco");
        otra.NoRateNote.Should().Be("incluido en tu suscripción");

        // Los MISMOS números por la casa que sí tiene tarifa sí dan importe.
        CostCalculator.Calculate(
                "m", "copilot", 1_000_000, 0, 900_000, 0, rates,
                new ProviderCostTraits("copilot"))
            .Usd.Should().Be(1.9m);

        // Y en cuanto alguien escriba una tarifa para la otra casa, se tarifa como cualquiera.
        rates.Rates.Add(new ModelRate("m", "otra-casa", 10.00m, 0m, 1.00m));
        CostResult conTarifa = CostCalculator.Calculate(
            "m", "otra-casa", 1_000_000, 0, 900_000, 0, rates, suscripcion);

        conTarifa.Usd.Should().Be(1.9m);
        conTarifa.IsUnpriced.Should().BeFalse();
    }

    /// <summary>
    /// Y una casa que <b>no</b> declara ninguna frase sigue teniendo un hueco cuando le falta la
    /// tarifa: lo que se conserva es el texto de quien lo declara, no una exención para todos.
    /// </summary>
    [Fact]
    public void Sin_frase_declarada_un_modelo_sin_tarifa_sigue_siendo_un_hueco()
    {
        CostResult cost = CostCalculator.Calculate(
            "m", "casa-nueva", 1000, 100, 0, 0,
            Table(new ModelRate("m", "copilot", 1m, 1m, 1m)),
            new ProviderCostTraits("casa-nueva"));

        cost.Why.Should().Be(CostUnavailable.RateMissing);
        cost.IsUnpriced.Should().BeFalse();
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
        CostResult cost = CostCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 0, cacheWriteTokens: 400_000,
            Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m, CacheWritePerMillion: null)));

        cost.BillableInputTokens.Should().Be(1_000_000, "no se sacan del montón si no se cobran aparte");
        cost.Usd.Should().Be(10m, "1.000.000 de tokens a 10 $/M son 10 $ (1.000 credits)");
    }

    /// <summary>Y uno que sí la cobra: salen del montón de entrada y van a su tarifa.</summary>
    [Fact]
    public void Con_tarifa_de_escritura_esos_tokens_salen_del_monton_y_van_a_la_suya()
    {
        CostResult cost = CostCalculator.Calculate(
            "m", "copilot",
            inputTokens: 1_000_000, outputTokens: 0,
            cacheReadTokens: 0, cacheWriteTokens: 400_000,
            Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m, CacheWritePerMillion: 20.00m)));

        // 600.000 a 10 $/M = 6 $ · 400.000 a 20 $/M = 8 $ → 14 $ (1.400 credits)
        cost.BillableInputTokens.Should().Be(600_000);
        cost.Usd.Should().Be(14m);
    }

    /// <summary>
    /// Si las cachés suman más que la entrada, el supuesto no encaja con estos datos. Antes que
    /// emitir un coste NEGATIVO —que se propagaría a los agregados sin que nadie lo notara— se
    /// cobra cero por lo que no cuadra.
    /// </summary>
    [Fact]
    public void Nunca_sale_un_coste_negativo_aunque_los_numeros_no_cuadren()
    {
        CostResult cost = CostCalculator.Calculate(
            "m", "copilot",
            inputTokens: 100, outputTokens: 0,
            cacheReadTokens: 900_000, cacheWriteTokens: 0,
            Table(new ModelRate("m", string.Empty, 10.00m, 0m, 1.00m)));

        cost.BillableInputTokens.Should().Be(0);
        cost.Usd.Should().BeGreaterThanOrEqualTo(0m);
    }

    // ================================================================ el modelo manda

    /// <summary>
    /// <b>La misma sesión con dos tarifas distintas da dos costes distintos.</b> Es el corazón de
    /// F15: el modelo se lee del registro de CADA sesión, así que dos sesiones del mismo periodo
    /// con modelos distintos van cada una con el suyo.
    /// </summary>
    [Theory]
    // barato: 1.000.000 × 1 $/M + 200.000 × 5 $/M = 1 + 1 = 2 $
    // caro:    1.000.000 × 5 $/M + 200.000 × 25 $/M = 5 + 5 = 10 $
    [InlineData("barato", 2.0)]
    [InlineData("caro", 10.0)]
    public void La_misma_sesion_cuesta_lo_que_diga_la_tarifa_de_SU_modelo(
        string model, double expectedUsd)
    {
        var rates = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("barato", string.Empty, 1.00m, 5.00m, 0.10m),
                new ModelRate("caro", string.Empty, 5.00m, 25.00m, 0.50m),
            },
        };

        CostResult cost = CostCalculator.Calculate(
            model, "copilot",
            inputTokens: 1_000_000, outputTokens: 200_000,
            cacheReadTokens: 0, cacheWriteTokens: 0,
            rates);

        cost.Usd.Should().Be((decimal)expectedUsd);
        cost.Model.Should().Be(model);
    }

    /// <summary>Sin modelo registrado no se aplica la tarifa de otro «parecido»: se dice.</summary>
    [Fact]
    public void Sin_modelo_registrado_el_coste_es_no_aplicable()
    {
        CostResult cost = CostCalculator.Calculate(
            null, "copilot", 1000, 100, 0, 0, Table(new ModelRate("m", string.Empty, 1m, 1m, 1m)));

        cost.HasValue.Should().BeFalse();
        cost.Why.Should().Be(CostUnavailable.ModelUnknown);
    }

    /// <summary>Con un modelo sin tarifa configurada, tampoco: se señala y no se inventa.</summary>
    [Fact]
    public void Con_un_modelo_sin_tarifa_el_coste_es_no_aplicable()
    {
        CostResult cost = CostCalculator.Calculate(
            "modelo-nuevo", "copilot", 1000, 100, 0, 0, Table(new ModelRate("otro", string.Empty, 1m, 1m, 1m)));

        cost.HasValue.Should().BeFalse();
        cost.Why.Should().Be(CostUnavailable.RateMissing);
        cost.Model.Should().Be("modelo-nuevo");
    }

    /// <summary>Sin tabla ninguna —hub sin sembrar— es lo mismo: falta la tarifa.</summary>
    [Fact]
    public void Sin_tabla_de_tarifas_el_coste_es_no_aplicable()
        => CostCalculator.Calculate("m", "copilot", 1000, 100, 0, 0, rates: null)
            .Why.Should().Be(CostUnavailable.RateMissing);

    /// <summary>Y sin tokens no hay nada que calcular: «—», jamás un cero.</summary>
    [Fact]
    public void Sin_tokens_registrados_no_se_inventa_un_cero()
    {
        CostResult cost = CostCalculator.Calculate(
            "m", "copilot", 0, 0, 0, 0, Table(new ModelRate("m", string.Empty, 1m, 1m, 1m)));

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
                new ModelRate("claude-sonnet-5", string.Empty, 2m, 10m, 0.2m, CacheWritePerMillion: 2.50m),
                new ModelRate("claude-sonnet-5", "otra-reventa", 2m, 10m, 0.2m, CacheWritePerMillion: 4.00m),
            },
        };

        rates.Find("claude-sonnet-5", "copilot")!.CacheWritePerMillion.Should().Be(2.50m);
        rates.Find("claude-sonnet-5", "otra-reventa")!.CacheWritePerMillion.Should().Be(4.00m);
    }

    [Fact]
    public void El_modelo_se_casa_sin_distinguir_mayusculas()
        => Table(new ModelRate("GPT-5.4", string.Empty, 1m, 1m, 1m)).Find("gpt-5.4", "copilot").Should().NotBeNull();

    // ================================================================ la siembra

    /// <summary>
    /// La siembra trae tarifas de verdad y con fecha. No se comprueban aquí sus valores uno a uno
    /// —son datos de fuera y cambian—, sino que existe lo que la aplicación va a usar de serie y
    /// que nada nació sin fecha ni con un precio imposible.
    /// </summary>
    [Fact]
    public void La_siembra_trae_las_tarifas_con_su_fecha_y_su_procedencia()
    {
        ModelRateTable seed = ModelRateSeed.Create("copilot");

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
    /// <b>La siembra escribe el proveedor en TODAS las filas, y es el que le pasan</b>
    /// (PROV-2 §3). La tabla sembrada es la lista de precios publicada de la casa de fábrica, que
    /// es la que le factura a la organización; el dominio no conoce el nombre de ninguna casa, así
    /// que el identificador entra por parámetro.
    /// <para>
    /// Lo que se rompería en silencio sin esto: una tarifa sembrada sin proveedor casa con
    /// CUALQUIER casa, así que le pondría precio al consumo de una que no lo lleva — y la frase de
    /// «incluido en tu suscripción» se convertiría en un cobro inventado.
    /// </para>
    /// <para>
    /// Los modelos de Anthropic que están en la tabla son los que Copilot revende: ésos los paga
    /// la organización, con la tarifa que publica GitHub.
    /// </para>
    /// </summary>
    [Fact]
    public void La_siembra_escribe_el_proveedor_de_fabrica_en_todas_las_filas()
    {
        ModelRateTable seed = ModelRateSeed.Create("copilot");

        seed.Rates.Should().OnlyContain(r => r.Provider == "copilot");
        seed.Rates.Should().OnlyContain(r => r.IsProviderSpecific);

        // Y ninguna le pone precio al consumo de otra casa.
        seed.Find("claude-opus-5", "claude-code").Should().BeNull();
        seed.Find("claude-opus-5", "copilot").Should().NotBeNull();
        seed.Find("claude-sonnet-4.5", "copilot").Should().NotBeNull();
        seed.Find("gpt-5.4", "copilot").Should().NotBeNull();
    }

    /// <summary>
    /// <b>La migración de un hub que ya existe</b> (PROV-2 §3): sus tarifas se sembraron sin
    /// proveedor, y adoptar el de fábrica no cambia ni un precio ni pierde ni una fila.
    /// <para>
    /// Lo que se rompería en silencio sin esto: con la columna obligatoria, una tarifa genérica
    /// deja de casar en cuanto alguien escribe una específica del mismo modelo, y el coste de
    /// meses de historia cambiaría sin que nadie hubiera cambiado de gasto.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_hub_anterior_adopta_el_proveedor_de_fabrica_sin_perder_nada()
    {
        var viejo = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("gpt-5.4", string.Empty, 2.5m, 15m, 0.25m),
                new ModelRate("claude-opus-5", "claude-code", 5m, 25m, 0.5m),
            },
        };

        viejo.AdoptProvider("copilot").Should().Be(1, "solo la que no tenía dueño");

        viejo.Rates.Should().HaveCount(2, "nadie pierde su tabla");
        viejo.Find("gpt-5.4", "copilot")!.InputPerMillion.Should().Be(2.5m, "ningún precio cambia");
        viejo.Rates.Single(r => r.Model == "claude-opus-5").Provider.Should()
            .Be("claude-code", "lo que ya nombraba a alguien no se toca");
    }

    private static ModelRateTable Table(params ModelRate[] rates)
        => new() { Rates = rates.ToList() };
}
