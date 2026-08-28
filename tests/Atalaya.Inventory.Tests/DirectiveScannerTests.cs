using FluentAssertions;
using Xunit;

namespace Atalaya.Inventory.Tests;

/// <summary>
/// La detección de directivas por catálogo (F7 §1).
/// <para>
/// El fixture es un proyecto spec-driven creíble: instrucciones de agentes en dos niveles,
/// instrucciones de Copilot, reglas de Cursor en sus dos formatos, ADRs en su carpeta canónica y
/// sueltos, specs, un PRD y una colección de skills. Es el caso de aceptación de «el compañero da
/// de alta su app y el escaneo le propone sus ficheros», escrito como test.
/// </para>
/// </summary>
public sealed class DirectiveScannerTests
{
    private static SyntheticRepo SpecDriven()
        => new SyntheticRepo()
            // --- lo que el catálogo tiene que encontrar ---
            .File("AGENTS.md", "# Convenciones de agente")
            .File("src/paquete/AGENTS.md", "# Convenciones del paquete")
            .File("CLAUDE.md", "# Instrucciones de Claude Code")
            .File(".github/copilot-instructions.md", "# Copilot")
            .File(".github/instructions/csharp.instructions.md", "# C#")
            .File(".cursorrules", "usa siempre records")
            .File(".cursor/rules/estilo.mdc", "# estilo")
            .File(".cursor/rules/anidada/mas.mdc", "# más reglas")
            .File("docs/adr/0001-usamos-postgres.md", "# ADR 1")
            .File("documentacion/ADR-014-colas.md", "# ADR 14")
            .File("docs/PRD-facturacion.md", "# PRD")
            .File("specs/facturacion/emision.md", "# Emisión")
            .File(".claude/skills/revisar/SKILL.md", "# Cómo revisar")

            // --- lo que NO es una directiva y no puede colarse ---
            .File("src/paquete/Servicio.cs", "public class Servicio {}")
            .File("docs/adr/diagrama.png", "no soy texto")
            .File("specs/facturacion/captura.png", "tampoco")
            .File("README.md", "# Léeme")
            .File("node_modules/paquete/AGENTS.md", "instrucciones de una dependencia")
            .File("bin/Debug/CLAUDE.md", "un artefacto de compilación");

    private static IReadOnlyList<string> Paths(DirectiveScanOutput scan)
        => scan.Candidates.Select(c => c.Path).ToList();

