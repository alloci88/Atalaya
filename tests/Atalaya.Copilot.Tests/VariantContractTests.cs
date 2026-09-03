using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F24, RETIRADA — <b>el prompt vuelve a ser el de antes, y esto lo fija</b>.
/// <para>
/// F24 metió en la zona estable un contrato de variantes: «una reformulación, una ampliación o una
/// consecuencia de algo ya reportado no es un hallazgo nuevo», más la instrucción de buscar lo que
/// falta y cerrar la unidad vacía cuando no quedara nada. Se midió con nueve tandas y se retiró
/// (D-907): hacía converger el barrido, sí, pero las tandas que convergían eran las que menos
/// encontraban, y lo que dejaba de salir no eran solo variantes.
/// </para>
/// <para>
/// <b>Por qué un test y no borrarlo y ya.</b> El contrato son ~390 tokens en el prefijo estable, que
/// viaja en TODAS las llamadas de la sesión. Un trozo de texto que vuelve a colarse ahí no rompe
/// nada: gasta, y encima cambia lo que el auditor busca. Esto fija que no está, y el guardarraíl de
/// F19 —<c>El_prefijo_estable_no_puede_engordar_sin_que_salte_un_rojo</c>— fija el tamaño.
/// </para>
/// <para>
/// <b>Lo único que F24 dejó en el prefijo</b> es la línea que pide el <c>symbol</c> siempre, y se
/// queda a propósito: sin ella la marca de posibles duplicados del §5 de F23 se apaga en silencio
/// con los proveedores que no lo rellenan (D-898). Por eso el prefijo NO es byte a byte el de antes
/// de F24 — es el de antes más esa línea, y nada más.
/// </para>
/// </summary>
public class VariantContractTests
{
    private static ComposedUnitPrompt Compose()
        => PromptComposer.Compose(
            "src/A.cs", "class A { }", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes);

    /// <summary>Ni una frase del contrato retirado, en ninguna parte del prompt.</summary>
    [Theory]
    [InlineData("UNA VARIANTE NO ES UN HALLAZGO NUEVO")]
    [InlineData("BUSCA LO QUE FALTA")]
    [InlineData("submit_findings vacío y unit_done")]
    [InlineData("distinctFrom")]
    [InlineData("distinctReason")]
    [InlineData("queda registrado como insistido")]
    public void El_contrato_de_variantes_no_esta_en_el_prompt(string frase)
        => Compose().Text.Should().NotContain(frase);

    /// <summary>
    /// Y lo de siempre sigue entero: el contrato se metió EN MEDIO de las reglas del auditor, así
    /// que al sacarlo había que volver a unir las dos mitades. Si esto falla, el corte se llevó algo
    /// que no era suyo.
    /// </summary>
    [Theory]
    [InlineData("Eres un auditor de código.")]
    [InlineData("MÉTODO DE BARRIDO")]
    [InlineData("UN DEFECTO SISTÉMICO ES UN SOLO HALLAZGO")]
    [InlineData("Tienes dos cosas que entregar en cada unidad:")]
    [InlineData("ECONOMÍA DE TURNOS")]
    [InlineData("Cierra con unit_done")]
    public void Las_reglas_de_siempre_siguen_enteras(string frase)
        => Compose().Text.Should().Contain(frase);

    /// <summary>
    /// El orden también: el defecto sistémico y las dos entregas quedaron pegados otra vez, sin el
    /// bloque que se metió entre ellos.
    /// </summary>
    [Fact]
    public void El_defecto_sistemico_y_las_entregas_vuelven_a_ir_seguidos()
    {
        string p = Compose().Text;
        int sistemico = p.IndexOf("defecto por miembro infla el baseline", StringComparison.Ordinal);
        int entregas = p.IndexOf("Tienes dos cosas que entregar", StringComparison.Ordinal);

        sistemico.Should().BeGreaterThan(0);
        entregas.Should().BeGreaterThan(sistemico);
        p[sistemico..entregas].Should().NotContain("VARIANTE", "entre los dos no queda nada de F24");
    }

    /// <summary>
    /// <b>Lo único que F24 deja en la zona estable</b>: el miembro, pedido siempre. Es lo que hace
    /// que el criterio de posibles duplicados de F23 pueda distinguir dos métodos vecinos.
    /// </summary>
    [Fact]
    public void Lo_unico_que_queda_de_F24_es_pedir_el_simbolo_siempre()
    {
        ComposedUnitPrompt composed = Compose();

        composed.StablePrefix.Should().Contain("symbol: SIEMPRE, el miembro que contiene el defecto");
        composed.UnitPart.Should().NotContain("symbol: SIEMPRE");
    }

    /// <summary>
    /// <b>Y el tamaño vuelve al de antes</b>, que es lo que se paga: el prefijo estable viaja en
    /// todas las llamadas de la sesión. Antes de F24 eran ~3.056 tokens; el contrato lo subió a
    /// ~3.486 y su retirada lo deja en ~3.094. La diferencia que queda —unos 40 tokens— es la línea
    /// del <c>symbol</c>, y no hay más.
    /// <para>
    /// El margen es estrecho a propósito: el guardarraíl de F19 (<c>PromptComposition.TechoEstable</c>)
    /// avisa de un engorde grande, pero no habría dicho nada de que el contrato volviera a colarse
    /// aquí. Esto sí.
    /// </para>
    /// </summary>
    [Fact]
    public void El_prefijo_estable_vuelve_al_tamano_de_antes_de_F24()
    {
        int estable = Compose().Composition.Estable;

        estable.Should().BeLessThan(3_150, "sin el contrato de variantes solo sobra la línea del symbol");
        estable.Should().BeGreaterThan(3_000, "y si se desplomara sería que se ha caído un bloque");
    }
}
