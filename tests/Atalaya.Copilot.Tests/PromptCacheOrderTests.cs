using Atalaya.Agents;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F18 §2 — <b>el orden del prompt, fijado</b>.
/// <para>
/// La caché de un proveedor solo sirve si el principio del prompt es idéntico entre unidades y
/// entre pasadas. Lo que se prueba aquí es exactamente eso: que existe un tramo estable, que es el
/// mismo byte a byte se audite lo que se audite, y que <b>nada de la unidad se cuela dentro</b> —
/// ni su ruta, ni su código, ni los ULID de sus hallazgos, ni el número de pasada.
/// </para>
/// <para>
/// <b>Por qué con un test y no con una nota.</b> Un prefijo contaminado no falla: gasta. No hay
/// excepción, ni error, ni cifra que se salga de sitio; solo una factura un poco más alta cada mes.
/// Es justo la clase de regresión que solo un rojo puede impedir.
/// </para>
/// </summary>
public class PromptCacheOrderTests
{
    private static readonly IReadOnlyList<ExistingFinding> Existing = new[]
    {
        new ExistingFinding("01J000000000000000000000AA", "BUG-0001", "No valida nulos", "alta",
            "src/A.cs:12", "activo", "General"),
    };

    private static readonly IReadOnlyList<ExistingFinding> OffTheme = new[]
    {
        new ExistingFinding("01J000000000000000000000BB", "SEC-0002", "Concatena SQL", "critica",
            "src/A.cs:80", "activo", "Seguridad"),
    };

    private static PatternSilenceSet Patterns() => PatternSilenceSet.From(
        new[]
        {
            new PatternSilence
            {
                ShortId = "P-1",
                Exemplar = "faltan comentarios XML en miembros públicos",
                By = "yo",
            },
        },
        DateTimeOffset.UtcNow);

    private static ComposedUnitPrompt Compose(
        string path, string content, AuditTheme theme = AuditTheme.General,
        IReadOnlyList<ExistingFinding>? existing = null)
        => PromptComposer.Compose(
            path, content, PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes,
            existing ?? Existing, Patterns(), null, theme,
            theme == AuditTheme.General ? null : OffTheme);

    /// <summary>Concatenar las dos piezas da el prompt de siempre. Sin esto, todo lo demás sobra.</summary>
    [Fact]
    public void Las_dos_piezas_concatenadas_son_el_prompt_entero()
    {
        ComposedUnitPrompt c = Compose("src/A.cs", "class A {}");

        (c.StablePrefix + c.UnitPart).Should().Be(c.Text);
        c.Text.Should().Be(PromptComposer.ComposeUnitPrompt(
            "src/A.cs", "class A {}", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes,
            Existing, Patterns()));
    }

    /// <summary>
    /// Dos unidades distintas, con código distinto y hallazgos distintos: el prefijo es EL MISMO.
    /// Es la condición sin la cual la caché no puede servir nada entre unidades.
    /// </summary>
    [Fact]
    public void El_prefijo_estable_es_identico_entre_unidades()
    {
        ComposedUnitPrompt a = Compose("src/Uno.cs", "class Uno { void M() {} }");
        ComposedUnitPrompt b = Compose(
            "src/Otro/Dos.cs",
            "class Dos { int N => 42; }",
            existing: Array.Empty<ExistingFinding>());

        b.StablePrefix.Should().Be(a.StablePrefix);
        b.UnitPart.Should().NotBe(a.UnitPart, "lo que cambia tiene que estar fuera del prefijo");
    }

    /// <summary>
    /// Y entre PASADAS de la misma unidad: la segunda pasada ve más hallazgos —los que reportó la
    /// primera— y eso no puede mover el prefijo.
    /// </summary>
    [Fact]
    public void El_prefijo_estable_es_identico_entre_pasadas()
    {
        ComposedUnitPrompt pasada1 = Compose("src/A.cs", "class A {}", existing: Array.Empty<ExistingFinding>());
        ComposedUnitPrompt pasada2 = Compose("src/A.cs", "class A {}", existing: Existing);

        pasada2.StablePrefix.Should().Be(pasada1.StablePrefix);
        pasada2.UnitPart.Should().NotBe(pasada1.UnitPart);
    }

