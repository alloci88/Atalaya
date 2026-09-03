using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F18 §1 — <b>adónde va cada token</b>.
/// <para>
/// Lo que se fija aquí es la aritmética de la línea que el informe escribe y que hasta F18 había
/// que sacar a mano: cuánto de una llamada es el código que se audita, cuánto es andamiaje y
/// cuántas llamadas lleva una unidad. Y, con el mismo peso, <b>que no se inventa nada</b>: una
/// sesión sin desglose no produce una línea con ceros, produce silencio.
/// </para>
/// </summary>
public class PromptBudgetTests
{
    private static AuditSession Session(string? provider, params UnitUsageBreakdown[] units)
    {
        var s = new AuditSession
        {
            Id = Ulid.Empty,
            AppSlug = "app",
            By = "yo",
            Machine = "maquina",
            Provider = provider,
            Mode = AuditMode.Lotes,
        };
        s.UsageBreakdown.AddRange(units);
        return s;
    }

    private static UnitUsageBreakdown Unit(string path, params PassUsage[] passes)
    {
        var u = new UnitUsageBreakdown { Unit = path };
        u.Passes.AddRange(passes);
        return u;
    }

    private static PromptComposition Comp(int estable, int existentes, int unidad)
        => new(Reglas: estable, Existentes: existentes, Unidad: unidad);

    /// <summary>
    /// El caso del enunciado de F18: una clase pequeña dentro de un prompt enorme. El código es una
    /// fracción minúscula y la línea tiene que decirlo con esas palabras.
    /// </summary>
    [Fact]
    public void La_linea_dice_andamiaje_codigo_y_llamadas_por_unidad()
    {
        AuditSession s = Session(
            "copilot",
            Unit("src/A.cs", new PassUsage(1, Calls: 10, Composition: Comp(2800, 100, 600))),
            Unit("src/B.cs", new PassUsage(1, Calls: 10, Composition: Comp(2800, 100, 600))));

        // Copilot mete la caché DENTRO de la entrada: el prompt entero son los 570.000 de In.
        s.Usage.Add(570_000, 24_000, 450_000, 100_000, null, calls: 20);

        PromptBudget b = PromptBudget.From(s);

        b.Calls.Should().Be(20);
        b.Units.Should().Be(2);
        b.Prompts.Should().Be(2);
        b.PromptTokens.Should().Be(570_000);
        b.PerCall.Should().Be(28_500);
        b.CodePerCall.Should().Be(600, "el código de la pasada viaja entero en cada una de sus llamadas");
        b.CodeShare.Should().BeApproximately(600.0 / 28_500, 0.0001);
        b.CallsPerUnit.Should().Be(10);
        b.Line.Should().Be("andamiaje ≈ 27900 tokens/llamada · código auditado ≈ 600 (2.1 %) · 10 llamadas por unidad");
    }

    /// <summary>
    /// Con Claude Code la caché va POR FUERA de la entrada (D-785). Los mismos tokens, contados con
    /// la regla del otro proveedor, dan otro reparto — y usar la equivocada desviaría todo a la vez.
    /// </summary>
    [Fact]
    public void La_entrada_se_lee_con_la_semantica_de_su_proveedor()
    {
        UnitUsageBreakdown[] Units() => new[] { Unit("src/A.cs", new PassUsage(1, Calls: 4, Composition: Comp(2800, 0, 700))) };

        AuditSession copilot = Session("copilot", Units());
        copilot.Usage.Add(40_000, 5_000, 30_000, 6_000, null, calls: 4);

        AuditSession claude = Session("claude-code", Units());
        claude.Usage.Add(40_000, 5_000, 30_000, 6_000, null, calls: 4);

        PromptBudget.From(copilot).PromptTokens.Should().Be(40_000);
        PromptBudget.From(claude).PromptTokens.Should().Be(76_000, "40.000 + 30.000 leídos + 6.000 escritos");
    }

    /// <summary>
    /// Un proveedor sin registrar se lee como Copilot, que es lo único que había antes de F14 — la
    /// misma regla que ya usa el cálculo de credits. Aquí solo se comprueba que no hay una segunda.
    /// </summary>
    [Fact]
    public void Sin_proveedor_registrado_se_cuenta_como_Copilot()
    {
        AuditSession s = Session(null, Unit("src/A.cs", new PassUsage(1, Calls: 2, Composition: Comp(100, 0, 50))));
        s.Usage.Add(1_000, 100, 800, 100, null, calls: 2);

        PromptBudget.From(s).PromptTokens.Should().Be(1_000);
    }

