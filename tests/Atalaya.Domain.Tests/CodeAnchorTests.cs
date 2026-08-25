using Atalaya.Domain.Anchoring;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// Lo que sobrevive del antiguo <c>Fingerprint</c> (F4): normalizar rutas y anclar snippets.
/// Ninguna de las dos tiene que ver con la identidad de un hallazgo — esa es su ULID.
/// </summary>
public sealed class CodeAnchorTests
{
    [Theory]
    [InlineData("src/Db/Pool.cs", "src/Db/Pool.cs")]
    [InlineData("src\\Db\\Pool.cs", "src/Db/Pool.cs")]
    [InlineData("./src//Db/Pool.cs", "src/Db/Pool.cs")]
    [InlineData("  /src/Db/Pool.cs/  ", "src/Db/Pool.cs")]
    public void NormalizePath_canonicalizes_separators_prefixes_and_whitespace(string input, string expected)
        => CodeAnchor.NormalizePath(input).Should().Be(expected);

    [Fact]
    public void NormalizePath_preserves_case_because_git_is_case_sensitive()
        => CodeAnchor.NormalizePath("src/Db/Pool.cs").Should().NotBe(CodeAnchor.NormalizePath("src/db/pool.cs"));

    [Fact]
    public void Snippet_hash_ignores_line_endings_and_trailing_whitespace()
    {
        string a = CodeAnchor.ComputeSnippetHash("var x = 1;\r\nvar y = 2;   ");
        string b = CodeAnchor.ComputeSnippetHash("var x = 1;\nvar y = 2;");

        a.Should().Be(b);
        a.Should().StartWith("sha256:");
    }

    [Fact]
    public void Snippet_hash_distinguishes_different_code()
        => CodeAnchor.ComputeSnippetHash("var x = 1;")
            .Should().NotBe(CodeAnchor.ComputeSnippetHash("var x = 2;"));

    /// <summary>
    /// El defecto que hacía salir el banner de «el código ha cambiado» en TODOS los hallazgos
    /// (F5.6, D-217): al ingerir se hashea el snippet que manda el LLM, <b>sin sangría</b>, y al
    /// mostrar la línea cruda del fichero, <b>con</b> sus doce espacios. Recortando solo por la
    /// derecha, ninguna línea de dentro de una clase podía casar jamás.
    /// </summary>
    [Fact]
    public void Snippet_hash_ignora_la_sangria_porque_las_dos_puntas_no_ven_el_mismo_texto()
        => CodeAnchor.ComputeSnippetHash("            return uint.Parse(detId, NumberStyles.HexNumber);")
            .Should().Be(CodeAnchor.ComputeSnippetHash("return uint.Parse(detId, NumberStyles.HexNumber);"));

    /// <summary>Y la sangría relativa de un bloque tampoco cuenta: cada línea va por su cuenta.</summary>
    [Fact]
    public void Snippet_hash_ignora_la_sangria_linea_a_linea_en_un_bloque()
        => CodeAnchor.ComputeSnippetHash("    if (x)\n        return 1;")
            .Should().Be(CodeAnchor.ComputeSnippetHash("if (x)\nreturn 1;"));

    /// <summary>
    /// Las líneas en blanco de los extremos son ruido de recorte: un snippet que llega con un
    /// salto de línea de más es el mismo snippet.
    /// </summary>
    [Fact]
    public void Snippet_hash_ignora_los_blancos_de_los_extremos()
        => CodeAnchor.ComputeSnippetHash("\n\n  var x = 1;  \n\n")
            .Should().Be(CodeAnchor.ComputeSnippetHash("var x = 1;"));

    /// <summary>
    /// El caso real del hub (D-217): el hash guardado para la ubicación del hallazgo
    /// «ConvertToDetId/ConvertToSeq propagan excepciones de Parse». Se calculó con la
    /// normalización vieja sobre un snippet sin sangría, y tiene que <b>seguir valiendo</b> con la
    /// nueva — si no, arreglar el defecto habría exigido reescribir el hub entero.
    /// </summary>
    [Fact]
    public void Los_hashes_ya_guardados_en_el_hub_siguen_valiendo()
        => CodeAnchor.ComputeSnippetHash("return uint.Parse(detId, NumberStyles.HexNumber);")
            .Should().Be("sha256:2523e49fbf9f68049d123895ea68485ec795b638839c608a886fa0b36fef6792");
}
