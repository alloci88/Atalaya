using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10.2 §1 — el orden «Atención»: <b>¿por dónde miro AHORA?</b>
/// <para>
/// Los tres casos que tiene que resolver son los tres que un orden ingenuo falla. Por deuda, con
/// el 0,2 % auditado, arriba salen los dos ficheros que alguien miró. Por tamaño, arriba sale el
/// módulo más grande aunque esté limpio y comprobado. Atención tiene que poner arriba <b>lo
/// medido que arde</b> y <b>lo grande que nadie ha abierto</b>, y abajo lo auditado y limpio.
/// </para>
/// </summary>
public sealed class AttentionScoreTests
{
    private static HeatModule Module(
        string name, int loc, int auditedLoc, int debt, double? density, int units = 10)
    {
        var cells = new List<HeatUnit>();
        for (int i = 0; i < units; i++)
        {
            cells.Add(new HeatUnit(
                $"{name}/{i}.cs",
                name,
                loc / Math.Max(1, units),
                UnitState.Pendiente,
                HeatKnowledge.NoAuditada,
                new SeverityChips(0, 0, 0, 0),
                0,
                null));
        }

        return new HeatModule(name, cells, loc, auditedLoc, auditedLoc == 0 ? 0 : 1, debt, debt, density);
    }

    // ============================================ Los tres casos del encargo

    /// <summary>
    /// <b>Un módulo grande sin auditar rankea alto: la ignorancia es riesgo.</b> 240 KLOC que
    /// nadie ha abierto no son 240 KLOC limpias — son 240 KLOC de las que no se sabe nada.
    /// </summary>
    [Fact]
    public void Un_modulo_grande_sin_auditar_sube()
    {
        HeatModule big = Module("Grande", loc: 240_000, auditedLoc: 0, debt: 0, density: null);
        HeatModule small = Module("Pequeno", loc: 2_000, auditedLoc: 0, debt: 0, density: null);

        Attention huge = AttentionScore.Of(big, appLoc: 314_000);
        Attention tiny = AttentionScore.Of(small, appLoc: 314_000);

        huge.Score.Should().BeGreaterThan(tiny.Score * 10);
        huge.Risk.Should().Be(0, "no se ha medido nada: lo que pesa es lo que se ignora");
        huge.Ignorance.Should().BeApproximately(240.0 / 314, 0.01);
    }

    /// <summary>
    /// <b>Un módulo pequeño y muy sucio también sube</b>, aunque no esconda casi nada: lo suyo es
    /// un hecho comprobado, no una sospecha.
    /// </summary>
    [Fact]
    public void Un_modulo_pequeno_y_muy_sucio_tambien_sube()
    {
        HeatModule filthy = Module("Sucio", loc: 1_000, auditedLoc: 1_000, debt: 200, density: 200);
        HeatModule quiet = Module("Tranquilo", loc: 20_000, auditedLoc: 0, debt: 0, density: null);

        Attention dirty = AttentionScore.Of(filthy, appLoc: 314_000);
        Attention unseen = AttentionScore.Of(quiet, appLoc: 314_000);

        dirty.Score.Should().BeGreaterThan(unseen.Score);
        dirty.Risk.Should().Be(1, "auditado entero y por encima del techo de la rampa");
        dirty.Confidence.Should().Be(1, "está medido del todo");
    }

    /// <summary>
    /// <b>Un módulo limpio y auditado se va al fondo</b>, que es lo único que se puede pedir de
    /// una lista de «por dónde empiezo»: que no mande a donde ya se ha mirado y no había nada.
    /// </summary>
    [Fact]
    public void Un_modulo_limpio_y_auditado_se_va_al_fondo()
    {
        HeatModule clean = Module("Limpio", loc: 10_000, auditedLoc: 10_000, debt: 0, density: 0);

        Attention score = AttentionScore.Of(clean, appLoc: 314_000);

        score.Score.Should().Be(0);
        score.Risk.Should().Be(0, "densidad cero MEDIDA es cero riesgo, no riesgo desconocido");
        score.Ignorance.Should().Be(0, "no queda nada suyo por mirar");
    }

    // ============================================ La confianza

    /// <summary>
    /// Una densidad medida sobre el 2 % del módulo cuenta <b>la mitad</b> que la misma medida sobre
    /// el módulo entero — pero cuenta. Descontarla del todo enterraría el dato más accionable que
    /// tiene la herramienta; darle el peso completo sería extrapolar de una muestra del 2 %.
    /// </summary>
    [Fact]
    public void Una_densidad_medida_sobre_poco_codigo_cuenta_menos_pero_cuenta()
    {
        HeatModule sampled = Module("Muestra", loc: 11_000, auditedLoc: 244, debt: 39, density: 160);
        HeatModule complete = Module("Entero", loc: 11_000, auditedLoc: 11_000, debt: 1_760, density: 160);

        Attention little = AttentionScore.Of(sampled, appLoc: 314_000);
        Attention whole = AttentionScore.Of(complete, appLoc: 314_000);

        little.Confidence.Should().BeApproximately(0.51, 0.01);
        whole.Confidence.Should().Be(1);
        little.Risk.Should().BeLessThan(whole.Risk);
        little.Risk.Should().BeGreaterThan(0, "sigue contando: es un hecho comprobado");
        AttentionScore.ConfidenceFloor.Should().Be(0.5);
    }

    // ============================================ Los anclajes

    /// <summary>
    /// Los dos sumandos están anclados —el riesgo al techo de la rampa y la ignorancia al tamaño
    /// de la aplicación—, <b>no normalizados contra el máximo del día</b>: la puntuación de un
    /// módulo no cambia porque se dé de alta otro, así que el orden de ayer se puede comparar con
    /// el de hoy.
    /// </summary>
    [Fact]
    public void La_puntuacion_de_un_modulo_no_depende_de_los_demas()
    {
        HeatModule one = Module("Uno", loc: 10_000, auditedLoc: 5_000, debt: 250, density: 50);

        Attention alone = AttentionScore.Of(one, appLoc: 314_000);
        Attention crowded = AttentionScore.Of(one, appLoc: 314_000);

        alone.Should().Be(crowded);
        AttentionScore.Ceiling.Should().Be(100, "el umbral del último paso de la rampa, no un número nuevo");
    }

    /// <summary>La puntuación cabe siempre en 0..1, y sus pesos suman uno.</summary>
    [Fact]
    public void La_puntuacion_esta_acotada()
    {
        (AttentionScore.RiskWeight + AttentionScore.IgnoranceWeight).Should().Be(1);

        HeatModule worst = Module("Peor", loc: 314_000, auditedLoc: 1, debt: 99_999, density: 9_999);
        Attention score = AttentionScore.Of(worst, appLoc: 314_000);

        score.Score.Should().BeInRange(0, 1);
        score.Risk.Should().BeInRange(0, 1);
        score.Ignorance.Should().BeInRange(0, 1);
    }

    /// <summary>Sin aplicación que medir, la ignorancia no se divide entre cero.</summary>
    [Fact]
    public void Sin_lineas_en_la_aplicacion_no_se_divide_entre_cero()
    {
        Attention score = AttentionScore.Of(Module("X", 0, 0, 0, null), appLoc: 0);

        score.Ignorance.Should().Be(0);
        score.Score.Should().Be(0);
    }
}
