using Atalaya.Storage.Sync;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// «¿Estas dos URLs son el mismo repositorio?» (F5.8). La regla la usan dos sitios: re-apuntar
/// <c>origin</c> cuando el despliegue mueve el hub, y validar el clon local que un usuario dice
/// tener. Es la misma pregunta, así que tiene que ser la misma respuesta.
/// <para>
/// Lo que se prueba es sobre todo lo que NO puede rechazar: un compañero que clonó por SSH tiene
/// su clon tan bueno como quien clonó por HTTPS, y rechazárselo lo dejaría sin poder auditar por
/// una diferencia de forma.
/// </para>
/// </summary>
public sealed class RemoteUrlTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo")]
    [InlineData("https://github.com/org/repo/", "https://github.com/org/repo")]
    [InlineData("https://GitHub.com/Org/Repo", "https://github.com/org/repo")]
    [InlineData("git@github.com:org/repo.git", "https://github.com/org/repo")]
    [InlineData("ssh://git@github.com/org/repo", "https://github.com/org/repo.git")]
    [InlineData("https://x-access-token:ghs_secreto@github.com/org/repo", "https://github.com/org/repo")]
    [InlineData("C:\\repos\\app", "C:/repos/app")]
    public void El_mismo_repo_escrito_de_varias_formas_es_el_mismo_repo(string a, string b)
        => RemoteUrl.Same(a, b).Should().BeTrue();

    [Theory]
    [InlineData("https://github.com/org/repo", "https://github.com/org/otro")]
    [InlineData("https://github.com/org/repo", "https://github.com/otra-org/repo")]
    [InlineData("https://github.com/org/repo", "https://gitlab.com/org/repo")]
    public void Repos_distintos_no_se_confunden(string a, string b)
        => RemoteUrl.Same(a, b).Should().BeFalse();

    /// <summary>
    /// Dos ausencias no son «el mismo repo». Si lo fueran, una app sin URL en el hub daría por
    /// válido cualquier clon sin remoto — que es exactamente el caso que hay que rechazar.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "https://github.com/org/repo")]
    public void Sin_URL_no_hay_coincidencia(string? a, string? b)
        => RemoteUrl.Same(a, b).Should().BeFalse();

    /// <summary>Un puerto explícito no es la forma scp de SSH y no se parte por los dos puntos.</summary>
    [Fact]
    public void Un_puerto_no_se_confunde_con_la_forma_scp()
    {
        RemoteUrl.Same("ssh://git@host:2222/org/repo", "ssh://git@host:2222/org/repo.git")
            .Should().BeTrue();
        RemoteUrl.Same("ssh://git@host:2222/org/repo", "ssh://git@host/org/repo")
            .Should().BeFalse("el puerto forma parte de la dirección");
    }
}
