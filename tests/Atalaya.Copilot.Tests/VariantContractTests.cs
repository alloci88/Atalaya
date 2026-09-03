using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F24 §1 — <b>el contrato: una variante no es un hallazgo nuevo</b>.
/// <para>
/// El contrato de F4 decía «si es el mismo problema, no lo reportes como nuevo». Lo que no decía es
/// <b>qué es el mismo problema cuando se le pide más y no hay más</b>, y ahí es donde el auditor
/// reformulaba: el mismo defecto con otro título, bajo otra regla, por su consecuencia. Cinco pares
/// del informe de referencia son eso, y cuatro de los nueve hallazgos tardíos de ClienteRemoto.
/// </para>
/// <para>
/// <b>Por qué se prueba el TEXTO.</b> Dos de los cinco pares —reglas distintas, y símbolos a
/// alturas distintas del código— el filtro de la puerta no los puede ver por construcción: quedan a
/// cargo de esto y solo de esto. Un párrafo que desaparece en una reescritura no rompe nada
/// visible; simplemente vuelven a pagarse pasadas.
/// </para>
/// <para>
/// Y va en la ZONA ESTABLE, que es donde vive todo lo que no cambia entre unidades ni entre
/// pasadas. El orden lo fija <see cref="PromptCacheOrderTests"/>; aquí se fija que el texto está y
/// que está en ese lado de la costura.
/// </para>
/// </summary>
public class VariantContractTests
{
    private static ComposedUnitPrompt Compose()
        => PromptComposer.Compose(
            "src/A.cs", "class A { }", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes);

    [Theory]
    [InlineData("UNA VARIANTE NO ES UN HALLAZGO NUEVO")]
    [InlineData("MISMO defecto en el MISMO sitio")]
    [InlineData("MISMO defecto en OTRO punto de la unidad")]
    public void El_contrato_de_las_variantes_esta_en_el_prompt(string frase)
        => Compose().Text.Should().Contain(frase);

    /// <summary>
    /// Los dos casos, y ninguno más. «No emitas nada» es el que faltaba: no hay herramienta para
    /// retocar el título de un hallazgo y no se añade ninguna, así que el auditor tiene que saber
    /// que la respuesta correcta a «esto ya está» es el silencio.
    /// </summary>
    [Fact]
    public void Dice_que_el_mismo_defecto_en_el_mismo_sitio_no_se_emite()
        => Compose().Text.Should().Contain("NO emitas nada");

    /// <summary>Y que el mismo defecto en otro punto es <c>add_locations</c>, no un hallazgo.</summary>
    [Fact]
    public void Dice_que_el_mismo_defecto_en_otro_punto_es_add_locations()
        => Compose().Text.Should().Contain("add_locations(findingId, locations) sobre el");

    /// <summary>
    /// «Existente» son las DOS cosas: la lista de la unidad y lo creado en el barrido en curso
    /// (D-088, D-091). Sin la segunda mitad, el auditor podría duplicar dentro de la misma pasada
    /// sin salirse de la letra del contrato.
    /// </summary>
    [Fact]
    public void Existente_incluye_lo_reportado_en_esta_misma_unidad()
        => Compose().Text.Should().Contain("mismo hayas reportado en esta unidad");

    /// <summary>
    /// Los dos pares que el filtro no ve, nombrados en el propio prompt. Son ejemplos, no una
    /// taxonomía: lo que enseñan es que «otra regla» y «otra consecuencia» siguen siendo el mismo
    /// defecto.
    /// </summary>
    [Theory]
    [InlineData("sin Timeout")]
    [InlineData("sin CancellationToken")]
    [InlineData("no valida signos")]
    [InlineData("no es otra por ocurrir")]
    public void Nombra_los_casos_que_el_filtro_no_puede_ver(string ejemplo)
        => Compose().Text.Should().Contain(ejemplo);

