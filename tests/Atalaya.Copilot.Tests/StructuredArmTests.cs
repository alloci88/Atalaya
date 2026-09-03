using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// M1 — <b>el brazo estructurado, que solo existe para medir</b>.
/// <para>
/// F24 dejó escrito (D-907) que el núcleo de defectos sale siempre y con los mismos nombres, y que
/// la cola varía de tanda a tanda y produce variantes en las pasadas tardías. La hipótesis de M1 es
/// que eso viene de cómo se pregunta, y que un recorrido <b>miembro × familia</b> con la identidad
/// fijada en <c>(regla, miembro)</c> no deja hueco donde poner una variante.
/// </para>
/// <para>
/// <b>Esto no cambia el producto.</b> La palanca vale <see cref="AuditStyle.Libre"/> en producción
/// y lo que fija esta suite es justamente eso: que apagada no se note, y que encendida no abra
/// puertas que el enfoque temático cerró.
/// </para>
/// </summary>
public class StructuredArmTests
{
    private static ComposedUnitPrompt Compose(
        AuditStyle style = AuditStyle.Libre, AuditTheme theme = AuditTheme.General)
        => PromptComposer.Compose(
            "src/A.cs", "class A { }", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes,
            theme: theme, style: style);

    /// <summary>
    /// <b>Apagada, byte a byte el de producción.</b> Es la condición de que M1 no sea una fase: el
    /// prompt por defecto y el prompt con la palanca en <c>Libre</c> son la MISMA cadena, no una
    /// parecida. Si esto falla, la medida ha cambiado lo que dice medir.
    /// </summary>
    [Fact]
    public void Con_la_palanca_apagada_el_prompt_es_byte_a_byte_el_de_produccion()
    {
        string produccion = PromptComposer.Compose(
            "src/A.cs", "class A { }", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes).Text;

        Compose().Text.Should().Be(produccion);
        Compose(AuditStyle.Libre).Text.Should().Be(produccion);
    }

    /// <summary>Y el método de barrido de siempre sigue ahí, entero, con la palanca apagada.</summary>
    [Theory]
    [InlineData("MÉTODO DE BARRIDO — síguelo en este orden, no lo abrevies:")]
    [InlineData("1. Enumera TODOS los miembros de la unidad")]
    [InlineData("4. Antes de cerrar, repasa tu lista del paso 1")]
    public void Apagada_lleva_el_metodo_de_barrido_de_siempre(string frase)
    {
        Compose().Text.Should().Contain(frase);
        Compose().Text.Should().NotContain("MÉTODO ESTRUCTURADO");
    }

    /// <summary>
    /// Encendida, el método se SUSTITUYE: no se suma. Un brazo que dejara los dos textos estaría
    /// midiendo una mezcla que no es ninguno de los dos.
    /// </summary>
    [Fact]
    public void Encendida_sustituye_el_metodo_y_no_lo_suma()
    {
        string e = Compose(AuditStyle.Estructurado).Text;

        e.Should().Contain("MÉTODO ESTRUCTURADO — síguelo en este orden");
        e.Should().NotContain("MÉTODO DE BARRIDO — síguelo en este orden");
        e.Should().NotContain("Trabaja sobre esa lista; es tu lista de comprobación.");
    }

    /// <summary>Lo que define el brazo: la identidad del hallazgo no la elige el modelo.</summary>
    [Theory]
    [InlineData("Enumera los miembros de la unidad CON SU LÍNEA")]
    [InlineData("Recorre MIEMBRO × FAMILIA")]
    [InlineData("identidad es (regla, miembro)")]
    [InlineData("UN SOLO hallazgo por pareja (regla, miembro)")]
    [InlineData("El título NO se inventa")]
    [InlineData("criterio.<área>, miembro")]
    public void Encendida_fija_la_identidad_en_regla_y_miembro(string frase)
        => Compose(AuditStyle.Estructurado).Text.Should().Contain(frase);

