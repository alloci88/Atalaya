using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F17 §1 — <b>el catálogo de temáticas y cómo llega al prompt</b>.
/// <para>
/// Lo que se fija aquí no es el juicio del modelo —eso lo verifica el usuario sobre el banco: un
/// ciclo de Rendimiento tiene que encontrar el doble recorrido y NO las credenciales— sino que el
/// criterio existe, dice lo que busca y lo que no, y viaja entero. Y, con el mismo peso, que con
/// General el prompt NO cambia ni un byte: el ciclo General es el de referencia y se comporta
/// exactamente como antes de F17.
/// </para>
/// </summary>
public class ThemeCatalogTests
{

    [Fact]
    public void El_catalogo_es_cerrado_y_General_va_primero_y_es_la_recomendada()
    {
        ThemeCatalog.All.Should().HaveCount(6);
        ThemeCatalog.All[0].Should().Be(AuditTheme.General);
        ThemeCatalog.Recommended.Should().Be(AuditTheme.General);
        ThemeCatalog.All.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(AuditTheme.Seguridad)]
    [InlineData(AuditTheme.Rendimiento)]
    [InlineData(AuditTheme.Fiabilidad)]
    [InlineData(AuditTheme.Concurrencia)]
    [InlineData(AuditTheme.Mantenibilidad)]
    public void Cada_tematica_dice_que_busca_y_que_no_debe_reportar(AuditTheme theme)
    {
        ThemeCatalog.Looks(theme).Should().NotBeNullOrWhiteSpace().And.Contain("- ", "es una lista de defectos concretos");
        ThemeCatalog.Excludes(theme).Should().Contain("NO reportes");
        ThemeCatalog.Description(theme).Should().EndWith("Nada más.", "la regla dura del enfoque se dice al elegirla");
    }

    /// <summary>General no tiene lista propia: su criterio es el brief entero.</summary>
    [Fact]
    public void General_no_acota_nada()
    {
        ThemeCatalog.Looks(AuditTheme.General).Should().BeEmpty();
        ThemeCatalog.Excludes(AuditTheme.General).Should().BeEmpty();
        ThemeSection.Render(AuditTheme.General).Should().BeEmpty();
    }

    /// <summary>
    /// Lo que la aceptación humana va a mirar en el banco: Rendimiento busca el doble recorrido y
    /// se calla las credenciales y los nulos; Seguridad busca las credenciales y se calla el coste.
    /// </summary>
    [Fact]
    public void Rendimiento_busca_el_doble_recorrido_y_se_calla_credenciales_y_nulos()
    {
        ThemeCatalog.Looks(AuditTheme.Rendimiento).Should().Contain("doble recorrido");
        ThemeCatalog.Excludes(AuditTheme.Rendimiento).Should().Contain("credenciales").And.Contain("nulos");
    }

    [Fact]
    public void Seguridad_busca_las_credenciales_y_se_calla_el_rendimiento()
    {
        ThemeCatalog.Looks(AuditTheme.Seguridad).Should().Contain("Secretos y credenciales");
        ThemeCatalog.Excludes(AuditTheme.Seguridad).Should().Contain("rendimiento");
    }

    // ---------------------------------------------------------------- el prompt

    private static string Prompt(AuditTheme theme, IReadOnlyList<ExistingFinding>? existing = null,
        IReadOnlyList<ExistingFinding>? offTheme = null)
        => PromptComposer.ComposeUnitPrompt(
            "src/A.cs", "class A {}", PillarBrief.For(TechStack.DotNet), AuditMode.Lotes,
            existing, theme: theme, offTheme: offTheme);

    /// <summary>
    /// El anti-objetivo de F17, byte a byte: con General el prompt es EXACTAMENTE el de antes. Se
    /// compara con la llamada que no sabe nada de temáticas.
    /// </summary>
    [Fact]
    public void Con_General_el_prompt_no_cambia_ni_un_byte()
    {
        string before = PromptComposer.ComposeUnitPrompt(
            "src/A.cs", "class A {}", PillarBrief.For(TechStack.DotNet), AuditMode.Lotes);

        Prompt(AuditTheme.General).Should().Be(before);
        Prompt(AuditTheme.General).Should().NotContain(ThemeSection.Heading);
    }

    [Fact]
    public void El_prompt_de_un_ciclo_tematico_lleva_el_enfoque_con_sus_exclusiones()
    {
        string p = Prompt(AuditTheme.Rendimiento);

        p.Should().Contain($"{ThemeSection.Heading}: RENDIMIENTO.");
        p.Should().Contain("fuera de");
        p.Should().Contain("NO SE REPORTA NADA");
        p.Should().Contain(ThemeCatalog.Looks(AuditTheme.Rendimiento).TrimEnd());
        p.Should().Contain(ThemeCatalog.Excludes(AuditTheme.Rendimiento).TrimEnd());
        p.Should().Contain("RECONCILIACIÓN ACOTADA");
    }

    /// <summary>La temática NO cambia la rúbrica: el prompt temático la sigue llevando entera.</summary>
    [Fact]
    public void El_prompt_tematico_sigue_llevando_la_rubrica_de_severidad_entera()
        => Prompt(AuditTheme.Seguridad).Should().Contain(SeverityRubric.Text);

    /// <summary>El enfoque va detrás del brief (cómo se clasifica) y antes del contenido (qué se mira).</summary>
    [Fact]
    public void El_enfoque_va_entre_el_brief_y_la_unidad()
    {
        string p = Prompt(AuditTheme.Fiabilidad);

        p.IndexOf(ThemeSection.Heading, StringComparison.Ordinal).Should()
            .BeGreaterThan(p.IndexOf("BRIEF DE AUDITOR", StringComparison.Ordinal))
            .And.BeLessThan(p.IndexOf("UNIDAD: src/A.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Los_existentes_de_la_tematica_se_reconcilian_y_los_de_otras_se_ensenan_sin_juzgar()
    {
        var mine = new ExistingFinding("01MINE", "PERF-1", "Doble recorrido", "media", "src/A.cs:10", "activo", "Rendimiento");
        var foreign = new ExistingFinding("01FOREIGN", "SEC-1", "Credencial en claro", "critica", "src/A.cs:3", "activo", "Seguridad");

        string p = Prompt(AuditTheme.Rendimiento, new[] { mine }, new[] { foreign });

        p.Should().Contain("HALLAZGOS YA EXISTENTES DE TU TEMÁTICA (Rendimiento) EN src/A.cs");
        p.Should().Contain("findingId: 01MINE");
        p.Should().Contain("HALLAZGOS DE OTRAS TEMÁTICAS EN src/A.cs (NO los juzgues ni los re-reportes");
        p.Should().Contain("01FOREIGN").And.Contain("temática: Seguridad");
        p.Should().NotContain("findingId: 01FOREIGN", "el de otra temática no se lista como reconciliable");
    }

    [Fact]
    public void Sin_hallazgos_de_otras_tematicas_no_se_escribe_el_bloque()
        => Prompt(AuditTheme.Rendimiento).Should().NotContain("HALLAZGOS DE OTRAS TEMÁTICAS");

    /// <summary>El catálogo se cita, no se copia: la lista de una temática está una sola vez en el prompt.</summary>
    [Fact]
    public void El_criterio_de_la_tematica_no_esta_escrito_dos_veces()
    {
        string p = Prompt(AuditTheme.Concurrencia);
        string looks = ThemeCatalog.Looks(AuditTheme.Concurrencia).TrimEnd();

        CountOf(p, looks).Should().Be(1);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