    /// <summary>
    /// <b>«Busca lo que falta», no «busca más»</b>. Es la mitad que explica el fenómeno: un modelo
    /// al que se le pide más cuando no queda más, reformula. Que una pasada vacía sea una respuesta
    /// CORRECTA tiene que estar escrito, porque es lo que termina el barrido.
    /// </summary>
    [Theory]
    [InlineData("BUSCA LO QUE FALTA, NO \"MÁS\"")]
    [InlineData("submit_findings vacío y unit_done")]
    [InlineData("CORRECTA y COMPLETA")]
    public void Pide_lo_que_falta_y_no_mas(string frase)
        => Compose().Text.Should().Contain(frase);

    /// <summary>
    /// El reintento, dicho en el prompt: si de verdad es otro defecto, se reenvía con
    /// <c>distinctFrom</c>. Sin esto el filtro sería un muro — el auditor no tendría cómo sostener
    /// una discrepancia legítima.
    /// </summary>
    [Fact]
    public void Explica_el_reintento_con_distinctFrom()
        => Compose().Text.Should().Contain("distinctFrom");

    /// <summary>
    /// <b>La palanca de medida</b>, y solo eso: apagada, el prompt es el de antes de F24. Existe
    /// para poder correr la misma tanda con la regla y sin ella y enseñar la diferencia en pasadas y
    /// llamadas — el mismo patrón que <c>CutOnUnitDone</c> en F21. En producción va siempre puesta.
    /// </summary>
    [Fact]
    public void Sin_el_contrato_el_prompt_es_el_de_antes()
    {
        string sin = Nivel(VariantContractLevel.None);

        sin.Should().NotContain("UNA VARIANTE NO ES UN HALLAZGO NUEVO").And.NotContain("BUSCA LO QUE FALTA");
        sin.Should().Contain("UN DEFECTO SISTÉMICO ES UN SOLO HALLAZGO", "lo demás sigue entero");
        sin.Should().Contain("Tienes dos cosas que entregar en cada unidad:");
        sin.Length.Should().BeLessThan(Compose().Text.Length);
    }

    /// <summary>
    /// <b>La media regla</b> (F24, rama de D-902): solo la mitad que define qué es una variante y qué
    /// hacer con ella, sin la que le dice al auditor que puede cerrar la unidad vacía. Las dos
    /// mitades hacen cosas distintas y solo separándolas se puede saber cuál se cobra cobertura.
    /// </summary>
    [Fact]
    public void Con_media_regla_va_la_definicion_y_no_el_permiso_para_terminar()
    {
        string medio = Nivel(VariantContractLevel.Core);

        medio.Should().Contain("UNA VARIANTE NO ES UN HALLAZGO NUEVO");
        medio.Should().Contain("MISMO defecto en OTRO punto de la unidad");
        medio.Should().Contain("distinctFrom");
        medio.Should().NotContain("BUSCA LO QUE FALTA");
        medio.Should().NotContain("submit_findings vacío y unit_done");
        medio.Length.Should().BeGreaterThan(Nivel(VariantContractLevel.None).Length);
        medio.Length.Should().BeLessThan(Compose().Text.Length);
    }

    private static string Nivel(VariantContractLevel nivel)
        => PromptComposer.Compose(
                "src/A.cs", "class A { }", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes,
                variantContract: nivel)
            .Text;

    /// <summary>
    /// Y todo ello del lado ESTABLE de la costura de caché (F18 §2). Un contrato que viajara en la
    /// parte variable se re-escribiría en caché en cada unidad y en cada pasada: costaría dinero por
    /// decir siempre lo mismo.
    /// </summary>
    [Fact]
    public void El_contrato_viaja_en_la_zona_estable()
    {
        ComposedUnitPrompt composed = Compose();

        composed.StablePrefix.Should().Contain("UNA VARIANTE NO ES UN HALLAZGO NUEVO");
        composed.UnitPart.Should().NotContain("UNA VARIANTE NO ES UN HALLAZGO NUEVO");
    }
}