    /// <summary>
    /// <b>El resto del prompt no se toca.</b> M1 sustituye el bloque del método y nada más: las
    /// entregas, la economía de turnos, las reglas de forma y el defecto sistémico siguen enteras.
    /// </summary>
    [Theory]
    [InlineData("Eres un auditor de código.")]
    [InlineData("UN DEFECTO SISTÉMICO ES UN SOLO HALLAZGO")]
    [InlineData("Tienes dos cosas que entregar en cada unidad:")]
    [InlineData("ECONOMÍA DE TURNOS")]
    [InlineData("Reglas de forma:")]
    [InlineData("symbol: SIEMPRE, el miembro que contiene el defecto")]
    public void Encendida_no_toca_el_resto_del_prompt(string frase)
        => Compose(AuditStyle.Estructurado).Text.Should().Contain(frase);

    // ------------------------------------------------------- el control temático (F17, D-825)

    /// <summary>
    /// <b>La lupa manda igual que en el libre.</b> Bajo una temática el recorrido es sobre las
    /// familias de QUÉ BUSCAS y ninguna más. El catálogo entero se sigue VIENDO en el brief —eso lo
    /// decidió F5.12— pero verlo no es recorrerlo: un recorrido que enumerase el catálogo bajo una
    /// lupa abriría la puerta que el enfoque cerró.
    /// </summary>
    [Fact]
    public void Bajo_una_tematica_el_recorrido_es_solo_el_de_la_lupa()
    {
        string t = Compose(AuditStyle.Estructurado, AuditTheme.Seguridad).Text;

        t.Should().Contain("SOLO las de QUÉ BUSCAS del ENFOQUE DEL CICLO, y ninguna");
        t.Should().NotContain("todas las reglas del catálogo del brief");
        t.Should().Contain(ThemeSection.Heading, "el bloque de enfoque de F17 sigue puesto");
    }

    /// <summary>Y en General sí se recorre el catálogo entero, que es lo que General significa.</summary>
    [Fact]
    public void En_General_el_recorrido_es_el_catalogo_entero()
    {
        string g = Compose(AuditStyle.Estructurado).Text;

        g.Should().Contain("todas las reglas del catálogo del brief");
        g.Should().NotContain("SOLO las de QUÉ BUSCAS");
    }

    /// <summary>
    /// El brazo no sube el guardarraíl de F19 (3.500), que no se toca por una medida: en General
    /// los dos brazos caben —3.096 el libre, 3.189 el estructurado—.
    /// <para>
    /// <b>Y lo que se encontró midiendo esto, que no es de M1</b>: bajo una temática el prefijo de
    /// PRODUCCIÓN ya vale 3.645, porque el bloque de enfoque de F17 pesa 549 tokens. El guardarraíl
    /// lleva roto desde F17 y no se había visto porque su test solo mide General. Aquí se afirma lo
    /// que es cierto —que el brazo cuesta ~90 tokens y no es la causa— y el agujero queda anotado
    /// en el parte y en el BACKLOG; taparlo con una cifra más alta sería justo lo que no se hace.
    /// </para>
    /// </summary>
    [Fact]
    public void El_brazo_cuesta_poco_y_no_es_quien_rompe_el_guardarrail()
    {
        Compose().Composition.Estable.Should().BeLessThan(3_500);
        Compose(AuditStyle.Estructurado).Composition.Estable.Should().BeLessThan(3_500);

        int libreTema = Compose(AuditStyle.Libre, AuditTheme.Seguridad).Composition.Estable;
        int estructuradoTema = Compose(AuditStyle.Estructurado, AuditTheme.Seguridad).Composition.Estable;

        libreTema.Should().BeGreaterThan(3_500,
            "el prefijo de producción bajo una lupa YA se pasa: es un agujero de F17, no de M1");
        (estructuradoTema - libreTema).Should().BeLessThan(150,
            "lo que añade el brazo es el método, y es barato");
    }
}