    /// <summary>
    /// <b>La guarda dura.</b> Ni la ruta, ni el código, ni los ULID, ni los alias de los hallazgos
    /// aparecen en el prefijo. Un solo identificador colado ahí invalidaría la caché en cada unidad.
    /// </summary>
    [Theory]
    [InlineData(AuditTheme.General)]
    [InlineData(AuditTheme.Seguridad)]
    public void Nada_que_varie_por_unidad_se_cuela_en_el_prefijo(AuditTheme theme)
    {
        const string codigo = "class MarcaUnica { void MetodoInconfundible() {} }";
        ComposedUnitPrompt c = Compose("src/RutaInconfundible.cs", codigo, theme);

        c.StablePrefix.Should().NotContain("RutaInconfundible");
        c.StablePrefix.Should().NotContain("MarcaUnica");
        c.StablePrefix.Should().NotContain("MetodoInconfundible");
        c.StablePrefix.Should().NotContain("01J000000000000000000000AA");
        c.StablePrefix.Should().NotContain("BUG-0001");
        c.StablePrefix.Should().NotContain("SEC-0002");

        // Y lo que sí tiene que estar, está: si el prefijo se quedara vacío el test de arriba
        // pasaría sin significar nada.
        c.StablePrefix.Should().Contain("Eres un auditor de código");
        c.StablePrefix.Should().Contain("RÚBRICA DE SEVERIDAD");
        c.StablePrefix.Should().Contain("PILAR ERRORES");
        c.StablePrefix.Should().Contain("[P-1]", "los patrones silenciados son de la app, no de la unidad");
    }

    /// <summary>
    /// El orden: primero lo cacheable, después lo que cambia. Se comprueba por posiciones dentro
    /// del prompt entero, que es lo que ve el modelo.
    /// </summary>
    [Fact]
    public void Lo_estable_va_delante_y_el_codigo_de_la_unidad_al_final()
    {
        ComposedUnitPrompt c = Compose("src/A.cs", "class A {}", AuditTheme.Seguridad);
        string p = c.Text;

        int reglas = p.IndexOf("Eres un auditor de código", StringComparison.Ordinal);
        int rubrica = p.IndexOf("RÚBRICA DE SEVERIDAD", StringComparison.Ordinal);
        int catalogo = p.IndexOf("PILAR ERRORES", StringComparison.Ordinal);
        int tematica = p.IndexOf("ENFOQUE DEL CICLO", StringComparison.Ordinal);
        int patrones = p.IndexOf("TIPOS DE PROBLEMA SILENCIADOS", StringComparison.Ordinal);
        int existentes = p.IndexOf("HALLAZGOS YA EXISTENTES", StringComparison.Ordinal);
        int unidad = p.IndexOf("<<<UNIT", StringComparison.Ordinal);

        reglas.Should().BeLessThan(rubrica);
        rubrica.Should().BeLessThan(catalogo);
        catalogo.Should().BeLessThan(tematica);
        tematica.Should().BeLessThan(patrones);
        patrones.Should().BeLessThan(existentes);
        existentes.Should().BeLessThan(unidad);

        // La costura cae exactamente donde empieza lo variable.
        c.StablePrefix.Length.Should().Be(existentes);
    }

    /// <summary>
    /// La composición mide el prompt que se manda: sus bloques suman lo mismo que el prompt entero,
    /// salvo el redondeo de contar cada trozo por separado. Un desglose que no cuadre con el total
    /// haría que la línea del informe repartiera tokens que no existen.
    /// </summary>
    [Fact]
    public void La_composicion_cuadra_con_el_prompt_que_se_manda()
    {
        ComposedUnitPrompt c = Compose("src/A.cs", new string('x', 4000), AuditTheme.Rendimiento);

        int entero = PromptTokens.Estimate(c.Text);
        c.Composition.Total.Should().BeCloseTo(entero, 8, "un token por bloque, de redondear cada trozo");
        c.Composition.Estable.Should().BeCloseTo(PromptTokens.Estimate(c.StablePrefix), 8);
        c.Composition.Variable.Should().BeCloseTo(PromptTokens.Estimate(c.UnitPart), 8);
        c.Composition.Unidad.Should().BeGreaterThan(900, "cuatro mil caracteres de código pesan");
        c.Composition.Andamiaje.Should().Be(c.Composition.Total - c.Composition.Unidad);
    }

    /// <summary>
    /// Un ciclo General no escribe bloque de temática, y eso tiene que verse en el desglose: un
    /// número distinto de cero ahí sería andamiaje fantasma en la cuenta.
    /// </summary>
    [Fact]
    public void Con_General_no_hay_bloque_de_tematica()
    {
        Compose("src/A.cs", "class A {}").Composition.Tematica.Should().Be(0);
        Compose("src/A.cs", "class A {}", AuditTheme.Concurrencia).Composition.Tematica.Should().BeGreaterThan(0);
    }
}