    [Fact]
    public void Encuentra_las_instrucciones_de_agente_en_todos_los_niveles()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().Contain("AGENTS.md")
            .And.Contain("src/paquete/AGENTS.md")
            .And.Contain("CLAUDE.md");
    }

    [Fact]
    public void Encuentra_las_instrucciones_de_copilot_y_las_reglas_de_cursor()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().Contain(".github/copilot-instructions.md")
            .And.Contain(".github/instructions/csharp.instructions.md")
            .And.Contain(".cursorrules")
            .And.Contain(".cursor/rules/estilo.mdc")
            .And.Contain(".cursor/rules/anidada/mas.mdc");
    }

    [Fact]
    public void Encuentra_los_adrs_en_su_carpeta_y_los_sueltos()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().Contain("docs/adr/0001-usamos-postgres.md")
            .And.Contain("documentacion/ADR-014-colas.md");
    }

    [Fact]
    public void Encuentra_specs_prds_y_skills()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().Contain("specs/facturacion/emision.md")
            .And.Contain("docs/PRD-facturacion.md")
            .And.Contain(".claude/skills/revisar/SKILL.md");
    }

    /// <summary>
    /// El escaneo de directivas NO usa las exclusiones del inventario. Aquéllas podan
    /// <c>specs</c> y <c>docs</c> porque no son código que auditar, y aquí son justo lo que se
    /// viene a buscar: si alguien las reutilizara «para no duplicar», esta funcionalidad devolvería
    /// una lista vacía en todos los proyectos spec-driven, que son los únicos para los que existe.
    /// </summary>
    [Fact]
    public void Las_carpetas_que_el_inventario_poda_si_se_miran_aqui()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        DefaultExclusions.Patterns.Should().Contain("specs");
        found.Should().Contain("specs/facturacion/emision.md");
    }

    [Fact]
    public void Las_dependencias_y_los_artefactos_de_compilacion_no_son_directivas()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().NotContain("node_modules/paquete/AGENTS.md")
            .And.NotContain("bin/Debug/CLAUDE.md");
    }

    /// <summary>
    /// Un <c>specs/</c> real trae capturas y diagramas junto al texto. Una directiva es TEXTO que
    /// se le enseña a un modelo, así que lo que no se puede leer no se propone.
    /// </summary>
    [Fact]
    public void Los_ficheros_que_no_son_texto_no_se_proponen()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().NotContain("docs/adr/diagrama.png")
            .And.NotContain("specs/facturacion/captura.png");
    }

    [Fact]
    public void El_codigo_y_el_readme_no_son_directivas()
    {
        using SyntheticRepo repo = SpecDriven();

        IReadOnlyList<string> found = Paths(new DirectiveScanner().Scan(repo.Root));

        found.Should().NotContain("src/paquete/Servicio.cs").And.NotContain("README.md");
    }

    /// <summary>El catálogo dice de qué familia es cada cosa: es lo que agrupa el panel.</summary>
    [Theory]
    [InlineData("AGENTS.md", "agents")]
    [InlineData("CLAUDE.md", "claude")]
    [InlineData(".github/copilot-instructions.md", "copilot")]
    [InlineData(".cursorrules", "cursor")]
    [InlineData("docs/adr/0001-usamos-postgres.md", "adr")]
    [InlineData("documentacion/ADR-014-colas.md", "adr")]
    [InlineData("docs/PRD-facturacion.md", "prd")]
    [InlineData("specs/facturacion/emision.md", "spec")]
    [InlineData(".claude/skills/revisar/SKILL.md", "skills")]
    public void Cada_candidato_dice_de_que_familia_es(string path, string kind)
    {
        using SyntheticRepo repo = SpecDriven();

        DirectiveCandidate candidate = new DirectiveScanner().Scan(repo.Root)
            .Candidates.Single(c => c.Path == path);

        candidate.Kind.Should().Be(kind);
        candidate.Why.Should().NotBeEmpty("cada patrón del catálogo explica por qué está ahí");
    }

    [Fact]
    public void Un_repo_sin_directivas_no_propone_nada_y_no_revienta()
    {
        using var repo = new SyntheticRepo().File("src/A.cs", "class A {}");

        DirectiveScanOutput scan = new DirectiveScanner().Scan(repo.Root);

        scan.Candidates.Should().BeEmpty();
        scan.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Sin_carpeta_que_escanear_devuelve_vacio_en_vez_de_reventar()
    {
        var scanner = new DirectiveScanner();

        scanner.Scan(string.Empty).Candidates.Should().BeEmpty();
        scanner.Scan(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N")))
            .Candidates.Should().BeEmpty();
    }

    /// <summary>
    /// Dos máquinas que escanean el mismo commit tienen que proponer la misma lista en el mismo
    /// orden, o el panel parecería cambiar solo entre aperturas.
    /// </summary>
    [Fact]
    public void El_orden_de_los_candidatos_es_estable()
    {
        using SyntheticRepo repo = SpecDriven();
        var scanner = new DirectiveScanner();

        Paths(scanner.Scan(repo.Root)).Should().Equal(Paths(scanner.Scan(repo.Root)));
        Paths(scanner.Scan(repo.Root)).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- el emparejador de rutas

    [Theory]
    [InlineData("**/AGENTS.md", "AGENTS.md", true)]
    [InlineData("**/AGENTS.md", "src/paquete/AGENTS.md", true)]
    [InlineData("**/AGENTS.md", "AGENTS.md.bak", false)]
    [InlineData("**/AGENTS.md", "src/AGENTS.txt", false)]
    [InlineData(".cursorrules", ".cursorrules", true)]
    [InlineData(".cursorrules", "sub/.cursorrules", false)]
    [InlineData(".github/instructions/*.md", ".github/instructions/a.md", true)]
    [InlineData(".github/instructions/*.md", ".github/instructions/sub/a.md", false)]
    [InlineData(".cursor/rules/**", ".cursor/rules/a.mdc", true)]
    [InlineData(".cursor/rules/**", ".cursor/rules/x/y/a.mdc", true)]
    [InlineData(".cursor/rules/**", ".cursor/otra/a.mdc", false)]
    [InlineData("**/ADR-*.md", "ADR-1.md", true)]
    [InlineData("**/ADR-*.md", "docs/x/ADR-014-colas.md", true)]
    [InlineData("**/ADR-*.md", "docs/ADRIANO.md", false)]
    [InlineData("**/PRD*.md", "PRD.md", true)]
    [InlineData("**/PRD*.md", "docs/PRD-facturacion.md", true)]
    [InlineData("specs/**", "specs/a.md", true)]
    [InlineData("specs/**", "src/specs/a.md", false)]
    public void El_emparejador_de_rutas_hace_lo_que_dice(string glob, string path, bool matches)
        => GlobPath.IsMatch(glob, path).Should().Be(matches);

    /// <summary>Los anclajes de un segmento con <c>*</c> no pueden solaparse: «ab*ba» no es «aba».</summary>
    [Fact]
    public void Un_comodin_no_casa_solapando_sus_extremos()
        => GlobPath.IsMatch("ab*ba", "aba").Should().BeFalse();
}