    /// <summary>
    /// <b>Sin composición no hay línea.</b> Las sesiones anteriores a F18 no llevan desglose por
    /// pasada, y escribir «código 0 %» afirmaría que no viajó código — que es falso y además es el
    /// tipo de cifra sin causa que N-2 prohíbe.
    /// </summary>
    [Fact]
    public void Una_sesion_legada_no_produce_una_linea_con_ceros()
    {
        AuditSession s = Session("copilot", new UnitUsageBreakdown { Unit = "src/A.cs" });
        s.Usage.Add(10_000, 500, 0, 0, null, calls: 3);

        PromptBudget b = PromptBudget.From(s);

        b.HasComposition.Should().BeFalse();
        b.Line.Should().BeEmpty();
        b.PromptTokens.Should().Be(10_000, "lo que sí se sabe se sigue diciendo");
        b.CallsPerUnit.Should().Be(3);
    }

    /// <summary>Sin llamadas registradas no se divide por cero ni se escribe la frase.</summary>
    [Fact]
    public void Sin_llamadas_no_hay_reparto()
    {
        AuditSession s = Session("copilot", Unit("src/A.cs", new PassUsage(1, Composition: Comp(100, 0, 50))));

        PromptBudget b = PromptBudget.From(s);

        b.PerCall.Should().Be(0);
        b.CodeShare.Should().Be(0);
        b.Line.Should().BeEmpty();
    }

    /// <summary>
    /// El termómetro de F18 §2: el suelo de escritura de caché es el prefijo estable UNA vez más la
    /// parte variable de cada prompt. Lo que pase de ahí son re-escrituras.
    /// </summary>
    [Fact]
    public void El_suelo_de_cache_es_el_prefijo_una_vez_mas_lo_variable_de_cada_prompt()
    {
        AuditSession s = Session(
            "copilot",
            Unit("src/A.cs",
                new PassUsage(1, Calls: 3, Composition: Comp(2800, 100, 600)),
                new PassUsage(2, Calls: 3, Composition: Comp(2800, 150, 600))),
            Unit("src/B.cs", new PassUsage(1, Calls: 3, Composition: Comp(2800, 0, 400))));

        s.Usage.Add(90_000, 3_000, 60_000, 20_000, null, calls: 9);

        PromptBudget b = PromptBudget.From(s);

        b.StablePrefixTokens.Should().Be(2800);
        // 700 + 750 + 400 de partes variables, más 2.800 de prefijo escrito una sola vez.
        b.CacheWriteFloor.Should().Be(2800 + 700 + 750 + 400);
        b.CacheRewrites.Should().Be(20_000 - b.CacheWriteFloor);
    }

    /// <summary>Una caché que escribe MENOS que el suelo no produce re-escrituras negativas.</summary>
    [Fact]
    public void Las_reescrituras_nunca_son_negativas()
    {
        AuditSession s = Session("copilot", Unit("src/A.cs", new PassUsage(1, Calls: 1, Composition: Comp(2800, 0, 600))));
        s.Usage.Add(4_000, 100, 0, 10, null, calls: 1);

        PromptBudget.From(s).CacheRewrites.Should().Be(0);
    }

    /// <summary>
    /// Una pasada pesa por sus llamadas: la que necesitó ocho vueltas mandó su código ocho veces, y
    /// promediar sin eso repartiría el código de una pasada corta entre las llamadas de una larga.
    /// </summary>
    [Fact]
    public void Cada_pasada_pesa_por_sus_llamadas()
    {
        AuditSession s = Session(
            "copilot",
            Unit("src/A.cs",
                new PassUsage(1, Calls: 8, Composition: Comp(0, 0, 1000)),
                new PassUsage(2, Calls: 2, Composition: Comp(0, 0, 100))));
        s.Usage.Add(10_000, 500, 0, 0, null, calls: 10);

        PromptBudget b = PromptBudget.From(s);

        b.Composition.Unidad.Should().Be(8 * 1000 + 2 * 100);
        b.CodePerCall.Should().Be(820);
    }

    /// <summary>El desglose se suma y se escala como lo que es: una cuenta, no una foto.</summary>
    [Fact]
    public void La_composicion_se_suma_y_se_escala()
    {
        PromptComposition a = new(Reglas: 10, Rubrica: 20, Unidad: 30);
        PromptComposition b = new(Reglas: 1, Existentes: 2, Unidad: 3);

        (a + b).Should().Be(new PromptComposition(Reglas: 11, Rubrica: 20, Existentes: 2, Unidad: 33));
        a.Times(3).Should().Be(new PromptComposition(Reglas: 30, Rubrica: 60, Unidad: 90));
        a.Estable.Should().Be(30);
        a.Variable.Should().Be(30);
        a.Total.Should().Be(60);
        a.Andamiaje.Should().Be(30);
    }
}
