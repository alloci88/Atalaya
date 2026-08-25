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
}
