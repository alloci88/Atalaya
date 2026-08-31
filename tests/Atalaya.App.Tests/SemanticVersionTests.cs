using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F8 §3 — la comparación SemVer. Es la regla que decide si a todo el equipo le sale un banner,
/// así que sus casos raros importan: la <c>v</c> del tag, el cuarto número de .NET, los metadatos
/// de build y —el que de verdad protege— que un pre-release sea ANTERIOR a su versión final.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("  1.2.3  ", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    [InlineData("1.2.3.0", 1, 2, 3)]
    [InlineData("2.0", 2, 0, 0)]
    [InlineData("3", 3, 0, 0)]
    public void Parsea_lo_que_de_verdad_llega(string text, int major, int minor, int patch)
    {
        SemanticVersion? v = SemanticVersion.TryParse(text);

        v.Should().NotBeNull();
        (v!.Major, v.Minor, v.Patch).Should().Be((major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("main")]
    [InlineData("release-candidate")]
    [InlineData("—")]
    public void Lo_que_no_es_una_version_devuelve_null(string? text)
        => SemanticVersion.TryParse(text).Should().BeNull();

    [Fact]
    public void El_prerelease_se_separa_del_numero()
    {
        SemanticVersion v = SemanticVersion.TryParse("v1.2.0-rc.1+sha")!;

        v.IsPrerelease.Should().BeTrue();
        v.Prerelease.Should().Be("rc.1");
        v.ToString().Should().Be("1.2.0-rc.1");
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("1.3.0", "1.2.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.10", "1.0.9")]
    public void Mas_nueva_es_mas_nueva(string newer, string older)
        => SemanticVersion.TryParse(newer)!.IsNewerThan(SemanticVersion.TryParse(older))
            .Should().BeTrue();

    [Fact]
    public void La_misma_version_no_es_mas_nueva()
        => SemanticVersion.TryParse("1.2.3")!.IsNewerThan(SemanticVersion.TryParse("v1.2.3"))
            .Should().BeFalse();

    /// <summary>
    /// La regla que impide que un <c>v2.0.0-rc1</c> etiquetado para probar le salte a todo el
    /// equipo como versión disponible: un pre-release es ANTERIOR a su versión final.
    /// </summary>
    [Fact]
    public void Un_prerelease_es_anterior_a_su_version_final()
    {
        SemanticVersion final = SemanticVersion.TryParse("1.2.0")!;
        SemanticVersion rc = SemanticVersion.TryParse("1.2.0-rc.1")!;

        final.IsNewerThan(rc).Should().BeTrue();
        rc.IsNewerThan(final).Should().BeFalse();
    }

    [Fact]
    public void Entre_prereleases_manda_el_orden_de_SemVer()
    {
        SemanticVersion Parse(string s) => SemanticVersion.TryParse(s)!;

        Parse("1.0.0-rc.2").IsNewerThan(Parse("1.0.0-rc.1")).Should().BeTrue();
        Parse("1.0.0-beta").IsNewerThan(Parse("1.0.0-alpha")).Should().BeTrue();
        // Menos identificadores = menor (SemVer §11.4).
        Parse("1.0.0-alpha.1").IsNewerThan(Parse("1.0.0-alpha")).Should().BeTrue();
        // Numérico < alfanumérico.
        Parse("1.0.0-alpha").IsNewerThan(Parse("1.0.0-1")).Should().BeTrue();
    }

    /// <summary>Los metadatos de build no participan en la comparación (SemVer §10).</summary>
    [Fact]
    public void Los_metadatos_de_build_no_cambian_el_orden()
        => SemanticVersion.TryParse("1.2.3+aaa")!
            .IsNewerThan(SemanticVersion.TryParse("1.2.3+zzz"))
            .Should().BeFalse();

    /// <summary>
    /// Una versión se escribe ENTERA, siempre y en un solo sitio (BUGFIX-AVISO).
    /// <para>
    /// Aquí vivía la regla contraria: un <c>Short</c> que dejaba la 1.2.3 en «1.2» para que el
    /// banner dijera el número «que la gente dice en voz alta». Este test la daba por buena, y
    /// por eso el defecto sobrevivió a una release entera — el aviso anunciaba la 1.0.4 como
    /// «1.0», que no es ninguna versión que exista y que además se lee como 1.0.0, o sea más
    /// vieja que la que ya tenías.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_version_se_escribe_entera_y_el_parche_nunca_se_oculta()
    {
        SemanticVersion.TryParse("v1.2.0")!.ToString().Should().Be("1.2.0");
        SemanticVersion.TryParse("v1.2.3")!.ToString().Should().Be("1.2.3");
        SemanticVersion.TryParse("v1.0.4")!.ToString().Should().Be("1.0.4");
        SemanticVersion.TryParse("v1.2.0-rc.1")!.ToString().Should().Be("1.2.0-rc.1");
        // Lo que sí se recorta son los metadatos de build: identifican, no versionan (SemVer §10).
        SemanticVersion.TryParse("1.2.3+abc1234")!.ToString().Should().Be("1.2.3");
        // Y el cuarto número que mete .NET, que en SemVer no existe.
        SemanticVersion.TryParse("1.2.3.0")!.ToString().Should().Be("1.2.3");
    }
}
