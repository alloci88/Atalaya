using Atalaya.Domain.Fingerprinting;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

public class FingerprintTests
{
    [Fact]
    public void Fingerprint_ignores_title_when_symbol_is_known()
    {
        string a = Fingerprint.Compute("errores.recursos.no-liberado", "src/Db/Pool.cs", "Pool.Acquire", "Connection leaked on error path");
        string b = Fingerprint.Compute("errores.recursos.no-liberado", "src/Db/Pool.cs", "Pool.Acquire", "A totally different LLM wording");

        a.Should().Be(b);
        a.Should().StartWith("sha256:");
    }

    [Fact]
    public void Fingerprint_uses_normalized_title_when_no_symbol()
    {
        string a = Fingerprint.Compute("errores.x", "src/A.cs", null, "Leak in Handler 42");
        string b = Fingerprint.Compute("errores.x", "src/A.cs", null, "leak    in   handler   99");

        // digits stripped, whitespace collapsed, lowercased → identical discriminator.
        a.Should().Be(b);
    }

    [Fact]
    public void Fingerprint_differs_by_path()
    {
        string a = Fingerprint.Compute("errores.x", "src/A.cs", "Sym", "t");
        string b = Fingerprint.Compute("errores.x", "src/B.cs", "Sym", "t");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Fingerprint_differs_by_rule()
    {
        string a = Fingerprint.Compute("errores.x", "src/A.cs", "Sym", "t");
        string b = Fingerprint.Compute("errores.y", "src/A.cs", "Sym", "t");

        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData(@"src\Db\Pool.cs", "src/Db/Pool.cs")]
    [InlineData("./src/Db/Pool.cs", "src/Db/Pool.cs")]
    [InlineData("src//Db///Pool.cs", "src/Db/Pool.cs")]
    [InlineData("  src/Db/Pool.cs  ", "src/Db/Pool.cs")]
    public void NormalizePath_canonicalizes(string input, string expected)
    {
        Fingerprint.NormalizePath(input).Should().Be(expected);
    }

    [Fact]
    public void Path_separators_do_not_change_fingerprint()
    {
        string win = Fingerprint.Compute("r", @"src\Db\Pool.cs", "S", "t");
        string nix = Fingerprint.Compute("r", "src/Db/Pool.cs", "S", "t");

        win.Should().Be(nix);
    }

    [Fact]
    public void SnippetHash_is_stable_across_line_endings_and_trailing_space()
    {
        string a = Fingerprint.ComputeSnippetHash("var x = 1;  \r\nreturn x;\r\n");
        string b = Fingerprint.ComputeSnippetHash("var x = 1;\nreturn x;\n");

        a.Should().Be(b);
    }
}
